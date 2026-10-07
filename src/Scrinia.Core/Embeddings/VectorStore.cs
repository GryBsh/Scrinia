using System.Collections.Concurrent;
using Scrinia.Core;
using Scrinia.Core.Embeddings.Models;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Per-scope binary vector storage with atomic SVF3 snapshot publication.
///
/// SVF1 (legacy, read-only):
///   [magic "SVF1" 4B] [dimensions uint16] [count uint32]
///   then count entries: [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1 = null)] [vector float32[dims]]
///
/// SVF2 (legacy append-only, still read for backward-compat):
///   [magic "SVF2" 4B] [dimensions uint16]
///   then appendable entries:
///     [op byte: 0=add, 1=delete] [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1 = null)]
///     (for add only: [vector float32[dims]])
///
/// SVF3 (current, signed):
///   [magic "SVF3" 4B] [dimensions uint16] [signatureLen uint16] [signatureUtf8]
///   then appendable entries (same shape as SVF2).
///   The signature captures the embedding provider + model that produced the vectors
///   (e.g. "ollama:nomic-embed-text"). When <see cref="VectorStore"/> is constructed with
///   an expected signature, files whose stored signature differs are quarantined as
///   <c>vectors.bin.stale-{timestamp}</c> and the store starts empty — the caller
///   ({c}WorkspaceSetup{c} / reindex command) then rebuilds vectors from artifacts.
///
/// Each persistent mutation rewrites the live entries under one exclusive scope file lease.
/// Ephemeral scope vectors are stored in-memory only.
/// Persistent scopes write to {baseDir}/{scope}/vectors.bin using atomic replacement.
/// </summary>
public sealed class VectorStore : IDisposable, IVectorStore
{
    /// <summary>Health observed for one explicitly requested vector scope.</summary>
    public sealed record ScopeProbeResult(
        string Scope, bool HasVectorFile, bool IsStale, IReadOnlyList<VectorEntry> Vectors);

    private static readonly byte[] MagicSvf1 = "SVF1"u8.ToArray();
    private static readonly byte[] MagicSvf2 = "SVF2"u8.ToArray();
    private static readonly byte[] MagicSvf3 = "SVF3"u8.ToArray();
    private readonly string _baseDir;
    private readonly string? _expectedSignature;
    // Copy-on-write: the published list for a scope is never mutated after it is stored here.
    // Mutating operations (Upsert/Remove) build a new list and swap it in under the scope
    // lock; GetVectors can therefore hand out the cached reference directly with no lock and
    // no copy — readers can never observe a torn/in-progress mutation.
    private readonly ConcurrentDictionary<string, IReadOnlyList<VectorEntry>> _scopeVectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scopeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _staleQuarantines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test-only fault injection point for the atomic full-rewrite boundary.</summary>
    private static readonly AsyncLocal<Func<string, Exception?>?> RewriteFailure = new();

    internal static Func<string, Exception?>? RewriteFailureInjector
    {
        get => RewriteFailure.Value;
        set => RewriteFailure.Value = value;
    }

    /// <summary>
    /// True if any scope's vector file was quarantined because its signature did not match
    /// <see cref="ExpectedSignature"/>. Callers use this to decide whether to run a reindex
    /// after construction. Cleared once observed by the caller.
    /// </summary>
    public bool HasStaleQuarantines => !_staleQuarantines.IsEmpty;

    /// <summary>Snapshot of scopes whose files were quarantined this session.</summary>
    public IReadOnlyCollection<string> StaleQuarantineScopes => [.. _staleQuarantines.Keys];

    /// <summary>
    /// Signature this store expects to find in vector files. <c>null</c> disables signature
    /// checking — used by tests and callers that don't need migration safety. Production
    /// callers should always pass the active provider's <c>Signature</c>.
    /// </summary>
    public string? ExpectedSignature => _expectedSignature;

    public VectorStore(string baseDir, string? expectedSignature = null)
    {
        _baseDir = baseDir;
        _expectedSignature = expectedSignature;
    }

