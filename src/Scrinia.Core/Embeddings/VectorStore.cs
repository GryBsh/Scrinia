using System.Collections.Concurrent;
using Scrinia.Core.Embeddings.Models;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Per-scope binary vector storage with append-only SVF3 format.
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
/// Compaction (deletes &gt; 20% of total ops, &gt;=10 deletes) rewrites in SVF3.
/// Ephemeral scope vectors are stored in-memory only.
/// Persistent scopes write to {baseDir}/{scope}/vectors.bin with atomic writes for full rewrites
/// and direct append for single-entry upserts.
/// </summary>
public sealed class VectorStore : IDisposable, IVectorStore
{
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
    private readonly ConcurrentDictionary<string, int> _scopeDeleteCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _scopeOpCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _staleQuarantines = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Adds or updates vectors for a named memory in a scope.</summary>
    public async Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct = default)
    {
        var lk = GetLock(scope);
        await lk.WaitAsync(ct);
        try
        {
            // Load explicitly rather than via GetOrAdd: a failed read must not become the
            // baseline this method writes back from.
            if (!_scopeVectors.TryGetValue(scope, out var current))
            {
                var loaded = TryLoadFromDisk(scope);
                if (!loaded.IsAuthoritative)
                    throw new VectorStoreUnavailableException(scope, GetFilePath(scope));
                current = loaded.Vectors;
                _scopeVectors[scope] = current;
            }

            // Copy-on-write: build a new list rather than mutating `current` in place, so any
            // GetVectors caller holding a reference to the old list keeps seeing a consistent
            // (old) snapshot, and never a torn one.
            //
            // Check if we're replacing an existing entry. Case-insensitive to match
            // RemoveAsync and the on-disk op-log replay (ReadEntryOps), both of which key
            // names with OrdinalIgnoreCase — keeping this ordinal would let two entries
            // differing only in case coexist in memory while a reload collapses them to one.
            bool hadExisting = false;
            var vectors = new List<VectorEntry>(current.Count + 1);
            foreach (var v in current)
            {
                if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) && v.ChunkIndex == chunkIndex)
                {
                    hadExisting = true;
                    continue;
                }
                vectors.Add(v);
            }
            vectors.Add(new VectorEntry(name, chunkIndex, vector));
            _scopeVectors[scope] = vectors;

            // Persist if not ephemeral
            if (!scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                string path = GetFilePath(scope);
                var probe = File.Exists(path) ? ProbeAppendable(path) : AppendProbe.NotAppendable;
                if (probe == AppendProbe.Unknown)
                    throw new VectorStoreUnavailableException(scope, path);

                if (probe == AppendProbe.Appendable)
                {
                    // SVF3: append operations instead of full rewrite
                    if (hadExisting)
                        await AppendDeleteOpAsync(path, name, chunkIndex, ct);
                    await AppendAddOpAsync(path, name, chunkIndex, vector, ct);

                    // Track ops for compaction
                    int ops = _scopeOpCount.AddOrUpdate(scope, 1, (_, v) => v + 1);
                    int deletes = hadExisting
                        ? _scopeDeleteCount.AddOrUpdate(scope, 1, (_, v) => v + 1)
                        : _scopeDeleteCount.GetOrAdd(scope, 0);

                    if (ops > 0 && (double)deletes / ops > 0.2 && deletes >= 10)
                        await CompactAsync(scope, vectors, ct);
                }
                else
                {
                    // First write, or no file present (older format files were either loaded
                    // and live-in-memory now, or quarantined as stale). Always write SVF3.
                    await SaveAsSvf3Async(scope, vectors, ct);
                }
            }
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>Removes all vectors for a named memory in a scope.</summary>
    public async Task RemoveAsync(string scope, string name, CancellationToken ct = default)
    {
        var lk = GetLock(scope);
        await lk.WaitAsync(ct);
        try
        {
            if (!_scopeVectors.TryGetValue(scope, out var current))
                return;

            // Copy-on-write: same rationale as UpsertAsync — never mutate `current` in place.
            var removed = current.Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            int removedCount = removed.Count;
            if (removedCount == 0)
                return;

            var vectors = current.Where(v => !v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            _scopeVectors[scope] = vectors;

            if (!scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                string path = GetFilePath(scope);
                var probe = File.Exists(path) ? ProbeAppendable(path) : AppendProbe.NotAppendable;
                if (probe == AppendProbe.Unknown)
                    throw new VectorStoreUnavailableException(scope, path);

                if (probe == AppendProbe.Appendable)
                {
                    foreach (var entry in removed)
                        await AppendDeleteOpAsync(path, entry.Name, entry.ChunkIndex, ct);

                    _scopeDeleteCount.AddOrUpdate(scope, removedCount, (_, v) => v + removedCount);
                    _scopeOpCount.AddOrUpdate(scope, removedCount, (_, v) => v + removedCount);
                }
                else
                {
                    await SaveAsSvf3Async(scope, vectors, ct);
                }
            }
        }
        finally
        {
            lk.Release();
        }
    }

    /// <summary>Returns the count of vectors across all loaded scopes.</summary>
    public int Count() => _scopeVectors.Values.Sum(v => v.Count);

    private string GetFilePath(string scope) => Path.Combine(_baseDir, ScopeToRelativeDir(scope), "vectors.bin");

    /// <summary>
    /// Maps a scope to its storage directory. Separators become real nested directories so
    /// <c>'_'</c> stays a literal character — replacing every separator with <c>'_'</c> is not
    /// injective, and collapsed <c>local:foo/bar</c> and <c>local:foo_bar</c> onto one file.
    /// </summary>
    private static string ScopeToRelativeDir(string scope)
    {
        string[] segments = scope.Split([':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return SanitizeSegment(scope);
        return Path.Combine([.. segments.Select(SanitizeSegment)]);
    }

    /// <summary>
    /// Makes one scope segment safe as a directory name. A short hash is appended whenever
    /// sanitization changes the segment, so <c>a*b</c> and <c>a_b</c> stay distinct.
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

    /// <summary>
    /// Moves a scope's vector file from the older flat layout
    /// (<c>{baseDir}/local_foo_bar/vectors.bin</c>) into the nested one, so upgrading does not
    /// orphan vectors and force a re-embed. Best-effort — on failure the scope reindexes.
    /// </summary>
    private void MigrateLegacyScopeFileIfPresent(string scope, string newPath)
    {
        if (File.Exists(newPath))
            return;

        string legacyDir = Path.Combine(_baseDir, scope.Replace(':', '_').Replace('/', '_').Replace('\\', '_'));
        string legacyPath = Path.Combine(legacyDir, "vectors.bin");
        if (!File.Exists(legacyPath) || string.Equals(legacyPath, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            FileRetry.Run(() => File.Move(legacyPath, newPath, overwrite: false));
            if (!Directory.EnumerateFileSystemEntries(legacyDir).Any())
                Directory.Delete(legacyDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"scrinia: could not migrate legacy vector file for scope '{scope}' ({ex.Message}). " +
                "The scope will be reindexed instead.");
        }
    }

    /// <summary>
    /// Whether a scope's file can be appended to. <see cref="AppendProbe.Unknown"/> (exists but
    /// unreadable) is distinct from not-appendable, which callers answer with a full rewrite.
    /// </summary>
    private enum AppendProbe { Appendable, NotAppendable, Unknown }

    private static AppendProbe ProbeAppendable(string path)
    {
        try
        {
            return FileRetry.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] magic = new byte[4];
                return fs.Read(magic, 0, 4) == 4 && magic.AsSpan().SequenceEqual(MagicSvf3)
                    ? AppendProbe.Appendable
                    : AppendProbe.NotAppendable;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppendProbe.Unknown;
        }
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
        string path = GetFilePath(scope);
        MigrateLegacyScopeFileIfPresent(scope, path);
        if (!File.Exists(path))
            return VectorLoadResult.Missing;

        try
        {
            return FileRetry.Run(() => new VectorLoadResult(VectorLoadStatus.Loaded, LoadFromDiskUnsafe(scope, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // Transient and still failing after the retry window, or a permission problem.
            // Either way we do not know what is on disk, so say so rather than claiming empty.
            Console.Error.WriteLine(
                $"scrinia: could not read vectors for scope '{scope}' ({ex.GetType().Name}: {ex.Message}). " +
                "Leaving the on-disk file untouched; embedding writes for this scope will fail until it is readable.");
            return VectorLoadResult.Failed;
        }
        catch
        {
            // Malformed content (bad header, garbage body). This IS authoritative — the file
            // is unusable and rewriting it is the intended recovery, same as before.
            return new VectorLoadResult(VectorLoadStatus.Loaded, []);
        }
    }

    private List<VectorEntry> LoadFromDiskUnsafe(string scope, string path)
    {
        {
            using var fileLock = FileLock.AcquireShared(GetVectorLockPath(path));
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
                    fileLock.Dispose();
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
                    fileLock.Dispose();
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
                    fileLock.Dispose();
                    QuarantineStaleFile(scope, path, fileSignature);
                    return [];
                }

                return LoadSvf3Body(reader, dims);
            }

            return [];
        }
    }

    private void QuarantineStaleFile(string scope, string path, string oldSignature)
    {
        try
        {
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            string stalePath = $"{path}.stale-{stamp}";
            File.Move(path, stalePath, overwrite: false);
            _staleQuarantines[scope] = 0;
            Console.Error.WriteLine(
                $"[scrinia:info] Vector file for scope '{scope}' was built with embedding " +
                $"signature '{oldSignature}' but the active provider is '{_expectedSignature}'. " +
                $"Quarantined to {Path.GetFileName(stalePath)}; rebuilding via reindex.");
        }
        catch (Exception ex)
        {
            // Best-effort quarantine — if rename fails, log and continue with an empty store.
            // The stale file remains on disk but is unreachable via the active store.
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to quarantine stale vector file for scope '{scope}': " +
                $"{ex.GetType().Name}: {ex.Message}");
            _staleQuarantines[scope] = 0;
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

    /// <summary>Full rewrite in SVF3 format (used for initial write and compaction).</summary>
    private async Task SaveAsSvf3Async(string scope, List<VectorEntry> vectors, CancellationToken ct)
    {
        string path = GetFilePath(scope);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";

        using var fileLock = FileLock.AcquireExclusive(GetVectorLockPath(path));

        // Empty signature is acceptable: tests and ad-hoc tools may construct a VectorStore
        // without an expected signature. Production paths always pass one.
        string signature = _expectedSignature ?? "";
        byte[] sigBytes = System.Text.Encoding.UTF8.GetBytes(signature);

        await FileRetry.RunAsync(async () =>
        {
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new BinaryWriter(fs);
            writer.Write(MagicSvf3);
            ushort dims = vectors.Count > 0 ? (ushort)vectors[0].Vector.Length : (ushort)0;
            writer.Write(dims);
            writer.Write((ushort)sigBytes.Length);
            writer.Write(sigBytes);

            foreach (var entry in vectors)
            {
                writer.Write((byte)0); // Add op
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((ushort)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(entry.ChunkIndex ?? -1);
                foreach (float f in entry.Vector)
                    writer.Write(f);
            }
        }, ct: ct);

        // File.Move onto the live path — where a transient "file is being used by another
        // process" failure is most likely. Retry with backoff.
        FileRetry.Run(() => File.Move(tmp, path, overwrite: true));

        _scopeDeleteCount[scope] = 0;
        _scopeOpCount[scope] = vectors.Count;
    }

    /// <summary>Appends a single add operation to an SVF2 file.</summary>
    private static async Task AppendAddOpAsync(string path, string name, int? chunkIndex, float[] vector, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var fileLock = FileLock.AcquireExclusive(GetVectorLockPath(path));
        await FileRetry.RunAsync(async () =>
        {
            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var writer = new BinaryWriter(fs);

            writer.Write((byte)0); // Add
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            writer.Write((ushort)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(chunkIndex ?? -1);
            foreach (float f in vector)
                writer.Write(f);
        }, ct: ct);
    }

    /// <summary>Appends a single delete operation to an SVF2 file.</summary>
    private static async Task AppendDeleteOpAsync(string path, string name, int? chunkIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var fileLock = FileLock.AcquireExclusive(GetVectorLockPath(path));
        await FileRetry.RunAsync(async () =>
        {
            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var writer = new BinaryWriter(fs);

            writer.Write((byte)1); // Delete
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            writer.Write((ushort)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(chunkIndex ?? -1);
        }, ct: ct);
    }

    /// <summary>Compacts an SVF3 file by rewriting only live entries.</summary>
    private async Task CompactAsync(string scope, List<VectorEntry> vectors, CancellationToken ct)
    {
        await SaveAsSvf3Async(scope, vectors, ct);
    }

    public void Dispose()
    {
        foreach (var lk in _scopeLocks.Values)
            lk.Dispose();
    }
}
