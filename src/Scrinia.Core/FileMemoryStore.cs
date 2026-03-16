using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Instance-based <see cref="IMemoryStore"/> backed by the filesystem.
/// Each instance is scoped to a workspace root directory.
///
/// Naming convention:
///   "subject"              → local scope:   {workspace}/.scrinia/store/subject.nmp2
///   "topic:subject"        → local topic:   {workspace}/.scrinia/topics/topic/subject.nmp2
///   "~subject"             → ephemeral:     in-memory only (dies with instance)
/// </summary>
public sealed partial class FileMemoryStore : IMemoryStore, IDisposable
{
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, EphemeralEntry> _ephemeralStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions;

    // Scope discovery cache with 2-second TTL
    private string[]? _cachedTopics;
    private DateTime _topicsCacheTime;
    private static readonly TimeSpan TopicsCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// In-memory cache of a scope's index entries with O(1) name→position lookup
    /// and lazily computed BM25 corpus statistics.
    /// </summary>
    internal sealed class CachedIndex
    {
        public List<ArtifactEntry> Entries { get; }
        public Dictionary<string, int> NameToPosition { get; private set; }

        /// <summary>Lazily computed BM25 corpus stats. Cleared on mutation.</summary>
        public CorpusStats? Stats { get; set; }

        public CachedIndex(List<ArtifactEntry> entries)
        {
            Entries = entries;
            NameToPosition = BuildNameMap(entries);
        }

        public void Rebuild()
        {
            NameToPosition = BuildNameMap(Entries);
            Stats = null; // Invalidate corpus stats on mutation
        }

        /// <summary>Computes and caches corpus stats on first access.</summary>
        public CorpusStats GetOrComputeCorpusStats()
        {
            if (Stats is not null) return Stats;

            var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
                Entries.Select(e => (IReadOnlyDictionary<string, int>?)e.TermFrequencies));

            Stats = new CorpusStats(avgDocLen, docFreqs, Entries.Count);
            return Stats;
        }

