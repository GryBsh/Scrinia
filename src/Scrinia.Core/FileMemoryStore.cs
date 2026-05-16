using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Instance-based <see cref="IMemoryStore"/> backed by the filesystem.
/// Each instance is scoped to a workspace root directory.
///
/// Naming convention:
///   "subject"              → local scope:   {workspace}/.scrinia/store/subject.nmp2
///   "topic:subject"        → local topic:   {workspace}/.scrinia/topics/topic/subject.nmp2
///   "/temp/subject"         → ephemeral:     in-memory only (dies with instance)
/// </summary>
public sealed partial class FileMemoryStore : IMemoryStore, IDisposable
{
    private const int MaxEphemeralEntries = 1000;
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, EphemeralEntry> _ephemeralStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);

    // Topic discovery cache. Invalidated event-driven (set to null) by Upsert / SaveIndex
    // when a topic-scope mutation occurs. No TTL — repeated DiscoverTopics calls between
    // mutations are O(1) cache hits rather than re-scanning the topics directory tree.
    // The earlier 2-second TTL drove a constant ~30 directory enumerations/minute on the
    // search hot path even when nothing had changed.
    private string[]? _cachedTopics;

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
                Entries.Select(e => (IReadOnlyDictionary<string, int>?)e.TermFrequencies),
                docCountHint: Entries.Count);

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
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IndexFile))]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class FileStoreJsonContext : JsonSerializerContext;

    /// <summary>
    /// Tunable ranker weights — read by <see cref="SearchAll(string, string?, int)"/>
    /// when constructing the underlying <see cref="WeightedFieldScorer"/>. Defaults to
    /// <see cref="RankerOptions.Default"/>; <c>WorkspaceSetup.Configure</c> populates
    /// it from <c>Scrinia:Search:*</c> config keys at workspace init.
    /// </summary>
    public RankerOptions RankerOptions { get; set; } = RankerOptions.Default;

    public FileMemoryStore(string workspaceRoot) : this(workspaceRoot, rankerOptions: null) { }

    public FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        RankerOptions = rankerOptions ?? RankerOptions.Default;

        EnsureGitIgnore(_workspaceRoot);
    }

    internal static void EnsureGitIgnore(string workspaceRoot)
    {
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        Directory.CreateDirectory(scriniaDir);

        string gitIgnorePath = Path.Combine(scriniaDir, ".gitignore");
        if (File.Exists(gitIgnorePath)) return;

        AtomicWriteAllText(gitIgnorePath, """
            # Lock files (runtime artifacts from cross-process file locking)
            **/.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Consolidation bookkeeping (rebuilt from sidecar state on next run)
            .last-consolidation
            .consolidate-progress.json
            """.Replace("            ", ""));
    }

    // ── Naming ───────────────────────────────────────────────────────────────

    public (string Scope, string Subject) ParseQualifiedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        // v2 path syntax: starts with "/"
        if (name.StartsWith('/'))
        {
            var parsed = PathParser.Parse(name, MemoryNaming.EntityTopics);

            var segments = parsed.Segments;
            if (segments.Count == 0)
                throw new ArgumentException("Empty path", nameof(name));

            if (segments.Count == 1)
                return ("local", SanitizeName(segments[0].Value));

            // Build scope from all but last segment
            string topicPart = string.Join("/", segments.Take(segments.Count - 1).Select(s => s.Value));
            string scope = $"local-topic:{topicPart}";
            string subject = SanitizeName(segments[^1].Value);
            return (scope, subject);
        }

        // v1 topic:name syntax (existing logic)
        int colonIdx = name.IndexOf(':');
        if (colonIdx < 0)
            return ("local", SanitizeName(name.Trim()));

        string topic = name[..colonIdx].Trim();
        string subjectV1 = name[(colonIdx + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException($"Topic part must not be empty in '{name}'.", nameof(name));
        if (string.IsNullOrWhiteSpace(subjectV1))
            throw new ArgumentException($"Subject part must not be empty in '{name}'.", nameof(name));

        return (MemoryNaming.BuildScopedTopicScope(SanitizeName(topic)), SanitizeName(subjectV1));
    }

    public string FormatQualifiedName(string scope, string subject)
    {
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            string topicPart = scope["local-topic:".Length..];

            // v2 path scope -> reconstruct as /path syntax
            if (IsV2PathScope(topicPart))
                return $"/{topicPart}/{subject}";

            // v1 -> strip namespace prefix and output as path format
            string stripped = MemoryNaming.StripNamespacePrefix(topicPart);
            return $"/{stripped}/{subject}";
        }
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

    public void RememberEphemeral(string key, EphemeralEntry entry)
    {
        _ephemeralStore[key] = entry;

        if (_ephemeralStore.Count > MaxEphemeralEntries)
        {
            // Evict oldest by CreatedAt
            var oldest = _ephemeralStore.OrderBy(e => e.Value.CreatedAt).First();
            _ephemeralStore.TryRemove(oldest.Key, out _);
        }
    }

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

    /// <summary>
    /// Determines whether a topic part from a "local-topic:X" scope is a v2 multi-level
    /// path (routed to .scrinia/memories/) vs a v1 namespace scope (routed to .scrinia/topics/).
    /// v1 patterns: "agent", "entity/{topic}", "memory/{topic}" (single namespace + single topic).
    /// v2 patterns: anything with more than 2 segments or a non-namespace first segment with slashes.
    /// </summary>
    private static bool IsV2PathScope(string topicPart)
    {
        // No slash -> v1 flat topic (e.g. "arch", "patterns")
        int firstSlash = topicPart.IndexOf('/');
        if (firstSlash < 0) return false;

        string prefix = topicPart[..firstSlash];

        // "agent" namespace has no sub-slash in v1
        if (prefix.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            // agent/X is v1 (shouldn't normally happen since agent is flat), but
            // agent/X/Y would be v2
            return topicPart.IndexOf('/', firstSlash + 1) >= 0;
        }

        // "entity/topic" or "memory/topic" with exactly one slash -> v1
        if (MemoryNaming.ReservedNamespaceDirs.Contains(prefix))
        {
            // If there's another slash beyond "entity/topic", it's v2
            return topicPart.IndexOf('/', firstSlash + 1) >= 0;
        }

        // First segment is not a reserved namespace -> v2 (e.g. "goal/G-5/research")
        return true;
    }

    public string GetStoreDirForScope(string scope)
    {
        if (scope == "local")
            return Path.Combine(_workspaceRoot, ".scrinia", "store");

        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            string topic = scope["local-topic:".Length..];

            // v2 path scope: topic contains "/" that isn't a v1 namespace prefix
            // (v1 namespaces are "entity/X", "memory/X", "agent" — exactly one slash
            // with a recognised prefix). Multi-level paths use .scrinia/memories/.
            if (IsV2PathScope(topic))
                return Path.Combine(_workspaceRoot, ".scrinia", "memories", topic.Replace('/', Path.DirectorySeparatorChar));

            return Path.Combine(_workspaceRoot, ".scrinia", "topics", topic);
        }

        throw new ArgumentException($"Unknown scope: {scope}");
    }

    public string ArtifactPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".nmp2");

    internal string MetaPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".meta.json");

    public string ArtifactUri(string name, string scope = "local") =>
        $"file://{ArtifactPath(name, scope)}";

    public string FindArtifactPath(string subject, string normalizedScope)
    {
        string primary = ArtifactPath(subject, normalizedScope);
        if (File.Exists(primary))
            return primary;

        // Namespace → flat legacy fallback
        string? legacyDir = ResolveLegacyFallbackDir(normalizedScope);
        if (legacyDir is not null)
        {
            string legacyPath = Path.Combine(legacyDir, SanitizeName(subject) + ".nmp2");
            if (File.Exists(legacyPath))
                return legacyPath;
        }

        // v2 path scope → v1 legacy file fallback via PathRouter
        string? v2Legacy = ResolveV2LegacyFilePath(subject, normalizedScope);
        if (v2Legacy is not null)
            return v2Legacy;

        return primary;
    }

    // ── Legacy fallback ────────────────────────────────────────────────────

    /// <summary>
    /// Given a namespaced scope like "local-topic:entity/task", returns the legacy
    /// flat path (.scrinia/topics/task/) for backward-compatible reads.
    /// Returns null if the scope is not a namespaced topic scope.
    /// </summary>
    private string? ResolveLegacyFallbackDir(string scope)
    {
        if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return null;

        string topicPart = scope["local-topic:".Length..];

        // v2 path scopes get their own fallback chain via ResolveV2LegacyDir
        if (IsV2PathScope(topicPart))
            return ResolveV2LegacyDir(scope);

        // Only applies to namespaced scopes (containing a '/')
        // or the special "agent" scope
        string legacyTopic;
        int slashIdx = topicPart.IndexOf('/');
        if (slashIdx >= 0)
        {
            string prefix = topicPart[..slashIdx];
            if (!MemoryNaming.ReservedNamespaceDirs.Contains(prefix))
                return null;
            legacyTopic = topicPart[(slashIdx + 1)..];
        }
        else
        {
            return null; // not a namespaced scope
        }

        string legacyDir = Path.Combine(_workspaceRoot, ".scrinia", "topics", legacyTopic);
        return Directory.Exists(legacyDir) ? legacyDir : null;
    }

    /// <summary>
    /// Resolves a v2 path scope to the equivalent v1 legacy directory for fallback reads.
    /// Uses the leaf segment of the topic path as the v1 topic name and probes
    /// legacy flat and namespaced locations.
    /// </summary>
    private string? ResolveV2LegacyDir(string scope)
    {
        if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return null;

        string topicPart = scope["local-topic:".Length..];
        if (!IsV2PathScope(topicPart))
            return null;

        // The leaf segment of the v2 topic path is the most likely v1 topic name.
        // e.g. "goal/G-5/research" → leaf = "research"
        int lastSlash = topicPart.LastIndexOf('/');
        string leafTopic = lastSlash >= 0 ? topicPart[(lastSlash + 1)..] : topicPart;
        if (string.IsNullOrEmpty(leafTopic))
            return null;

        // Check flat legacy location: .scrinia/topics/{leafTopic}/
        string flatDir = Path.Combine(_workspaceRoot, ".scrinia", "topics", leafTopic);
        if (Directory.Exists(flatDir))
            return flatDir;

        // Check namespaced legacy locations based on topic classification
        string ns = MemoryNaming.ClassifyTopic(leafTopic);
        string nsDir = Path.Combine(_workspaceRoot, ".scrinia", "topics", ns, leafTopic);
        if (Directory.Exists(nsDir))
            return nsDir;

        return null;
    }

    /// <summary>
    /// Resolves a v2 path scope + subject to the equivalent v1 legacy file path.
    /// Builds a synthetic <see cref="ParsedPath"/> and delegates to
    /// <see cref="PathRouter.ToLegacyPath"/>, which probes the filesystem.
    /// </summary>
    private string? ResolveV2LegacyFilePath(string subject, string scope)
    {
        if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return null;

        string topicPart = scope["local-topic:".Length..];
        if (!IsV2PathScope(topicPart))
            return null;

        // Build a full v2 path from scope + subject and let PathRouter find legacy
        string fullPath = "/" + topicPart + "/" + subject;
        try
        {
            var parsed = PathParser.Parse(fullPath, MemoryNaming.EntityTopics);
            string? legacyPath = PathRouter.ToLegacyPath(parsed, _workspaceRoot);
            if (legacyPath is not null && File.Exists(legacyPath))
                return legacyPath;
        }
        catch (ArgumentException)
        {
            // Path parsing can fail for malformed scope strings — not a fatal error
        }

        // Fallback: try the leaf topic segment as a v1 flat topic
        int lastSlash = topicPart.LastIndexOf('/');
        string leafTopic = lastSlash >= 0 ? topicPart[(lastSlash + 1)..] : topicPart;
        if (string.IsNullOrEmpty(leafTopic))
            return null;

        string sanitized = SanitizeName(subject);

        // Flat legacy: .scrinia/topics/{leafTopic}/{subject}.nmp2
        string flatPath = Path.Combine(_workspaceRoot, ".scrinia", "topics", leafTopic, sanitized + ".nmp2");
        if (File.Exists(flatPath))
            return flatPath;

        // Namespaced legacy: .scrinia/topics/{ns}/{leafTopic}/{subject}.nmp2
        string ns = MemoryNaming.ClassifyTopic(leafTopic);
        string nsPath = Path.Combine(_workspaceRoot, ".scrinia", "topics", ns, leafTopic, sanitized + ".nmp2");
        if (File.Exists(nsPath))
            return nsPath;

        return null;
    }

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

        // Path prefix query: "/goal/G-5/" → find all scopes under that path tree
        if (s.StartsWith('/'))
            return ResolvePathPrefix(s);

        return [MemoryNaming.BuildScopedTopicScope(SanitizeName(s))];
    }

    /// <summary>
    /// Resolves a path prefix (e.g. "/goal/G-5/") to all discovered scopes whose
    /// topic part starts with the prefix. Enables subtree queries for hierarchical v2 paths
    /// as well as v1 scopes whose stripped topic name matches.
    /// </summary>
    internal IReadOnlyList<string> ResolvePathPrefix(string pathPrefix)
    {
        string prefix = pathPrefix.TrimStart('/').TrimEnd('/');
        if (string.IsNullOrEmpty(prefix))
        {
            // "/" alone means everything — equivalent to "all"
            var all = new List<string> { "local" };
            all.AddRange(DiscoverTopics());
            return all;
        }

        var matching = new List<string>();
        foreach (string scope in DiscoverTopics())
        {
            if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
                continue;

            string topicPart = scope["local-topic:".Length..];

            // Direct match: the topic part starts with the prefix at a path boundary
            // e.g. prefix="goal/G-5" matches "goal/G-5", "goal/G-5/research" but NOT "goal/G-50"
            if (MatchesPathPrefix(topicPart, prefix))
            {
                matching.Add(scope);
                continue;
            }

            // Also match against the stripped (namespace-removed) topic part
            // so "/arch" matches "local-topic:memory/arch" and "local-topic:arch"
            string stripped = MemoryNaming.StripNamespacePrefix(topicPart);
            if (!stripped.Equals(topicPart, StringComparison.Ordinal)
                && MatchesPathPrefix(stripped, prefix))
            {
                matching.Add(scope);
            }
        }

        return matching;
    }

    /// <summary>
    /// Returns true if <paramref name="topicPart"/> equals or is a child path of <paramref name="prefix"/>.
    /// Matches at path separator boundaries to avoid "goal/G-5" matching "goal/G-50".
    /// </summary>
    private static bool MatchesPathPrefix(string topicPart, string prefix)
    {
        if (topicPart.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        if (topicPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && topicPart.Length > prefix.Length
            && topicPart[prefix.Length] == '/')
            return true;

        return false;
    }

    public IReadOnlyList<string> ResolveReadScopes(string? scopes = null)
    {
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            // "all" (case-insensitive) returns every scope including entity-classified ones
            if (scopes.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = new List<string> { "local" };
                all.AddRange(DiscoverTopics());
                return all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return scopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(NormalizeScopeFilters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Default (null): exclude entity-classified scopes so searches focus on user content
        var ordered = new List<string> { "local" };
        foreach (var topic in DiscoverTopics())
        {
            if (IsEntityScope(topic))
                continue;
            ordered.Add(topic);
        }
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Returns true if the scope is classified as an entity scope — either
    /// namespaced ("local-topic:entity/*") or legacy flat ("local-topic:{topic}"
    /// where topic is in <see cref="MemoryNaming.EntityTopics"/>).
    /// </summary>
    private static bool IsEntityScope(string scope)
    {
        if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return false;

        string topicPart = scope["local-topic:".Length..];

        // Namespaced: "entity/task", "entity/concern", etc.
        if (topicPart.StartsWith("entity/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Legacy flat: "task", "concern", etc. (no slash → bare topic name)
        if (!topicPart.Contains('/') && MemoryNaming.EntityTopics.Contains(topicPart))
            return true;

        return false;
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
        var entries = LoadEntriesFromSidecars(storeDir);

        // Fallback: migrate from legacy index.json if no sidecars found
        if (entries.Count == 0)
        {
            entries = LoadEntriesFromLegacyIndex(storeDir);
            if (entries.Count > 0)
                WriteSidecars(entries, storeDir);
        }

        // Merge entries from legacy flat directory (new-path entries take precedence)
        string? legacyDir = ResolveLegacyFallbackDir(scope);
        if (legacyDir is not null)
        {
            var legacyEntries = LoadEntriesFromSidecars(legacyDir);
            if (legacyEntries.Count == 0)
                legacyEntries = LoadEntriesFromLegacyIndex(legacyDir);

            if (legacyEntries.Count > 0)
            {
                var existingNames = new HashSet<string>(
                    entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var le in legacyEntries)
                {
                    if (!existingNames.Contains(le.Name))
                        entries.Add(le);
                }
            }
        }

        if (entries.Count > 0)
            _indexCache[scope] = new CachedIndex(entries);

        return entries.ToList();
    }

    private List<ArtifactEntry> LoadEntriesFromSidecars(string storeDir)
    {
        if (!Directory.Exists(storeDir)) return [];

        var entries = new List<ArtifactEntry>();
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json"))
        {
            try
            {
                string json = File.ReadAllText(metaFile);
                var entry = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.ArtifactEntry);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch { /* skip corrupt sidecar */ }
        }
        return entries;
    }

    private static List<ArtifactEntry> LoadEntriesFromLegacyIndex(string storeDir)
    {
        string indexPath = Path.Combine(storeDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        try
        {
            string json = File.ReadAllText(indexPath);
            var idx = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.IndexFile);
            return idx?.Entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteSidecars(List<ArtifactEntry> entries, string storeDir)
    {
        foreach (var entry in entries)
            WriteSidecar(entry, storeDir);
    }

    private void WriteSidecar(ArtifactEntry entry, string storeDir)
    {
        string metaPath = Path.Combine(storeDir, SanitizeName(entry.Name) + ".meta.json");

        // Sort metadata for stable git diffs (G-29: multi-user merge safety)
        var sorted = entry with
        {
            Keywords = entry.Keywords?.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
            TermFrequencies = entry.TermFrequencies?
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            CodeRefs = entry.CodeRefs?
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            ChunkEntries = entry.ChunkEntries?.Select(ce => ce with
            {
                Keywords = ce.Keywords?.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                TermFrequencies = ce.TermFrequencies?
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            }).ToArray()
        };

        string json = JsonSerializer.Serialize(sorted, FileStoreJsonContext.Default.ArtifactEntry);
        string tmp = $"{metaPath}.{Environment.ProcessId}.tmp";
        FileRetry.Run(() => File.WriteAllText(tmp, json));
        FileRetry.Run(() => File.Move(tmp, metaPath, overwrite: true));
    }

    private static void AtomicWriteAllText(string path, string content)
    {
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        FileRetry.Run(() => File.WriteAllText(tmp, content));
        FileRetry.Run(() => File.Move(tmp, path, overwrite: true));
    }

    private static async Task AtomicWriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            await FileRetry.RunAsync(() => File.WriteAllTextAsync(tmp, content, ct), ct: ct);
            FileRetry.Run(() => File.Move(tmp, path, overwrite: true));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true)
    {
        string tmp = $"{destPath}.{Environment.ProcessId}.tmp";
        FileRetry.Run(() => File.Copy(sourcePath, tmp, overwrite: true));
        FileRetry.Run(() => File.Move(tmp, destPath, overwrite: overwrite));
    }

    private void DeleteSidecar(string name, string storeDir)
    {
        string metaPath = Path.Combine(storeDir, SanitizeName(name) + ".meta.json");
        if (File.Exists(metaPath))
            File.Delete(metaPath);
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

        // Write per-artifact sidecar metadata files
        WriteSidecars(entries, storeDir);

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
            string storeDir = GetStoreDirForScope(scope);
            Directory.CreateDirectory(storeDir);

            // Write the sidecar for this single entry
            WriteSidecar(entry, storeDir);

            // Invalidate cache and rebuild from sidecars (including legacy entries)
            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir);
            string? legacyDir = ResolveLegacyFallbackDir(scope);
            if (legacyDir is not null && Directory.Exists(legacyDir))
            {
                var legacyEntries = LoadEntriesFromSidecars(legacyDir);
                var namespacedNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var le in legacyEntries)
                {
                    if (!namespacedNames.Contains(le.Name))
                        entries.Add(le);
                }
            }
            _indexCache[scope] = new CachedIndex(entries);

            // Invalidate topic discovery cache when saving a topic scope
            if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
                _cachedTopics = null;
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
            string storeDir = GetStoreDirForScope(scope);
            string metaPath = Path.Combine(storeDir, SanitizeName(name) + ".meta.json");

            if (!File.Exists(metaPath))
                return false;

            // Delete the sidecar file
            File.Delete(metaPath);

            // Invalidate cache and rebuild from remaining sidecars (including legacy entries)
            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir);
            string? legacyDirR = ResolveLegacyFallbackDir(scope);
            if (legacyDirR is not null && Directory.Exists(legacyDirR))
            {
                var legacyEntries = LoadEntriesFromSidecars(legacyDirR);
                var namespacedNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var le in legacyEntries)
                {
                    if (!namespacedNames.Contains(le.Name))
                        entries.Add(le);
                }
            }
            _indexCache[scope] = new CachedIndex(entries);

            // Invalidate topic discovery cache when removing from a topic scope. Without
            // this, DiscoverTopics keeps reporting a topic that has no remaining entries
            // until the cache TTL eventually flushes — mirrors what SaveIndex/Upsert do
            // at the corresponding write paths.
            if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
                _cachedTopics = null;

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
        var entries = LoadEntriesFromSidecars(storeDir);

        // Fallback: migrate from legacy index.json
        if (entries.Count == 0)
        {
            entries = LoadEntriesFromLegacyIndex(storeDir);
            if (entries.Count > 0)
                WriteSidecars(entries, storeDir);
        }

        // Merge entries from legacy flat directory (new-path entries take precedence)
        string? legacyDir = ResolveLegacyFallbackDir(scope);
        if (legacyDir is not null)
        {
            var legacyEntries = LoadEntriesFromSidecars(legacyDir);
            if (legacyEntries.Count == 0)
                legacyEntries = LoadEntriesFromLegacyIndex(legacyDir);

            if (legacyEntries.Count > 0)
            {
                var existingNames = new HashSet<string>(
                    entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var le in legacyEntries)
                {
                    if (!existingNames.Contains(le.Name))
                        entries.Add(le);
                }
            }
        }

        return entries;
    }

    // ── Artifact file I/O ────────────────────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAllTextAsync(path, artifactText, ct);
        _artifactCache.Invalidate($"{scope}|{subject}|");
    }

    public async Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
    {
        string path = ArtifactPath(subject, scope);

        // Fall back to legacy flat path if the namespaced path doesn't have the file
        if (!File.Exists(path))
        {
            string? legacyDir = ResolveLegacyFallbackDir(scope);
            if (legacyDir is not null)
            {
                string legacyPath = Path.Combine(legacyDir, SanitizeName(subject) + ".nmp2");
                if (File.Exists(legacyPath))
                    path = legacyPath;
            }
        }

        // v2 path scope → v1 legacy file fallback via PathRouter
        if (!File.Exists(path))
        {
            string? v2Legacy = ResolveV2LegacyFilePath(subject, scope);
            if (v2Legacy is not null)
                path = v2Legacy;
        }

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

        // 2. file:// URI (backward compat — restricted to workspace)
        if (nameOrArtifact.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string filePath = Path.GetFullPath(nameOrArtifact[7..]);
            string scriniDir = Path.GetFullPath(Path.Combine(_workspaceRoot, ".scrinia"));
            if (!filePath.StartsWith(scriniDir, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"file:// URIs are restricted to the workspace .scrinia/ directory.");
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

        var searcher = new WeightedFieldScorer(RankerOptions);
        var candidates = ListScoped(scopes);
        // Derive topic infos directly from the candidates we already loaded above instead of
        // calling GatherTopicInfos — which would LoadIndex every topic scope a second time.
        // The cache absorbs the actual disk reads but every duplicate call still acquires a
        // shared file-lock and copies the entry list; on Synology Drive sync setups the
        // lock-file acquisition alone is non-trivial wear over a long daemon session.
        var topics = BuildTopicInfosFromCandidates(candidates);
        return searcher.SearchAll(query, candidates, topics, limit, supplementalScores);
    }

    private static List<TopicInfo> BuildTopicInfosFromCandidates(IReadOnlyList<ScopedArtifact> candidates)
    {
        var topics = new List<TopicInfo>();
        foreach (var group in candidates
            .Where(c => c.Scope.StartsWith("local-topic:", StringComparison.Ordinal))
            .GroupBy(c => c.Scope, StringComparer.OrdinalIgnoreCase))
        {
            string rawTopicPart = group.Key["local-topic:".Length..];
            string topicName = MemoryNaming.StripNamespacePrefix(rawTopicPart);
            int count = group.Count();
            topics.Add(new TopicInfo(
                Scope: group.Key,
                TopicName: topicName,
                EntryCount: count,
                Description: $"{topicName} ({count} {(count == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: group.Select(c => c.Entry.Name).ToArray()));
        }
        return topics;
    }

    // ── Filtering overloads (excludeTopics) ──────────────────────────────────

    /// <summary>
    /// Efficient override of the default interface method: filters at scope-resolution level
    /// rather than post-filtering, avoiding loading indices for excluded topics.
    /// </summary>
    public List<ScopedArtifact> ListScoped(string? scopes, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(excludeTopics))
            return ListScoped(scopes);

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

        foreach (string scope in ResolveReadScopes(scopes, excludeTopics))
        {
            foreach (var entry in LoadIndex(scope))
                result.Add(new ScopedArtifact(scope, entry));
        }
        return result;
    }

    /// <summary>
    /// Efficient override of the default interface method: filters candidates and topics
    /// at scope-resolution level before searching.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes, int limit, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(excludeTopics))
            return SearchAll(query, scopes, limit);

        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Use the workspace's configured ranker weights — without this override, the
        // excludeTopics path silently fell back to RankerOptions.Default no matter what
        // the user had set Scrinia:Search:Alpha:* to.
        var searcher = new WeightedFieldScorer(RankerOptions);
        var candidates = ListScoped(scopes, excludeTopics);
        // ListScoped(scopes, excludeTopics) already excluded the unwanted topic scopes from
        // candidates, so building topic infos from this filtered set automatically gives us
        // the same shape as GatherTopicInfos+filter without re-LoadIndex-ing.
        var topics = BuildTopicInfosFromCandidates(candidates);
        return searcher.SearchAll(query, candidates, topics, limit, supplementalScores: null);
    }

    /// <summary>
    /// Efficient override of the default interface method: resolves scopes then excludes
    /// the specified topic scopes.
    /// </summary>
    public IReadOnlyList<string> ResolveReadScopes(string? scopes, string? excludeTopics)
    {
        var resolved = ResolveReadScopes(scopes);
        if (string.IsNullOrWhiteSpace(excludeTopics))
            return resolved;
        var excluded = IMemoryStore.BuildExcludedScopeSet(excludeTopics);
        return resolved.Where(s => !excluded.Contains(s)).ToArray();
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
            AtomicWriteAllText(destPath, entry.Artifact);

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
        AtomicFileCopy(sourcePath, destPathP, overwrite);

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
            long originalBytes = Nmp2Strategy.Instance.Decode(artifactText).LongLength;

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

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string archiveName = $"{SanitizeName(subject)}_{timestamp}.nmp2";
        string archivePath = Path.Combine(versionsDir, archiveName);

        AtomicFileCopy(currentPath, archivePath);

        // Prune old versions — keep only the 10 most recent
        var versionFiles = Directory.GetFiles(versionsDir, $"{SanitizeName(subject)}_*.nmp2")
            .OrderByDescending(f => f)
            .Skip(10)
            .ToList();
        foreach (var old in versionFiles)
        {
            try { File.Delete(old); } catch { /* best-effort cleanup */ }
        }
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
        // Cache hit — only re-scans when explicitly invalidated by Upsert / SaveIndex.
        if (_cachedTopics is not null)
            return _cachedTopics;

        var results = new List<string>();
        var namespacedChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── v1: scan .scrinia/topics/ ────────────────────────────────────────
        string localTopicsRoot = Path.Combine(_workspaceRoot, ".scrinia", "topics");
        if (Directory.Exists(localTopicsRoot))
        {
            // Scan namespace subdirectories (entity/, memory/, agent/)
            foreach (string nsDir in Directory.GetDirectories(localTopicsRoot))
            {
                string nsDirName = Path.GetFileName(nsDir);
                if (!MemoryNaming.ReservedNamespaceDirs.Contains(nsDirName))
                    continue;

                if (nsDirName.Equals("agent", StringComparison.OrdinalIgnoreCase))
                {
                    // agent is a single scope (no children)
                    results.Add("local-topic:agent");
                }
                else
                {
                    // entity/ and memory/ contain child topic dirs
                    foreach (string childDir in Directory.GetDirectories(nsDir))
                    {
                        string childName = Path.GetFileName(childDir);
                        results.Add($"local-topic:{nsDirName}/{childName}");
                        namespacedChildren.Add(childName);
                    }
                }
            }

            // Legacy flat dirs: include those NOT in ReservedNamespaceDirs AND not shadowed by namespace children
            foreach (string flatDir in Directory.GetDirectories(localTopicsRoot))
            {
                string dirName = Path.GetFileName(flatDir);
                if (MemoryNaming.ReservedNamespaceDirs.Contains(dirName))
                    continue; // skip namespace dirs themselves
                if (namespacedChildren.Contains(dirName))
                    continue; // skip legacy dirs shadowed by namespaced entries
                results.Add($"local-topic:{dirName}");
            }
        }

        // ── v2: scan .scrinia/memories/ for hierarchical paths ───────────────
        string memoriesRoot = Path.Combine(_workspaceRoot, ".scrinia", "memories");
        if (Directory.Exists(memoriesRoot))
            DiscoverMemoriesRecursive(memoriesRoot, "", results);

        _cachedTopics = results.ToArray();
        return _cachedTopics;
    }

    /// <summary>
    /// Recursively scans the v2 memories directory tree and adds leaf directories
    /// (those containing .nmp2 or .meta.json files) as v2 scopes.
    /// </summary>
    private static void DiscoverMemoriesRecursive(string dir, string relativePath, List<string> results)
    {
        // Check if this directory itself is a leaf (contains artifact files)
        bool hasArtifacts = Directory.EnumerateFiles(dir, "*.nmp2").Any()
                         || Directory.EnumerateFiles(dir, "*.meta.json").Any();

        if (hasArtifacts && !string.IsNullOrEmpty(relativePath))
            results.Add($"local-topic:{relativePath}");

        // Recurse into subdirectories
        foreach (string subDir in Directory.GetDirectories(dir))
        {
            string subName = Path.GetFileName(subDir);
            if (subName.Equals("versions", StringComparison.OrdinalIgnoreCase))
                continue; // skip version archive dirs

            string childPath = string.IsNullOrEmpty(relativePath)
                ? subName
                : $"{relativePath}/{subName}";
            DiscoverMemoriesRecursive(subDir, childPath, results);
        }
    }

    public List<TopicInfo> GatherTopicInfos(string? scopes = null)
    {
        var topics = new List<TopicInfo>();
        foreach (string scope in ResolveReadScopes(scopes))
        {
            if (!scope.StartsWith("local-topic:", StringComparison.Ordinal))
                continue;

            string rawTopicPart = scope["local-topic:".Length..];
            string topicName = MemoryNaming.StripNamespacePrefix(rawTopicPart);
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
                AtomicWriteAllText(filePath, content);
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