    private SemaphoreSlim GetLock(string scope) => _scopeLocks.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));

    private static string GetVectorLockPath(string vectorPath) => vectorPath + ".lock";

    /// <summary>Loads vectors for a scope (from disk if persistent, from cache if already loaded).</summary>
    public IReadOnlyList<VectorEntry> GetVectors(string scope)
    {
        // No lock needed: published lists are immutable snapshots (copy-on-write), so handing
        // out the cached reference directly is safe even while a concurrent Upsert/Remove is
        // building its replacement list under the scope lock.
        if (_scopeVectors.TryGetValue(scope, out var cached))
            return cached;

        var lk = GetLock(scope);
        if (!lk.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("VectorStore lock acquisition timed out after 30 seconds.");
        try
        {
            // Double-check after lock
            if (_scopeVectors.TryGetValue(scope, out cached))
                return cached;

            var loaded = TryLoadFromDisk(scope);
            // Never cache a failed load: this store is a DI singleton with no invalidation
            // path, so a cached empty result would outlive the transient failure that caused it.
            if (!loaded.IsAuthoritative)
                return loaded.Vectors;

            _scopeVectors[scope] = loaded.Vectors;
            return loaded.Vectors;
        }
        finally
        {
            lk.Release();
        }
    }

    public IReadOnlyList<VectorEntry> GetCandidateVectors(
        string canonicalScope, IReadOnlyCollection<string> candidateNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalScope);
        ArgumentNullException.ThrowIfNull(candidateNames);
        if (candidateNames.Count == 0)
            return [];

        var allowedNames = candidateNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_scopeVectors.TryGetValue(canonicalScope, out var cached))
            return cached.Where(vector => allowedNames.Contains(vector.Name)).ToArray();

        var lk = GetLock(canonicalScope);
        if (!lk.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("VectorStore lock acquisition timed out after 30 seconds.");
        try
        {
            if (_scopeVectors.TryGetValue(canonicalScope, out cached))
                return cached.Where(vector => allowedNames.Contains(vector.Name)).ToArray();

            if (MemoryArtifactSource.IsEphemeral(canonicalScope))
                return [];

            string path = GetFilePath(canonicalScope);
            using var lease = FileLock.AcquireExclusive(GetVectorLockPath(path));
            MigrateLegacyScopeFileUnderLock(canonicalScope, path);
            if (!ScopeFileExists(path))
                return [];

            return FileRetry.Run(() => LoadCandidateVectorsUnderLock(canonicalScope, path, allowedNames));
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>
    /// Probes exactly the supplied canonical scopes for vector-file health.
    /// Missing scopes are returned as empty snapshots; existing files are loaded through the
    /// same migration, locking, signature validation, and stale quarantine path as
    /// <see cref="GetVectors"/>. This method never discovers scopes from the vector directory.
    /// </summary>
    public IReadOnlyCollection<string> ProbeScopes(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var probes = ProbeScopesDetailed(scopes);

        return probes
            .Where(probe => probe.IsStale)
            .Select(probe => probe.Scope)
            .ToArray();
    }

    /// <summary>
    /// Probes only the supplied canonical scopes and reports missing, healthy, and quarantined
    /// files without promoting unrelated files discovered on disk into the result set.
    /// </summary>
    public IReadOnlyList<ScopeProbeResult> ProbeScopesDetailed(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(scope =>
            {
                var vectors = GetVectors(scope);
                bool isStale = _staleQuarantines.ContainsKey(scope);
                return new ScopeProbeResult(scope, File.Exists(GetFilePath(scope)), isStale, vectors);
            })
            .ToArray();
    }

    /// <summary>Adds or updates vectors for a named memory in a scope.</summary>
    public Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct = default) =>
        MutateScopeAsync(scope, current =>
        {
            var replacement = current.Where(entry =>
                !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) || entry.ChunkIndex != chunkIndex).ToList();
            replacement.Add(new VectorEntry(name, chunkIndex, vector));
            return replacement;
        }, ct);

    /// <summary>Removes all vectors for a named memory in a scope.</summary>
    public Task RemoveAsync(string scope, string name, CancellationToken ct = default) =>
        MutateScopeAsync(scope, current =>
        {
            var replacement = current.Where(entry => !entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return replacement.Count == current.Count ? null : replacement;
        }, ct);

    /// <summary>
    /// Replaces every vector for a memory in one atomic publication. Repeated chunk indices
    /// use the last supplied value, matching sequential upserts. Persistent mutations reload
    /// the scope while holding its exclusive file lease and publish the cache only after disk.
    /// </summary>
    public Task ReplaceMemoryVectorsAsync(
        string scope, string name, IReadOnlyList<(int? ChunkIndex, float[] Vector)> vectors, CancellationToken ct = default) =>
        MutateScopeAsync(scope, current =>
        {
            var replacement = current.Where(entry => !entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (replacement.Count == current.Count && vectors.Count == 0)
                return null;

            // The format reserves -1 for a null chunk index. Preserve last-write-wins
            // deduplication and the first occurrence's position within the batch.
            var dedupedAt = new Dictionary<int, int>();
            foreach (var (chunkIndex, vector) in vectors)
            {
                int key = chunkIndex ?? -1;
                var entry = new VectorEntry(name, chunkIndex, vector);
                if (dedupedAt.TryGetValue(key, out int index))
                    replacement[index] = entry;
                else
                {
                    dedupedAt[key] = replacement.Count;
                    replacement.Add(entry);
                }
            }
            return replacement;
        }, ct);

    private async Task MutateScopeAsync(string scope,
        Func<IReadOnlyList<VectorEntry>, List<VectorEntry>?> mutation, CancellationToken ct)
    {
        var gate = GetLock(scope);
        await gate.WaitAsync(ct);
        try
        {
            if (MemoryArtifactSource.IsEphemeral(scope))
            {
                var current = _scopeVectors.TryGetValue(scope, out var cached) ? cached : [];
                if (mutation(current) is { } next)
                    _scopeVectors[scope] = next;
                return;
            }

            string path = GetFilePath(scope);
            using var lease = FileLock.AcquireExclusive(GetVectorLockPath(path), TimeSpan.FromSeconds(5), ct);
            var loaded = TryLoadFromDiskUnderLock(scope, path);
            if (!loaded.IsAuthoritative)
                throw new VectorStoreUnavailableException(scope, path);

            var replacement = mutation(loaded.Vectors);
            if (replacement is null)
            {
                _scopeVectors[scope] = loaded.Vectors;
                return;
            }

            ct.ThrowIfCancellationRequested();
            await SaveAsSvf3UnderLockAsync(scope, path, replacement, ct);
            _scopeVectors[scope] = replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns the count of vectors across all loaded scopes.</summary>
    public int Count() => _scopeVectors.Values.Sum(v => v.Count);

    private string GetFilePath(string scope)
    {
        string relative = ScopeToRelativeDir(scope);
        string candidate = Path.Combine(_baseDir, relative, "vectors.bin");

        // Fail-fast containment guard before any filesystem touch. PathContainment resolves
        // symlinks/junctions and trailing-separator edge cases; fall back to GetFullPath
        // StartsWith if containment reports false due to non-existent path.
        string fullBase = Path.GetFullPath(_baseDir);
        string fullCandidate = Path.GetFullPath(candidate);
        bool within = PathContainment.IsWithin(_baseDir, candidate);
        if (!within)
        {
            // PathContainment returns false for traversal that escapes baseDir even when the
            // trail does not exist yet — deny closed.
            throw new ArgumentException($"CROSS_VECTOR_PATH_FORBIDDEN: scope '{scope}' escapes baseDir", nameof(scope));
        }

        // Defense-in-depth: also verify lexical containment.
        string withSep = fullBase.EndsWith(Path.DirectorySeparatorChar) ? fullBase : fullBase + Path.DirectorySeparatorChar;
        if (!fullCandidate.Equals(fullBase, StringComparison.OrdinalIgnoreCase) &&
            !fullCandidate.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"CROSS_VECTOR_PATH_FORBIDDEN: scope '{scope}' escapes baseDir", nameof(scope));
        }

        return candidate;
    }

    /// <summary>
    /// Maps a scope to its storage directory. Separators become real nested directories so
    /// <c>'_'</c> stays a literal character — replacing every separator with <c>'_'</c> is not
    /// injective, and collapsed <c>local:foo/bar</c> and <c>local:foo_bar</c> onto one file.
    /// </summary>
    private static string ScopeToRelativeDir(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must not be null, empty, or whitespace.", nameof(scope));

        // Absolute-path guard before any Path.Combine — fail fast, no filesystem touch.
        if (Path.IsPathRooted(scope) || scope.StartsWith('/') || scope.StartsWith('\\'))
            throw new ArgumentException($"CROSS_VECTOR_PATH_FORBIDDEN: scope '{scope}' escapes baseDir", nameof(scope));

        // Windows drive-letter absolute (e.g. C:\windows or C:/windows) — Path.IsPathRooted
        // is false for this on Linux, so check explicitly.
        if (scope.Length >= 2 && char.IsLetter(scope[0]) && scope[1] == ':' )
            throw new ArgumentException($"CROSS_VECTOR_PATH_FORBIDDEN: scope '{scope}' escapes baseDir", nameof(scope));

        // Backslash is a path separator in VectorStore's scope mapping; reject bare backslash
        // abuse that would otherwise be sanitized away.
        string[] rawSegments = scope.Split([':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in rawSegments)
        {
            if (seg == ".." || seg == ".")
                throw new ArgumentException($"CROSS_VECTOR_PATH_FORBIDDEN: scope '{scope}' escapes baseDir", nameof(scope));
            if (Path.IsPathRooted(seg))
                throw new ArgumentException($"Scope segment '{seg}' is not allowed.", nameof(scope));
        }

        // Validate through the single scope constructor before any Path.Combine.
        // Ephemeral is allowed as-is; everything else must be buildable without rewriting
        // the caller's identity into a different scope.
        if (!MemoryArtifactSource.IsEphemeral(scope))
        {
            string topic = scope.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
                ? scope["local:".Length..]
                : scope.Equals("local", StringComparison.OrdinalIgnoreCase) ? "" : scope;

            if (topic.Length > 0)
            {
                // Single constructor is the validation gate; SanitizeSegment is not.
                if (!MemoryNaming.TryBuildScopedTopicScope(topic, out string normalized))
                    throw new ArgumentException($"Scope '{scope}' is not a valid scope.", nameof(scope));
                string suppliedTopic = string.Join('/', topic.Split('/', StringSplitOptions.RemoveEmptyEntries));
                if (!string.Equals(normalized["local:".Length..], suppliedTopic, StringComparison.Ordinal))
                    throw new ArgumentException($"Scope '{scope}' is not a portable scope.", nameof(scope));
                // Reject any segment that after sanitization still contains traversal tokens
                foreach (var seg in normalized.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string sanitized = SanitizeSegment(seg);
                    if (sanitized.Contains("..", StringComparison.Ordinal) || sanitized.Contains(':') || Path.IsPathRooted(seg))
                        throw new ArgumentException($"Scope segment '{seg}' is not allowed.", nameof(scope));
                }
            }
            else if (!scope.Equals("local", StringComparison.OrdinalIgnoreCase) && !MemoryArtifactSource.IsEphemeral(scope))
            {
                throw new ArgumentException($"Scope '{scope}' is not a valid scope.", nameof(scope));
            }
        }

        string[] segments = scope.Split([':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return SanitizeSegment(scope);
        return Path.Combine([.. segments.Select(SanitizeSegment)]);
    }

    /// <summary>
    /// Makes one scope segment safe as a directory name. A short hash is appended whenever
    /// sanitization changes the segment, so <c>a*b</c> and <c>a_b</c> stay distinct.
    ///
    /// <para><b>A deliberate NON-participant in the one scope constructor</b> (Task 13). This is a
    /// third sanitizer, and it is intentionally different from
    /// <see cref="MemoryNaming.TrySanitizeSegment"/>: that one is deliberately lossy (<c>a*b</c>
    /// and <c>a_b</c> both become <c>a_b</c>, matching what the memory store has always done to
    /// subject names), whereas this one must be INJECTIVE or two scopes would share one
    /// <c>vectors.bin</c> and silently merge their embeddings. The hash suffix is what buys that,
    /// so it cannot simply defer to the shared sanitizer.</para>
    ///
    /// <para>It also carried the same trailing-dot aliasing — <c>local:topic.</c> and
    /// <c>local:topic</c> produced two relative dirs that Windows resolved to one — and Task 13
    /// closed that incidentally rather than here: the scope reaching this method is now
    /// PRE-VALIDATED upstream, because the one constructor can no longer emit a segment with a
    /// trailing dot or space, a bare <c>.</c>, or <c>NUL</c>. This method is not the guard; it
    /// relies on there being exactly one way a scope string is built.</para>
    /// </summary>
    private static string SanitizeSegment(string segment)
    {
        string safe = string.Concat(segment.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.Equals(safe, segment, StringComparison.Ordinal))
            return safe;
        return $"{safe}-{StableHash(segment)}";
    }

    private static string StableHash(string value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Moves a legacy flat scope file under the caller's exclusive destination lease.</summary>
    private void MigrateLegacyScopeFileUnderLock(string scope, string path)
    {
        if (ScopeFileExists(path))
            return;

        string legacyDir = Path.Combine(_baseDir, scope.Replace(':', '_').Replace('/', '_').Replace('\\', '_'));
        string legacyPath = Path.Combine(legacyDir, "vectors.bin");
        if (string.Equals(legacyPath, path, StringComparison.OrdinalIgnoreCase) || !ScopeFileExists(legacyPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileRetry.Run(() => File.Move(legacyPath, path, overwrite: false));
        // Directory cleanup is optional; a successful migration remains authoritative.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacyDir).Any())
                Directory.Delete(legacyDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // File.Exists hides permission and I/O failures. Only absence is an empty baseline.
    private static bool ScopeFileExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>Loads vectors from SVF1/SVF2/SVF3. On SVF3 signature mismatch with
    /// <see cref="ExpectedSignature"/>, the file is quarantined to a timestamped <c>.stale</c>
    /// path and an empty list is returned so the caller can trigger a reindex.
    ///
    /// <para>Use <see cref="TryLoadFromDisk"/> on any path that writes back — this overload
    /// reports "no vectors" and "could not read them" as the same empty list.</para>
    /// </summary>
    internal List<VectorEntry> LoadFromDisk(string scope) => TryLoadFromDisk(scope).Vectors;

    /// <summary>
    /// Loads vectors for a scope, reporting whether the result is authoritative.
    ///
    /// <para>Retries transient failures the same way the write paths do, and reports
    /// <see cref="VectorLoadStatus.Failed"/> only once retries are exhausted.</para>
    /// </summary>
    internal VectorLoadResult TryLoadFromDisk(string scope)
    {
        if (MemoryArtifactSource.IsEphemeral(scope))
            return VectorLoadResult.Missing;

        string path = GetFilePath(scope);
        try
        {
            // Reads can migrate or quarantine, so take exclusive ownership from the outset.
            // Parsing and all decisions about the live path happen after admission.
            using var lease = FileLock.AcquireExclusive(GetVectorLockPath(path));
            return TryLoadFromDiskUnderLock(scope, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return ReportUnavailable(scope, ex);
        }
    }

    private VectorLoadResult TryLoadFromDiskUnderLock(string scope, string path)
    {
        try
        {
            MigrateLegacyScopeFileUnderLock(scope, path);
            if (!ScopeFileExists(path))
                return VectorLoadResult.Missing;

            return FileRetry.Run(() => new VectorLoadResult(VectorLoadStatus.Loaded, ReadScopeFileUnderLock(scope, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException)
        {
            return ReportUnavailable(scope, ex);
        }
    }

    private static VectorLoadResult ReportUnavailable(string scope, Exception exception)
    {
        Console.Error.WriteLine(
            $"scrinia: could not read vectors for scope '{scope}' ({exception.GetType().Name}: {exception.Message}). " +
            "Leaving the on-disk file untouched; embedding writes for this scope will fail until it is readable.");
        return VectorLoadResult.Failed;
    }

    private List<VectorEntry> ReadScopeFileUnderLock(string scope, string path)
    {
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            byte[] magic = reader.ReadBytes(4);

            if (magic.AsSpan().SequenceEqual(MagicSvf1))
            {
                if (_expectedSignature is not null)
                {
                    // Pre-signature format — we can't verify these vectors match the current
                    // provider, and silently re-saving them as SVF3 with the current
                    // signature would label them with the wrong identity. Quarantine and
                    // let the caller reindex. One-time cost on upgrade.
                    reader.Dispose();
                    fs.Dispose();
                    QuarantineStaleFile(scope, path, "unsigned (SVF1)");
                    return [];
                }
                return LoadSvf1(reader);
            }

            if (magic.AsSpan().SequenceEqual(MagicSvf2))
            {
                if (_expectedSignature is not null)
                {
                    reader.Dispose();
                    fs.Dispose();
                    QuarantineStaleFile(scope, path, "unsigned (SVF2)");
                    return [];
                }
                return LoadSvf2(reader);
            }

            if (magic.AsSpan().SequenceEqual(MagicSvf3))
            {
                ushort dims = reader.ReadUInt16();
                ushort sigLen = reader.ReadUInt16();
                string fileSignature = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(sigLen));

                if (_expectedSignature is not null
                    && !string.Equals(fileSignature, _expectedSignature, StringComparison.Ordinal))
                {
                    // Signature mismatch — provider changed since these vectors were written.
                    // Quarantine the file (so it's recoverable via filename if the user switches
                    // back), record the scope in _staleQuarantines so the caller can reindex.
                    // Must release the file handle before renaming on Windows.
                    reader.Dispose();
                    fs.Dispose();
                    QuarantineStaleFile(scope, path, fileSignature);
                    return [];
                }

                return LoadSvf3Body(reader, dims);
            }

            throw new InvalidDataException("Unsupported vector file format.");
        }
    }

    private List<VectorEntry> LoadCandidateVectorsUnderLock(
        string scope, string path, HashSet<string> allowedNames)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);
        byte[] magic = reader.ReadBytes(4);
        ushort dims;
        if (magic.AsSpan().SequenceEqual(MagicSvf1))
        {
            if (_expectedSignature is not null)
            {
                reader.Dispose();
                fs.Dispose();
                QuarantineStaleFile(scope, path, "unsigned (SVF1)");
                return [];
            }

            dims = reader.ReadUInt16();
            uint count = reader.ReadUInt32();
            var entries = new List<VectorEntry>();
            for (uint i = 0; i < count; i++)
            {
                ushort nameLength = reader.ReadUInt16();
                string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                int chunk = reader.ReadInt32();
                if (allowedNames.Contains(name))
                {
                    float[] vector = new float[dims];
                    for (int dimension = 0; dimension < dims; dimension++)
                        vector[dimension] = reader.ReadSingle();
                    entries.Add(new VectorEntry(name, chunk == -1 ? null : chunk, vector));
                }
                else
                {
                    reader.BaseStream.Seek(sizeof(float) * dims, SeekOrigin.Current);
                }
            }
            return entries;
        }

        if (!magic.AsSpan().SequenceEqual(MagicSvf2) && !magic.AsSpan().SequenceEqual(MagicSvf3))
            throw new InvalidDataException("Unsupported vector file format.");

        dims = reader.ReadUInt16();
        if (magic.AsSpan().SequenceEqual(MagicSvf3))
        {
            ushort signatureLength = reader.ReadUInt16();
            string signature = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(signatureLength));
            if (_expectedSignature is not null && !string.Equals(signature, _expectedSignature, StringComparison.Ordinal))
            {
                reader.Dispose();
                fs.Dispose();
                QuarantineStaleFile(scope, path, signature);
                return [];
            }
        }

        return ReadCandidateEntryOps(reader, dims, allowedNames);
    }

    private static List<VectorEntry> ReadCandidateEntryOps(
        BinaryReader reader, ushort dims, HashSet<string> allowedNames)
    {
        var entries = new Dictionary<string, VectorEntry>(StringComparer.OrdinalIgnoreCase);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte op = reader.ReadByte();
            ushort nameLength = reader.ReadUInt16();
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            int chunkValue = reader.ReadInt32();
            string key = $"{name}|{chunkValue}";
            bool wanted = allowedNames.Contains(name);
            if (op == 0)
            {
                if (wanted)
                {
                    float[] vector = new float[dims];
                    for (int dimension = 0; dimension < dims; dimension++)
                        vector[dimension] = reader.ReadSingle();
                    entries[key] = new VectorEntry(name, chunkValue == -1 ? null : chunkValue, vector);
                }
                else
                {
                    reader.BaseStream.Seek(sizeof(float) * dims, SeekOrigin.Current);
                }
            }
            else if (op == 1)
            {
                entries.Remove(key);
            }
            else
            {
                throw new InvalidDataException("Vector file contains an unknown operation.");
            }
        }

        return entries.Values.ToList();
    }

    private void QuarantineStaleFile(string scope, string path, string oldSignature)
    {
        try
        {
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            string stalePath = $"{path}.stale-{stamp}-{Guid.NewGuid():N}";
            File.Move(path, stalePath, overwrite: false);
            _staleQuarantines[scope] = 0;
            Console.Error.WriteLine(
                $"[scrinia:info] Vector file for scope '{scope}' was built with embedding " +
                $"signature '{oldSignature}' but the active provider is '{_expectedSignature}'. " +
                $"Quarantined to {Path.GetFileName(stalePath)}; rebuilding via reindex.");
        }
        catch (Exception ex)
        {
            // Refuse a write baseline if the stale source could not be preserved.
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to quarantine stale vector file for scope '{scope}': " +
                $"{ex.GetType().Name}: {ex.Message}");
            _staleQuarantines[scope] = 0;
            throw;
        }
    }

    private static List<VectorEntry> LoadSvf1(BinaryReader reader)
    {
        ushort dims = reader.ReadUInt16();
        uint count = reader.ReadUInt32();

        var entries = new List<VectorEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            ushort nameLen = reader.ReadUInt16();
            string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            int chunkIdx = reader.ReadInt32();
            int? chunkIndex = chunkIdx == -1 ? null : chunkIdx;

            float[] vector = new float[dims];
            for (int d = 0; d < dims; d++)
                vector[d] = reader.ReadSingle();

            entries.Add(new VectorEntry(name, chunkIndex, vector));
        }

        return entries;
    }

    private static List<VectorEntry> LoadSvf2(BinaryReader reader)
    {
        ushort dims = reader.ReadUInt16();
        return ReadEntryOps(reader, dims);
    }

    /// <summary>Reads SVF3 entry ops once the caller has consumed the dims+signature header.</summary>
    private static List<VectorEntry> LoadSvf3Body(BinaryReader reader, ushort dims) =>
        ReadEntryOps(reader, dims);

    /// <summary>Shared op-loop used by SVF2 and SVF3 readers — they have identical entry shape.</summary>
    private static List<VectorEntry> ReadEntryOps(BinaryReader reader, ushort dims)
    {
        var entries = new Dictionary<string, VectorEntry>(StringComparer.OrdinalIgnoreCase);

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            try
            {
                byte op = reader.ReadByte();
                ushort nameLen = reader.ReadUInt16();
                string name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                int chunkIdx = reader.ReadInt32();
                int? chunkIndex = chunkIdx == -1 ? null : chunkIdx;
                string key = $"{name}|{chunkIdx}";

                if (op == 0) // Add
                {
                    float[] vector = new float[dims];
                    for (int d = 0; d < dims; d++)
                        vector[d] = reader.ReadSingle();
                    entries[key] = new VectorEntry(name, chunkIndex, vector);
                }
                else // Delete
                {
                    entries.Remove(key);
                }
            }
            catch (EndOfStreamException)
            {
                // Torn trailing record — e.g. a crash/power-loss mid-append left a partial
                // record at the end of the file. Stop here and keep everything parsed so
                // far rather than letting the whole-file catch in LoadFromDisk discard the
                // entire scope over a single truncated tail.
                break;
            }
        }

        return entries.Values.ToList();
    }

    /// <summary>Publishes a complete SVF3 snapshot under the caller's exclusive file lease.</summary>
    private async Task SaveAsSvf3UnderLockAsync(string scope, string path, List<VectorEntry> vectors, CancellationToken ct)
    {
        // Serialize completely before opening/truncating any file. Encoding or payload
        // failures therefore cannot leave even a partial temporary publication behind.
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true);
            byte[] signature = System.Text.Encoding.UTF8.GetBytes(_expectedSignature ?? "");
            writer.Write(MagicSvf3);
            writer.Write(vectors.Count > 0 ? (ushort)vectors[0].Vector.Length : (ushort)0);
            writer.Write((ushort)signature.Length);
            writer.Write(signature);
            foreach (var entry in vectors)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write((byte)0);
                byte[] name = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((ushort)name.Length);
                writer.Write(name);
                writer.Write(entry.ChunkIndex ?? -1);
                foreach (float value in entry.Vector)
                    writer.Write(value);
            }
            writer.Flush();
            payload = buffer.ToArray();
        }

        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            // Each retry recreates the temporary snapshot; no append can replay a partial op.
            await FileRetry.RunAsync(() => File.WriteAllBytesAsync(tmp, payload, ct), ct: ct);
            if (RewriteFailureInjector?.Invoke(path) is { } injectedFailure)
                throw injectedFailure;

            ct.ThrowIfCancellationRequested();
            FileRetry.Run(() => File.Move(tmp, path, overwrite: true));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public void Dispose()
    {
        foreach (var lk in _scopeLocks.Values)
            lk.Dispose();
    }
}