        private static Dictionary<string, int> BuildNameMap(List<ArtifactEntry> entries)
        {
            var map = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
                map[entries[i].Name] = i;
            return map;
        }
    }

    /// <summary>Pre-computed BM25 corpus statistics for a scope.</summary>
    public sealed record CorpusStats(double AvgDocLength, Dictionary<string, int> DocumentFrequencies, int CorpusSize);

    /// <summary>
    /// LRU cache for decoded artifact text, bounded by total byte size.
    /// Thread-safe via internal locking.
    /// </summary>
    private sealed class ArtifactLruCache
    {
        private readonly int _maxBytes;
        private int _currentBytes;
        private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<(string Key, string Value)> _order = new();
        private readonly object _lock = new();

        public ArtifactLruCache(int maxBytes) => _maxBytes = maxBytes;

        public bool TryGet(string key, out string value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = "";
            return false;
        }

        public void Set(string key, string value)
        {
            int size = value.Length * 2; // approximate byte size (UTF-16)
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _currentBytes -= existing.Value.Value.Length * 2;
                    _order.Remove(existing);
                    _map.Remove(key);
                }

                while (_currentBytes + size > _maxBytes && _order.Count > 0)
                {
                    var last = _order.Last!;
                    _currentBytes -= last.Value.Value.Length * 2;
                    _map.Remove(last.Value.Key);
                    _order.RemoveLast();
                }

                var node = _order.AddFirst((key, value));
                _map[key] = node;
                _currentBytes += size;
            }
        }

        public void Invalidate(string keyPrefix)
        {
            lock (_lock)
            {
                var toRemove = _map.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var key in toRemove)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _currentBytes -= node.Value.Value.Length * 2;
                        _order.Remove(node);
                        _map.Remove(key);
                    }
                }
            }
        }
    }

    // 50 MB artifact LRU cache
    private readonly ArtifactLruCache _artifactCache = new(50 * 1024 * 1024);

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IndexFile))]
    private partial class FileStoreJsonContext : JsonSerializerContext;

    public FileMemoryStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = FileStoreJsonContext.Default,
        };
    }

    // ── Naming ───────────────────────────────────────────────────────────────

    public (string Scope, string Subject) ParseQualifiedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        int colonIdx = name.IndexOf(':');
        if (colonIdx < 0)
            return ("local", SanitizeName(name.Trim()));

        string topic = name[..colonIdx].Trim();
        string subject = name[(colonIdx + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException($"Topic part must not be empty in '{name}'.", nameof(name));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException($"Subject part must not be empty in '{name}'.", nameof(name));

        return ($"local-topic:{SanitizeName(topic)}", SanitizeName(subject));
    }

    public string FormatQualifiedName(string scope, string subject)
    {
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return $"{scope["local-topic:".Length..]}:{subject}";
        return subject;
    }

    public bool IsEphemeral(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    // Characters invalid on Windows that Linux allows — always strip for portability.
    private static readonly HashSet<char> s_portableInvalid =
        [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '<', '>', '|', '"'];

    public string SanitizeName(string name)
    {
        // Strip directory separators and path traversal sequences first
        string safe = name.Replace("..", "").Replace('/', '_').Replace('\\', '_');

        // Remove remaining invalid filename characters (cross-platform set)
        safe = string.Concat(safe.Select(c => s_portableInvalid.Contains(c) ? '_' : c));

        // Final safety: extract only the filename component (blocks any residual path)
        safe = Path.GetFileName(safe);

        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException($"Name '{name}' is invalid after sanitization.", nameof(name));

        return safe;
    }

    // ── Ephemeral memory ─────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.StripEphemeralPrefix instead.")]
    public static string StripEphemeralPrefix(string name) =>
        MemoryNaming.StripEphemeralPrefix(name);

    public void RememberEphemeral(string key, EphemeralEntry entry) =>
        _ephemeralStore[key] = entry;

    public bool ForgetEphemeral(string key) =>
        _ephemeralStore.TryRemove(key, out _);

    public EphemeralEntry? GetEphemeral(string key) =>
        _ephemeralStore.TryGetValue(key, out var entry) ? entry : null;

    public List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in _ephemeralStore)
        {
            var e = kvp.Value;
            var artifactEntry = new ArtifactEntry(
                Name: e.Name,
                Uri: "",
                OriginalBytes: e.OriginalBytes,
                ChunkCount: e.ChunkCount,
                CreatedAt: e.CreatedAt,
                Description: e.Description,
                Tags: e.Tags,
                ContentPreview: e.ContentPreview,
                Keywords: e.Keywords,
                TermFrequencies: e.TermFrequencies,
                UpdatedAt: e.UpdatedAt,
                ChunkEntries: e.ChunkEntries);
            result.Add(new ScopedArtifact("ephemeral", artifactEntry));
        }
        return result;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    public string GetStoreDirForScope(string scope)
    {
        if (scope == "local")
            return Path.Combine(_workspaceRoot, ".scrinia", "store");

        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            string topic = scope["local-topic:".Length..];
            return Path.Combine(_workspaceRoot, ".scrinia", "topics", topic);
        }

        throw new ArgumentException($"Unknown scope: {scope}");
    }

    public string ArtifactPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".nmp2");

    public string ArtifactUri(string name, string scope = "local") =>
        $"file://{ArtifactPath(name, scope)}";

    public string FindArtifactPath(string subject, string normalizedScope) =>
        ArtifactPath(subject, normalizedScope);

    // ── Scope helpers ────────────────────────────────────────────────────────

    internal IReadOnlyList<string> NormalizeScopeFilters(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ["local"];

        string s = token.Trim();
        if (s.Equals("local", StringComparison.OrdinalIgnoreCase)) return ["local"];
        // Ephemeral is handled separately in ListScoped — exclude from filesystem scopes
        if (s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase)) return [];
        if (s.StartsWith("local-topic:", StringComparison.OrdinalIgnoreCase)) return [s];

        return [$"local-topic:{SanitizeName(s)}"];
    }

    public IReadOnlyList<string> ResolveReadScopes(string? scopes = null)
    {
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            return scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(NormalizeScopeFilters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    private ReaderWriterLockSlim GetIndexLock(string scope) =>
        _indexLocks.GetOrAdd(scope, _ => new ReaderWriterLockSlim());

    private string GetLockPath(string scope) =>
        Path.Combine(GetStoreDirForScope(scope), ".lock");

    public List<ArtifactEntry> LoadIndex(string scope = "local")
    {
        using var fileLock = FileLock.AcquireShared(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterReadLock();
        try
        {
            return LoadIndexUnsafe(scope);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }

    private List<ArtifactEntry> LoadIndexUnsafe(string scope)
    {
        // Check in-memory cache first
        if (_indexCache.TryGetValue(scope, out var cached))
            return cached.Entries.ToList();

        string storeDir = GetStoreDirForScope(scope);
        string indexPath = Path.Combine(storeDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        try
        {
            string json = File.ReadAllText(indexPath);
            var idx = JsonSerializer.Deserialize<IndexFile>(json, _jsonOptions);
            var entries = idx?.Entries ?? [];

            // Populate cache (safe even under read lock — ConcurrentDictionary handles races)
            _indexCache[scope] = new CachedIndex(entries);

            return entries.ToList();
        }
        catch
        {
            return [];
        }
    }

    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
    {
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            SaveIndexUnsafe(entries, scope);
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        string storeDir = GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDir);

        var idx = new IndexFile { Entries = entries };
        string json = JsonSerializer.Serialize(idx, _jsonOptions);

        string indexPath = Path.Combine(storeDir, "index.json");
        string tmp = $"{indexPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, indexPath, overwrite: true);

        // Update the in-memory cache
        _indexCache[scope] = new CachedIndex(entries);

        // Invalidate topic discovery cache when saving a topic scope
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            _cachedTopics = null;
    }

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            // Invalidate cache under lock to pick up changes from other processes
            _indexCache.TryRemove(scope, out _);
            List<ArtifactEntry> entries = LoadIndexFromCacheOrDisk(scope);

            // O(1) lookup via cached name dictionary
            if (_indexCache.TryGetValue(scope, out var cached) &&
                cached.NameToPosition.TryGetValue(entry.Name, out int pos))
            {
                entries[pos] = entry;
            }
            else
            {
                int idx = entries.FindIndex(e => e.Name == entry.Name);
                if (idx >= 0)
                    entries[idx] = entry;
                else
                    entries.Add(entry);
            }

            SaveIndexUnsafe(entries, scope);
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    public bool Remove(string name, string scope = "local")
    {
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            // Invalidate cache under lock to pick up changes from other processes
            _indexCache.TryRemove(scope, out _);
            List<ArtifactEntry> entries = LoadIndexFromCacheOrDisk(scope);
            int before = entries.Count;

            // O(1) lookup via cached name dictionary
            if (_indexCache.TryGetValue(scope, out var cached) &&
                cached.NameToPosition.TryGetValue(name, out int pos))
            {
                entries.RemoveAt(pos);
            }
            else
            {
                entries.RemoveAll(e => e.Name == name);
            }

            if (entries.Count == before) return false;
            SaveIndexUnsafe(entries, scope);
            return true;
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    /// <summary>
    /// Loads index entries from cache or disk. Unlike <see cref="LoadIndexUnsafe"/>,
    /// this does NOT take a lock — caller must already hold a write lock.
    /// </summary>
    private List<ArtifactEntry> LoadIndexFromCacheOrDisk(string scope)
    {
        if (_indexCache.TryGetValue(scope, out var cached))
            return cached.Entries.ToList();

        string storeDir = GetStoreDirForScope(scope);
        string indexPath = Path.Combine(storeDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        try
        {
            string json = File.ReadAllText(indexPath);
            var idx = JsonSerializer.Deserialize<IndexFile>(json, _jsonOptions);
            return idx?.Entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    // ── Artifact file I/O ────────────────────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, artifactText, ct);
        _artifactCache.Invalidate($"{scope}|{subject}|");
    }

    public async Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Artifact not found: {subject} in scope {scope}", path);

        // Check LRU cache (keyed by scope|subject|lastWriteTicks for staleness safety)
        long ticks = new FileInfo(path).LastWriteTimeUtc.Ticks;
        string cacheKey = $"{scope}|{subject}|{ticks}";
        if (_artifactCache.TryGet(cacheKey, out string cached))
            return cached;

        string text = await File.ReadAllTextAsync(path, ct);
        _artifactCache.Set(cacheKey, text);
        return text;
    }

    public bool DeleteArtifact(string subject, string scope)
    {
        string path = ArtifactPath(subject, scope);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _artifactCache.Invalidate($"{scope}|{subject}|");
        return true;
    }

    // ── Resolve ──────────────────────────────────────────────────────────────

    public async Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrArtifact))
            throw new ArgumentException("Input must not be empty.", nameof(nameOrArtifact));

        // 1. Inline artifact
        if (nameOrArtifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return nameOrArtifact;

        // 2. file:// URI (backward compat)
        if (nameOrArtifact.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string filePath = nameOrArtifact[7..];
            return await File.ReadAllTextAsync(filePath, ct);
        }

        // 3. Ephemeral memory (~name)
        if (IsEphemeral(nameOrArtifact))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrArtifact);
            var entry = GetEphemeral(key);
            if (entry is null)
                throw new FileNotFoundException($"Ephemeral memory '~{key}' not found.");
            return entry.Artifact;
        }

        // 4. Qualified name resolution
        var (scope, subject) = ParseQualifiedName(nameOrArtifact);
        string path = FindArtifactPath(subject, scope);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Memory '{nameOrArtifact}' not found.", path);

        return await File.ReadAllTextAsync(path, ct);
    }

    // ── Listing & Search ─────────────────────────────────────────────────────

    public List<ScopedArtifact> ListScoped(string? scopes = null)
    {
        var result = new List<ScopedArtifact>();

        bool includeEphemeral = string.IsNullOrWhiteSpace(scopes);
        if (!includeEphemeral && scopes is not null)
        {
            includeEphemeral = scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(s => s.Trim().Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
        }

        if (includeEphemeral)
            result.AddRange(ListEphemeral());

        foreach (string scope in ResolveReadScopes(scopes))
        {
            foreach (var entry in LoadIndex(scope))
                result.Add(new ScopedArtifact(scope, entry));
        }
        return result;
    }

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20)
        => SearchAll(query, scopes, limit, supplementalScores: null);

    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit,
        IReadOnlyDictionary<string, double>? supplementalScores)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var searcher = new WeightedFieldScorer();
        var candidates = ListScoped(scopes);
        var topics = GatherTopicInfos(scopes);
        return searcher.SearchAll(query, candidates, topics, limit, supplementalScores);
    }

    // ── Copy & Archive ───────────────────────────────────────────────────────

    public bool CopyMemory(string sourceName, string destinationName, bool overwrite, out string message)
    {
        bool srcEphemeral = IsEphemeral(sourceName);
        bool dstEphemeral = IsEphemeral(destinationName);
        string srcKey = srcEphemeral ? MemoryNaming.StripEphemeralPrefix(sourceName) : sourceName;
        string dstKey = dstEphemeral ? MemoryNaming.StripEphemeralPrefix(destinationName) : destinationName;

        // ── Ephemeral source ─────────────────────────────────────────────
        if (srcEphemeral)
        {
            var entry = GetEphemeral(srcKey);
            if (entry is null)
            {
                message = $"Error: source memory '{sourceName}' was not found.";
                return false;
            }

            if (dstEphemeral)
            {
                if (srcKey.Equals(dstKey, StringComparison.OrdinalIgnoreCase))
                {
                    message = "Error: source and destination are the same.";
                    return false;
                }
                if (!overwrite && GetEphemeral(dstKey) is not null)
                {
                    message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                    return false;
                }
                RememberEphemeral(dstKey, entry with { Name = dstKey });
                message = $"Copied '{sourceName}' to '{destinationName}'.";
                return true;
            }

            // Ephemeral → Persistent (promotion)
            var (dstScope, dstSubject) = ParseQualifiedName(dstKey);
            string destPath = ArtifactPath(dstSubject, dstScope);
            if (File.Exists(destPath) && !overwrite)
            {
                message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllText(destPath, entry.Artifact);

            var destEntry = new ArtifactEntry(
                Name: dstSubject,
                Uri: ArtifactUri(dstSubject, dstScope),
                OriginalBytes: entry.OriginalBytes,
                ChunkCount: entry.ChunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: entry.Description,
                Tags: entry.Tags,
                ContentPreview: entry.ContentPreview,
                ChunkEntries: entry.ChunkEntries);
            Upsert(destEntry, dstScope);
            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
        }

        // ── Persistent source ────────────────────────────────────────────
        var (srcScope, srcSubject) = ParseQualifiedName(srcKey);

        if (!dstEphemeral)
        {
            var (dstScopeCheck, dstSubjectCheck) = ParseQualifiedName(dstKey);
            if (srcScope.Equals(dstScopeCheck, StringComparison.OrdinalIgnoreCase)
                && srcSubject.Equals(dstSubjectCheck, StringComparison.OrdinalIgnoreCase))
            {
                message = "Error: source and destination are the same.";
                return false;
            }
        }

        string sourcePath = FindArtifactPath(srcSubject, srcScope);
        if (!File.Exists(sourcePath))
        {
            message = $"Error: source memory '{sourceName}' was not found.";
            return false;
        }

        if (dstEphemeral)
        {
            if (!overwrite && GetEphemeral(dstKey) is not null)
            {
                message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
                return false;
            }

            string artifact = File.ReadAllText(sourcePath);
            ArtifactEntry? srcEntry = LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject);

            var ephEntry = new EphemeralEntry(
                Name: dstKey,
                Artifact: artifact,
                OriginalBytes: srcEntry?.OriginalBytes ?? 0,
                ChunkCount: srcEntry?.ChunkCount ?? Nmp2ChunkedEncoder.GetChunkCount(artifact),
                CreatedAt: DateTimeOffset.UtcNow,
                Description: srcEntry?.Description ?? $"Loaded from {sourceName}",
                Tags: srcEntry?.Tags,
                ContentPreview: srcEntry?.ContentPreview,
                ChunkEntries: srcEntry?.ChunkEntries);
            RememberEphemeral(dstKey, ephEntry);
            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
        }

        // Persistent → Persistent
        var (persistDstScope, persistDstSubject) = ParseQualifiedName(dstKey);
        string destPathP = ArtifactPath(persistDstSubject, persistDstScope);
        if (File.Exists(destPathP) && !overwrite)
        {
            message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPathP)!);
        File.Copy(sourcePath, destPathP, overwrite);

        ArtifactEntry? sourceEntry = LoadIndex(srcScope).FirstOrDefault(e => e.Name == srcSubject);
        ArtifactEntry destinationEntry;

        if (sourceEntry is not null)
        {
            destinationEntry = sourceEntry with
            {
                Name = persistDstSubject,
                Uri = ArtifactUri(persistDstSubject, persistDstScope),
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
        else
        {
            string artifactText = File.ReadAllText(destPathP);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifactText);
            long originalBytes = new Nmp2Strategy().Decode(artifactText).LongLength;

            destinationEntry = new ArtifactEntry(
                Name: persistDstSubject,
                Uri: ArtifactUri(persistDstSubject, persistDstScope),
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: $"Copied from {sourceName}");
        }

        Upsert(destinationEntry, persistDstScope);
        message = $"Copied '{sourceName}' to '{destinationName}'.";
        return true;
    }

    public void ArchiveVersion(string subject, string scope = "local")
    {
        string currentPath = ArtifactPath(subject, scope);
        if (!File.Exists(currentPath))
            return;

        string storeDir = GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(storeDir, "versions");
        Directory.CreateDirectory(versionsDir);

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string archiveName = $"{SanitizeName(subject)}_{timestamp}.nmp2";
        string archivePath = Path.Combine(versionsDir, archiveName);

        File.Copy(currentPath, archivePath, overwrite: true);
    }

    // ── Content utility ──────────────────────────────────────────────────────

    public string GenerateContentPreview(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    // ── Topic discovery ──────────────────────────────────────────────────────

    public string[] DiscoverTopics()
    {
        // Return cached result if still fresh
        if (_cachedTopics is not null && (DateTime.UtcNow - _topicsCacheTime) < TopicsCacheTtl)
            return _cachedTopics;

        string localTopicsRoot = Path.Combine(_workspaceRoot, ".scrinia", "topics");
        if (!Directory.Exists(localTopicsRoot))
        {
            _cachedTopics = [];
            _topicsCacheTime = DateTime.UtcNow;
            return [];
        }

        var topics = Directory.GetDirectories(localTopicsRoot)
            .Select(d => $"local-topic:{Path.GetFileName(d)}")
            .ToArray();

        _cachedTopics = topics;
        _topicsCacheTime = DateTime.UtcNow;
        return topics;
    }

    public List<TopicInfo> GatherTopicInfos(string? scopes = null)
    {
        var topics = new List<TopicInfo>();
        foreach (string scope in ResolveReadScopes(scopes))
        {
            if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
                continue;

            string topicName = scope["local-topic:".Length..];
            var entries = LoadIndex(scope);
            if (entries.Count == 0) continue;
            topics.Add(new TopicInfo(
                Scope: scope,
                TopicName: topicName,
                EntryCount: entries.Count,
                Description: $"{topicName} ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: entries.Select(e => e.Name).ToArray()));
        }
        return topics;
    }

    // ── Export/Import helpers ─────────────────────────────────────────────────

    public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)
    {
        var result = new List<(string, string)>();
        var entries = LoadIndex(topicScope);
        string storeDir = GetStoreDirForScope(topicScope);
        foreach (var entry in entries)
        {
            string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
            if (File.Exists(filePath))
                result.Add((entry.Name, filePath));
        }
        return result;
    }

    public void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite)
    {
        string storeDir = GetStoreDirForScope(topicScope);
        Directory.CreateDirectory(storeDir);

        var existingEntries = LoadIndex(topicScope);

        foreach (var entry in entries)
        {
            bool exists = existingEntries.Any(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && !overwrite)
                continue;

            if (artifactContents.TryGetValue(entry.Name, out string? content))
            {
                string filePath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".nmp2");
                File.WriteAllText(filePath, content);
            }

            var updatedEntry = entry with
            {
                Uri = ArtifactUri(entry.Name, topicScope),
                CreatedAt = DateTimeOffset.UtcNow
            };

            int idx = existingEntries.FindIndex(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                existingEntries[idx] = updatedEntry;
            else
                existingEntries.Add(updatedEntry);
        }

        SaveIndex(existingEntries, topicScope);
    }

    // ── Display helpers ──────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.FormatScopeLabel instead.")]
    public static string FormatScopeLabel(string scope) =>
        MemoryNaming.FormatScopeLabel(scope);

    public static string NameFromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return uri;
        string path = uri[7..];
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        foreach (var lk in _indexLocks.Values)
            lk.Dispose();
        _indexLocks.Clear();
    }
}
