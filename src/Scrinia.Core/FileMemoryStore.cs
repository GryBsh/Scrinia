using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Instance-based <see cref="IMemoryStore"/> backed by the filesystem.
/// Each instance is scoped to a workspace root directory.
///
/// Naming convention (v3 — see <see cref="PathRouter.ToStoreDir"/>):
///   "subject"              → local scope:   {workspace}/.scrinia/memories/subject.nmp2
///   "/topic/subject"       → local topic:   {workspace}/.scrinia/memories/topic/subject.nmp2
///   "/temp/subject"         → ephemeral:     in-memory only (dies with instance)
/// </summary>
public sealed partial class FileMemoryStore : ILocalMemoryStore, IDisposable
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

    // Gate for the one-time v3 layout migration check — see MemoriesRoot below.
    private volatile bool _layoutChecked;
    private readonly object _layoutGate = new();

    // Merged cross-scope entity fact store. Invalidated event-driven (set to null) by
    // Upsert / Remove. Rebuilt lazily on next GetEntityFactStore() call from each scope's
    // facts.json sidecar (reused when its validity token matches the current index) or
    // re-derived from entries when the sidecar is missing/stale.
    //
    // _factStoreLock is NEVER held while building the merged view (that requires ListScoped
    // / LoadIndex, which take the per-scope index locks) — only at entry, to publish the
    // built result, and at Upsert/Remove's invalidation point. Upsert/Remove take the index
    // write-lock first and then (briefly) _factStoreLock; a build that instead held
    // _factStoreLock across an index-lock acquisition would invert that order and deadlock
    // against a concurrent mutation on the same scope. _factStoreGeneration guards against
    // caching a snapshot that a mutation invalidated mid-build: a build started before gen G
    // that finishes after a mutation bumped it to G+1 returns its result but does not cache it.
    private readonly object _factStoreLock = new();
    private EntityFactStore? _factStore;
    private long _factStoreGeneration;

    /// <summary>
    /// In-memory cache of a scope's index entries, handed out by reference on a
    /// <see cref="LoadIndex"/> cache hit.
    ///
    /// <para>This wraps a single list rather than being one because it is the documented home of
    /// the aliasing contract below — see
    /// docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md. It previously also
    /// carried a name-to-position map, a lazy BM25 corpus-stats cache and a rebuild method; none
    /// had a single reader anywhere in the repo, and the map was rebuilt on every cache write.
    /// Deleted 2026-08-10.</para>
    /// </summary>
    internal sealed class CachedIndex
    {
        /// <summary>The cached entries. Handed out by reference from LoadIndex — callers get a
        /// live view of this list, not a copy. That is safe only because no reader mutates it
        /// and every cache write REPLACES the CachedIndex rather than mutating this instance.
        /// A caller that needs to mutate must call ToList() explicitly, as ImportTopicEntries
        /// does.</summary>
        public List<ArtifactEntry> Entries { get; }

        public CachedIndex(List<ArtifactEntry> entries) => Entries = entries;
    }

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
    /// Tunable ranker weights — read by <see cref="SearchAll(SearchQuery)"/>
    /// when constructing the underlying <see cref="WeightedAttributeScorer"/>. Set at
    /// construction; <c>WorkspaceSetup.Configure</c> passes the workspace's
    /// <c>Scrinia:Search:*</c> config-derived values.
    /// </summary>
    public RankerOptions RankerOptions { get; init; } = RankerOptions.Default;

    public FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        RankerOptions = rankerOptions ?? RankerOptions.Default;
    }

    internal static void EnsureGitIgnore(string workspaceRoot)
    {
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        Directory.CreateDirectory(scriniaDir);

        string gitIgnorePath = Path.Combine(scriniaDir, ".gitignore");
        if (File.Exists(gitIgnorePath)) return;

        try
        {
            WriteDefaultGitIgnore(gitIgnorePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Benign first-write race. EnsureMaterialized calls this from OUTSIDE the scope's
            // file lock and write lock, so two concurrent writers to a fresh workspace both pass
            // the File.Exists check above and both File.Move onto the same destination. On
            // Windows the loser gets ERROR_ACCESS_DENIED (UnauthorizedAccessException), which
            // FileRetry classifies as permanent and will not retry — the same collision the
            // sidecar write-back documents in LoadIndexUnsafe. The content is a fixed constant,
            // so whichever move landed left the file correct. Rethrow only if it really is absent.
            if (!File.Exists(gitIgnorePath)) throw;
        }
    }

    private static void WriteDefaultGitIgnore(string gitIgnorePath)
    {
        AtomicWriteAllText(gitIgnorePath, """
            # Lock files (runtime artifacts from cross-process file locking)
            **/*.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Vector indexes (derived from topic content; rebuild with `scri reindex`)
            embeddings/

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
            var parsed = PathParser.Parse(name, new HashSet<string>());

            var segments = parsed.Segments;
            if (segments.Count == 0)
                throw new ArgumentException("Empty path", nameof(name));

            if (segments.Count == 1)
                return ("local", SanitizeName(segments[0].Value));

            // Build scope from all but last segment
            string topicPart = string.Join("/", segments.Take(segments.Count - 1).Select(s => s.Value));
            string scope = $"local:{topicPart}";
            string subject = SanitizeName(segments[^1].Value);
            return (scope, subject);
        }

        // A bare name is a local memory; the removed "topic:subject" form is rejected.
        MemoryNaming.RejectLegacyColonSyntax(name);
        return ("local", SanitizeName(name.Trim()));
    }

    public string FormatQualifiedName(string scope, string subject) =>
        scope.StartsWith("local:", StringComparison.Ordinal)
            ? $"/{scope["local:".Length..]}/{subject}"
            : subject;

    public bool IsEphemeral(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    // Characters invalid on Windows that Linux allows — always strip for portability.
    private static readonly HashSet<char> s_portableInvalid =
        [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '<', '>', '|', '"'];

    public string SanitizeName(string name) => SanitizeNameCore(name);

    /// <summary>Static core of <see cref="SanitizeName"/> — no instance state, so it is also
    /// usable from <see cref="WriteSidecarCore"/>/<see cref="ExpandLegacyIndexEntries"/>, which
    /// run without a <see cref="FileMemoryStore"/> instance (migration time).</summary>
    private static string SanitizeNameCore(string name)
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
                ContentPreview: e.ContentPreview,
                Keywords: e.Keywords,
                TermFrequencies: e.TermFrequencies,
                UpdatedAt: e.UpdatedAt,
                ChunkEntries: e.ChunkEntries,
                Attributes: e.Attributes);
            result.Add(new ScopedArtifact("ephemeral", artifactEntry));
        }
        return result;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The root every persistent path resolves under. Runs the one-time v3 layout migration
    /// on first use, which is why every path computation must flow through here — a method
    /// that builds a path another way would read a store that has not been migrated yet.
    ///
    /// <para>Double-checked locking, not a bare <c>Interlocked.Exchange</c> flag: a flag alone
    /// only stops a SECOND migration attempt — it does not stop a second (or 64th) concurrent
    /// caller from reading <c>memories/</c> WHILE the winning thread's migration is still moving
    /// files. <see cref="_layoutGate"/> makes every concurrent first-touch caller block until the
    /// one migration attempt actually finishes, so nobody ever observes a half-migrated tree.</para>
    /// </summary>
    private string MemoriesRoot
    {
        get
        {
            if (!_layoutChecked)
            {
                lock (_layoutGate)
                {
                    if (!_layoutChecked)
                    {
                        if (StoreLayoutMigration.NeedsMigration(_workspaceRoot))
                        {
                            var result = StoreLayoutMigration.MigrateIfNeeded(_workspaceRoot);
                            if (result.Ran)
                                Console.Error.WriteLine(
                                    $"scrinia: migrated {result.Moved} file(s) to the v3 layout" +
                                    (result.Conflicts > 0 ? $", {result.Conflicts} conflict(s) quarantined" : "") +
                                    (result.BackupPath is not null ? $"; backup at {result.BackupPath}"
                                        : result.BackupSkipped ? "; no backup written (a prior one already exists)"
                                        : ""));
                            _indexCache.Clear();
                            _cachedTopics = null;
                        }
                        _layoutChecked = true;
                    }
                }
            }
            return Path.Combine(_workspaceRoot, ".scrinia", "memories");
        }
    }

    public string GetStoreDirForScope(string scope)
    {
        _ = MemoriesRoot;   // gate
        return PathRouter.ToStoreDir(scope, _workspaceRoot);
    }

    public string WorkspaceRoot => _workspaceRoot;

    public string ArtifactPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".nmp2");

    internal string MetaPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirForScope(scope), SanitizeName(name) + ".meta.json");

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
        if (s.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return [s];

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
            if (!scope.StartsWith("local:", StringComparison.Ordinal))
                continue;

            string topicPart = scope["local:".Length..];

            // The topic part starts with the prefix at a path boundary — e.g.
            // prefix="goal/G-5" matches "goal/G-5", "goal/G-5/research" but NOT "goal/G-50".
            // v3: every scope's topic part is already flat (see MemoryNaming.BuildScopedTopicScope),
            // so there is no separate namespaced form left to also match against.
            if (MatchesPathPrefix(topicPart, prefix))
                matching.Add(scope);
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

        // Default (null): every discovered topic. There is no longer an "entity-classified"
        // category to exclude — see the commit that deleted IsEntityScope.
        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    private ReaderWriterLockSlim GetIndexLock(string scope) =>
        _indexLocks.GetOrAdd(scope, _ => new ReaderWriterLockSlim());

    private string GetLockPath(string scope) =>
        Path.Combine(GetStoreDirForScope(scope), ".lock");

    /// <summary>
    /// Creates the workspace store and the scope's directory. Called only from write paths, so
    /// a workspace that is never written to leaves no directory behind.
    /// </summary>
    private void EnsureMaterialized(string scope)
    {
        EnsureGitIgnore(_workspaceRoot);
        Directory.CreateDirectory(GetStoreDirForScope(scope));
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the scope's cross-process file lock and in-process
    /// write lock, materialising the store first. Every write goes through here so that
    /// "writes materialize, reads do not" holds in one place.
    /// </summary>
    private T WithExclusiveLock<T>(string scope, Func<T> body)
    {
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            return body();
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    private void WithExclusiveLock(string scope, Action body) =>
        WithExclusiveLock(scope, () => { body(); return true; });

    public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
    {
        var lk = GetIndexLock(scope);

        // Fast lane: an in-memory cache hit performs no I/O, so it needs no file handle.
        //
        // The shared file lock keeps a COLD read from seeing a half-written sidecar. It cannot
        // make a cache hit more correct: nothing invalidates _indexCache across processes, so a
        // hit already returns whatever this process last loaded, lock or no lock. Taking the
        // lock here bought a file handle open and close per call and no guarantee whatsoever.
        //
        // The in-process read lock STAYS. Writes replace the cache under EnterWriteLock (:560),
        // so dropping it here would newly open a same-process read-your-writes race — which is
        // not the same as the cross-process staleness above, and is not ours to introduce.
        //
        // Skipping GetLockPath also skips its GetStoreDirForScope call — which is load-bearing
        // for the layout-migration latch. Safe because every path that populates _indexCache
        // reaches it through GetStoreDirForScope first, so a cache entry cannot exist before
        // migration has been triggered. See
        // docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
        //
        // RULING (the-sweep, Task 1, G4): this EnterReadLock/ExitReadLock pair is deliberately
        // NOT pinned by a test, after a real attempt to write one — but it is not dead code, and
        // the race :558-560 names is real and present-day, not a hypothetical future one. This
        // ruling does not weaken that claim; it explains why nothing here can pin it.
        //
        // What it protects: every write runs inside WithExclusiveLock, which holds EnterWriteLock
        // across the write's ENTIRE body — sidecar I/O, LoadEntriesFromSidecars, and the final
        // `_indexCache[scope] = new CachedIndex(...)`. Because a reader takes EnterReadLock here,
        // a same-process reader on another thread blocks until that write finishes and therefore
        // always observes the post-write cache. Without this lock, the reader's lock-free
        // `_indexCache.TryGetValue` can run at ANY point during that write — not only the narrow
        // `TryRemove`-to-reset window — and return the PRE-write CachedIndex: a stale value, not a
        // torn one, since ConcurrentDictionary and CachedIndex's immutability rule out corruption.
        // This is exactly shape S2, rejected in
        // docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md ("S2 is rejected,
        // and the reason is not the one B2 covers") as a genuine, newly-introduced same-process
        // regression, distinct from the pre-existing cross-process staleness that lock skipping
        // is otherwise safe against. It names a live caller of this exact shape:
        // EntityTypingEventHandler.PromoteAsync (src/Scrinia/Services/EntityTypingEventHandler.cs:38)
        // calls LoadIndex(scope) from a detached background task fired by BackgroundWork.Fire ->
        // Task.Run (src/Scrinia.Core/BackgroundWork.cs:33-64) with no ordering guarantee against a
        // second concurrent MCP write to the same scope.
        //
        // Why no deterministic test pins it: the difference this lock makes is staleness, not
        // failure. A racing reader without it never throws and never sees a shrinking or torn
        // list — it just, on an unlucky interleaving, returns the answer from before the
        // concurrent write instead of after it. Whether a given run's race window lands before,
        // during, or after the writer's critical section is a timing accident with no black-box
        // hook to force it, so a test can observe one specific outcome but cannot make the race
        // land inside the dangerous window on demand — the miss is silent by construction, not
        // merely rare. A stress test built on the two natural black-box probes (exceptions,
        // shrinking entry counts) was run 300 times total — 50 rounds with this lock in place
        // first, to confirm those probes behave against a known-correct baseline, then 250 more
        // with the lock removed, hunting for either probe to trip — and neither probe ever fired
        // in either configuration, because neither one can detect staleness at all; a stale-but
        // well-formed CachedIndex looks identical to a fresh one to both checks. That silence is
        // not evidence the race is safe — it is evidence the probes were the wrong instrument.
        // Deleted rather than kept, so the suite does not claim coverage it never had.
        //
        // This lock must not be removed as dead code. It is the only thing keeping a same-process
        // racing reader's answer sequenced after any write already in flight against the same
        // scope, and EntityTypingEventHandler.PromoteAsync depends on that today.
        lk.EnterReadLock();
        try
        {
            // Live view, not a copy. Safe because no reader mutates the returned list — there is
            // no derived structure on CachedIndex left to desync — and every cache write REPLACES
            // the CachedIndex rather than mutating this instance.
            if (_indexCache.TryGetValue(scope, out var cached))
                return cached.Entries;
        }
        finally
        {
            lk.ExitReadLock();
        }

        using var fileLock = FileLock.AcquireShared(GetLockPath(scope));
        lk.EnterReadLock();
        try
        {
            // LoadIndexUnsafe re-checks the cache as its first statement, which covers the
            // window between the two acquisitions above — another thread may have populated it
            // while this one was waiting on the file lock.
            return LoadIndexUnsafe(scope);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }

    private List<ArtifactEntry> LoadIndexUnsafe(string scope)
    {
        // Live view — see the comment on the fast-path check in LoadIndex.
        if (_indexCache.TryGetValue(scope, out var cached))
            return cached.Entries;

        string storeDir = GetStoreDirForScope(scope);
        var entries = LoadEntriesFromSidecars(storeDir);

        // Read-time forward-fill: upgrade legacy carriers (tags/codeRefs/review/anchors/
        // recognized-key keywords) into the attributes model (Fields/Keywords) so every
        // reader sees Fields without a prior `scri migrate`. Primary-scope sidecars whose
        // shape actually changes are written back once here — guarded by MigratesTo, so a
        // later cold load of an already-upgraded sidecar performs no write — mirroring the
        // index.json→sidecar upgrade above. This durably promotes the on-disk data into
        // Fields before a later plan removes the legacy members, since STJ silently drops
        // unmapped members on serialize and would otherwise lose it. Purely additive:
        // legacy fields are never cleared, so scoring (Math.Max over legacy + attribute
        // reads) is unchanged.
        //
        // The write-back runs under LoadIndex's SHARED lock, so concurrent same-process cold
        // loads race to atomically replace the same sidecar. Unique per-call temp names (see
        // WriteSidecar) keep the temp writes independent, but the final File.Move(overwrite)
        // onto the shared destination can still collide — on Windows the loser surfaces as
        // UnauthorizedAccessException (ERROR_ACCESS_DENIED), which FileRetry does not retry.
        // That collision is benign: every writer serializes identical deterministic content,
        // so whichever move lands leaves the sidecar correct, and the in-memory upgrade below
        // already makes THIS read correct regardless. Persistence here is a best-effort
        // optimization (idempotent via MigratesTo — retried on the next cold load if a whole
        // round loses), so a failed write-back must never propagate out of a plain read path.
        for (int i = 0; i < entries.Count; i++)
        {
            if (AttributeMigration.MigratesTo(entries[i], out var upgraded))
            {
                entries[i] = upgraded;
                try { WriteSidecar(upgraded, storeDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort; see above */ }
            }
        }

        if (entries.Count > 0)
            _indexCache[scope] = new CachedIndex(entries);

        return entries;
    }

    private List<ArtifactEntry> LoadEntriesFromSidecars(string storeDir)
    {
        if (!Directory.Exists(storeDir)) return [];

        var entries = new List<ArtifactEntry>();
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json"))
        {
            try
            {
                // Retry the READ (only) on a transient lock: read-time forward-fill writes
                // sidecars under LoadIndex's shared lock, so a concurrent reader can hit the
                // File.Move replace window. Without the retry, that transient sharing violation
                // would fall into the blanket "skip corrupt" catch below and silently drop the
                // entry from this read. FileRetry retries transient IOExceptions and rethrows a
                // permanently-stuck file (which then degrades to skip, as before). Deserialize
                // stays OUTSIDE the retry so genuinely-corrupt JSON is still skipped, not retried.
                string json = FileRetry.Run(() => File.ReadAllText(metaFile));
                var entry = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.ArtifactEntry);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch { /* skip corrupt sidecar */ }
        }
        return entries;
    }

    /// <summary>
    /// Reads a legacy v1 <c>index.json</c>'s entries raw (no forward-fill, no write-back).
    /// Internal (not private): the read path's own fallback use of this was removed when the
    /// index.json → sidecar upgrade moved into <see cref="StoreLayoutMigration"/> (which calls
    /// this directly — see <c>ExpandLegacyIndexEntries</c>'s call site), but
    /// <see cref="EnumerateRawForMigration"/> still uses it as a belt-and-braces fallback for
    /// <see cref="Migration.AttributeMigration.MigrateStore"/>. In practice that fallback is
    /// unreachable once the workspace has gone through migration once, because every
    /// <see cref="GetStoreDirForScope"/> call gates on <see cref="MemoriesRoot"/> first — but it
    /// is still real, compiled, exercised code, not dead code eligible for deletion.
    /// </summary>
    internal static List<ArtifactEntry> LoadEntriesFromLegacyIndex(string storeDir)
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

    /// <summary>
    /// Yields every persistent entry across all scopes as RAW deserialized sidecars — WITHOUT the
    /// read-time forward-fill write-back that <see cref="LoadIndex"/>/<see cref="ListScoped(string?)"/>
    /// perform as a side effect of loading. Each item carries the entry, its scope, and the primary
    /// store directory the sidecar lives in. Read-only; used by
    /// <see cref="Migration.AttributeMigration.MigrateStore"/> to measure and persist accurately.
    /// </summary>
    internal IEnumerable<(ArtifactEntry Entry, string Scope, string StoreDir)> EnumerateRawForMigration()
    {
        // Guard against yielding the same on-disk sidecar twice — a defensive keying, kept even
        // though every scope now maps to exactly one v3 directory (no more shadowed legacy-flat-dir
        // target shared by two scopes to double-count).
        var yielded = new HashSet<(string Dir, string Name)>();
        foreach (string scope in ResolveReadScopes("all"))
        {
            string primaryDir = GetStoreDirForScope(scope);
            var primary = LoadEntriesFromSidecars(primaryDir);
            if (primary.Count == 0)
                primary = LoadEntriesFromLegacyIndex(primaryDir); // raw index.json fallback
            foreach (var entry in primary)
                if (yielded.Add((primaryDir, entry.Name.ToLowerInvariant())))
                    yield return (entry, scope, primaryDir);
        }
    }

    /// <summary>
    /// Writes an already-upgraded entry's sidecar to a SPECIFIC directory under <paramref name="scope"/>'s
    /// exclusive lock, then invalidates that scope's cached index. Unlike <see cref="Upsert"/> (which always
    /// targets <see cref="GetStoreDirForScope"/>), the explicit <paramref name="storeDir"/> lets a shadowed
    /// legacy-flat-dir entry be rewritten in place (its flat dir) rather than promoted into the namespaced
    /// scope dir. Used only by the migrate path.
    /// </summary>
    internal void PersistMigratedEntry(ArtifactEntry entry, string storeDir, string scope)
    {
        WithExclusiveLock(scope, () =>
        {
            Directory.CreateDirectory(storeDir);
            WriteSidecar(entry, storeDir);
            _indexCache.TryRemove(scope, out _); // next read rebuilds from the now-upgraded sidecars
            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;
        });
    }

    private void WriteSidecars(List<ArtifactEntry> entries, string storeDir)
    {
        foreach (var entry in entries)
            WriteSidecar(entry, storeDir);
    }

    private void WriteSidecar(ArtifactEntry entry, string storeDir) => WriteSidecarCore(entry, storeDir);

    /// <summary>
    /// Static core of sidecar writing: normalize, sort for stable diffs, serialize via
    /// <see cref="FileStoreJsonContext"/>, atomic temp+move. No instance state, so it is also
    /// used directly by <see cref="ExpandLegacyIndexEntries"/>, which runs from
    /// <see cref="StoreLayoutMigration"/> before any <see cref="FileMemoryStore"/> instance
    /// exists — that call site depends on this producing byte-identical sidecars to a normal
    /// store write.
    /// </summary>
    private static void WriteSidecarCore(ArtifactEntry entry, string storeDir)
    {
        // Write chokepoint: guarantees the on-disk sidecar is always keyed "attributes", never
        // the pre-rename "fields", regardless of which caller reached here (read-time forward-fill,
        // PersistMigratedEntry, the legacy-index expansion, or a plain Upsert of an entry that still
        // carries a legacy value). No-op for an already-normalized or freshly-constructed entry.
        entry = entry.NormalizeAttributesKey();

        string metaPath = Path.Combine(storeDir, SanitizeNameCore(entry.Name) + ".meta.json");

        // Sort metadata for stable git diffs (G-29: multi-user merge safety)
        var sorted = entry with
        {
            Keywords = entry.Keywords?.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
            TermFrequencies = entry.TermFrequencies?
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            FileHashes = entry.FileHashes?
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
        // Unique-per-call temp name (ProcessId + Guid): read-time forward-fill writes sidecars
        // from inside LoadIndex's shared lock, so concurrent cold loads of the same un-migrated
        // sidecar must not race a shared temp name (first File.Move consumes it, second throws
        // FileNotFound — permanent in FileRetry). Content is deterministic and File.Move atomic.
        string tmp = $"{metaPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileRetry.Run(() => File.WriteAllText(tmp, json));
            FileRetry.Run(() => File.Move(tmp, metaPath, overwrite: true));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Expands a legacy v1 <c>index.json</c>'s entries into individual <c>.meta.json</c> sidecars
    /// written to <paramref name="targetDir"/>, reusing <see cref="WriteSidecarCore"/> so the
    /// output is byte-identical to what this store writes normally. Called by
    /// <see cref="StoreLayoutMigration"/> after it has moved every real content file into place,
    /// so this is what makes an <c>index.json</c>-only legacy directory's entries visible again
    /// after the merge — see the migration's class doc comment.
    ///
    /// <para>Skips an entry whose sidecar already exists at the target: a real
    /// <c>.meta.json</c> merged in from a sibling legacy directory (e.g. the flat and
    /// <c>memory/</c>-namespaced forms of the same topic) describes the same artifact more
    /// currently than a legacy index.json snapshot ever can, so it wins rather than being
    /// clobbered.</para>
    /// </summary>
    internal static void ExpandLegacyIndexEntries(List<ArtifactEntry> entries, string targetDir)
    {
        if (entries.Count == 0) return;

        Directory.CreateDirectory(targetDir);
        foreach (var entry in entries)
        {
            string metaPath = Path.Combine(targetDir, SanitizeNameCore(entry.Name) + ".meta.json");
            if (File.Exists(metaPath)) continue;
            WriteSidecarCore(entry, targetDir);
        }
    }

    /// <summary>Internal (not private): also used by <see cref="StoreLayoutMigration"/> to update
    /// the WORKSPACE-level <c>.gitignore</c> (a different file from the <c>.scrinia/.gitignore</c>
    /// this class's own <see cref="EnsureGitIgnore"/> writes), so that write goes through the same
    /// atomic-temp-then-move convention as every other artifact write in this codebase rather than
    /// a bare <c>File.WriteAllText</c>.</summary>
    internal static void AtomicWriteAllText(string path, string content)
    {
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileRetry.Run(() => File.WriteAllText(tmp, content));
            FileRetry.Run(() => File.Move(tmp, path, overwrite: true));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static async Task AtomicWriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
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
        string tmp = $"{destPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileRetry.Run(() => File.Copy(sourcePath, tmp, overwrite: true));
            FileRetry.Run(() => File.Move(tmp, destPath, overwrite: overwrite));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private void DeleteSidecar(string name, string storeDir)
    {
        string metaPath = Path.Combine(storeDir, SanitizeName(name) + ".meta.json");
        if (File.Exists(metaPath))
            File.Delete(metaPath);
    }

    /// <summary>
    /// Persists <paramref name="entries"/> as the full index for <paramref name="scope"/> and takes
    /// ownership of the list instance: it becomes the scope's in-memory cache directly, not a copy.
    /// The caller must not retain a reference to <paramref name="entries"/> or mutate it after this
    /// call — doing so corrupts the cache and, since readers now get that same list by reference
    /// from <see cref="LoadIndex"/>, can surface as an <see cref="InvalidOperationException"/> inside
    /// an unrelated concurrent enumerator. Pass a fresh or already-copied list (see
    /// <see cref="ImportTopicEntries"/> for the pattern).
    /// </summary>
    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
    {
        WithExclusiveLock(scope, () => SaveIndexUnsafe(entries, scope));
    }

    private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        string storeDir = GetStoreDirForScope(scope);
        Directory.CreateDirectory(storeDir);

        // Delete orphaned sidecars: any *.meta.json whose entry is no longer present. Runs under the
        // caller's write lock; storeDir holds only artifact sidecars, so this is scope-local and safe.
        var expected = new HashSet<string>(
            entries.Select(e => SanitizeName(e.Name) + ".meta.json"),
            StringComparer.OrdinalIgnoreCase);
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
            if (!expected.Contains(Path.GetFileName(metaFile)))
                File.Delete(metaFile);

        // Write per-artifact sidecar metadata files
        WriteSidecars(entries, storeDir);

        // Update the in-memory cache
        _indexCache[scope] = new CachedIndex(entries);

        // Invalidate topic discovery cache when saving a topic scope
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _cachedTopics = null;

        lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }
    }

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        WithExclusiveLock(scope, () =>
        {
            string storeDir = GetStoreDirForScope(scope);
            Directory.CreateDirectory(storeDir);

            // Write the sidecar for this single entry
            WriteSidecar(entry, storeDir);

            // Invalidate cache and rebuild from sidecars. Coalesce each entry's legacy "fields"
            // key here (in-memory only, no write-back) so a write-first session — an Upsert
            // before any cold LoadIndex on this scope — never caches an entry with Attributes ==
            // null while its data sits in LegacyFields; that would silently hide the entry's
            // facets from search/filter/display for the rest of the process lifetime. Mirrors
            // LoadIndexUnsafe's cold-path forward-fill.
            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir).Select(e => e.NormalizeAttributesKey()).ToList();
            _indexCache[scope] = new CachedIndex(entries);

            // Invalidate topic discovery cache when saving a topic scope
            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;

            lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }
        });
    }

    public bool Remove(string name, string scope = "local")
    {
        return WithExclusiveLock(scope, () =>
        {
            string storeDir = GetStoreDirForScope(scope);
            string metaPath = Path.Combine(storeDir, SanitizeName(name) + ".meta.json");

            if (!File.Exists(metaPath))
            {
                // RULING (remote-mode-leftovers, Task 2): pinned by
                // CaseSensitiveFileStoreTests.Remove_DeletesCaseMismatchedName in
                // tests/Scrinia.Tests, which runs against a directory carrying NTFS's per-directory
                // case-sensitivity flag — so this is exercised on Windows, not only on a Linux CI
                // leg. The test skips (never silently passes) on a volume that cannot provide the
                // flag.
                //
                // SanitizeName does not fold case, so on a case-sensitive filesystem the exact-name
                // probe above answers "no such memory" for a sidecar that differs only in casing —
                // forget("mixedcase") reporting not-found for a memory stored as "MixedCase", while
                // every name match around it (LoadIndex, TryUpdateEntry, the write paths) is
                // case-insensitive. This scan restores the agreement. NTFS folds case itself by
                // default, so on an ordinary Windows volume the probe above always succeeds and this
                // branch is never entered — the guard test above uses a directory that opts out of
                // that default, which is what exercises it.
                //
                // Kept as a fallback rather than replacing the probe: the exact-name hit is the
                // overwhelmingly common case and costs one stat, where this costs a directory
                // enumeration. Nothing about the found path changes.
                //
                // Tie-break, and it matters because this is a delete: a case-sensitive filesystem
                // holding pre-branch data can have BOTH "Note.meta.json" and "note.meta.json", and
                // this branch is only entered when the caller asked for neither exactly (an exact
                // name would have hit the probe above). Directory enumeration order is not
                // guaranteed — notably on ext4 — so the candidates are sorted ordinally and the
                // first is taken, making the choice deterministic rather than arbitrary. Ordinal
                // sort puts uppercase first, so "Note.meta.json" wins over "note.meta.json". That
                // is a coin toss decided consistently, not a claim about which one is meant; the
                // alternative considered was refusing on ambiguity, rejected because it would make
                // forget() fail with no name the caller could give it instead.
                string wanted = Path.GetFileName(metaPath);
                string? actual = Directory.Exists(storeDir)
                    ? Directory.EnumerateFiles(storeDir, "*.meta.json")
                        .Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .FirstOrDefault()
                    : null;
                if (actual is null)
                    return false;
                metaPath = actual;
            }

            // Delete the sidecar file
            File.Delete(metaPath);

            // Invalidate cache and rebuild from remaining sidecars. Coalesce legacy "fields" here
            // too, in-memory only — see the matching comment in Upsert for why a write-first
            // Remove must not cache an un-coalesced entry.
            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir).Select(e => e.NormalizeAttributesKey()).ToList();
            _indexCache[scope] = new CachedIndex(entries);

            // Invalidate topic discovery cache when removing from a topic scope. Without
            // this, DiscoverTopics keeps reporting a topic that has no remaining entries
            // until the cache TTL eventually flushes — mirrors what SaveIndex/Upsert do
            // at the corresponding write paths.
            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;

            lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }

            return true;
        });
    }

    /// <summary>
    /// Atomically load-mutate-persist the entry for <paramref name="subject"/> in <paramref
    /// name="scope"/> under a single acquisition of the per-scope write lock. Returns false when
    /// the entry is missing or <paramref name="mutate"/> returns null (no write). This is the seam
    /// the async typing sink uses to write attribute promotions back — it fires NO event sinks.
    /// Resolves identity from the sidecars themselves (case-insensitively, like every other name
    /// match in this type) and reuses <see cref="Upsert"/>'s persist/invalidate tail.
    /// </summary>
    public bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        return WithExclusiveLock(scope, () =>
        {
            string storeDir = GetStoreDirForScope(scope);

            // RULING (remote-mode-leftovers, Task 2): the `File.Exists(SanitizeName(subject) +
            // ".meta.json")` fast path that used to stand here is gone, deliberately.
            //
            // What it did: SanitizeName does not fold case, so on a case-sensitive filesystem the
            // gate answered "no such entry" for a subject whose sidecar differs only in casing, and
            // TryUpdateEntry returned false for an entry that exists — without invoking mutate, so
            // the caller could not tell it apart from a genuine miss. The lookup immediately below
            // is already case-insensitive and is the authority; the gate was an optimization that
            // could only ever disagree with it by being wrong.
            //
            // Pinned by CaseSensitiveFileStoreTests.TryUpdateEntry_ResolvesCaseMismatchedSubject in
            // tests/Scrinia.Tests, which runs against a directory carrying NTFS's per-directory
            // case-sensitivity flag — so this is exercised on Windows, not only on a Linux CI leg.
            // The test skips (never silently passes) on a volume that cannot provide the flag. NTFS
            // is case-insensitive by default, so on an ordinary Windows volume File.Exists would
            // succeed for a case-mismatched subject either way and the removed gate would never have
            // disagreed with the lookup below; the guard test's directory opts out of that default.
            //
            // What is unchanged: "entry genuinely missing -> false, and mutate never runs".
            // LoadEntriesFromSidecars returns [] for a directory that does not exist, so the
            // `current is null` guard below answers that case exactly as the gate did. Pinned by
            // MissingEntry_ReturnsFalse in the same file.
            //
            // What it cost, since the gate was not free: it was the only thing keeping a MISS from
            // paying a full LoadEntriesFromSidecars scan, which a miss now always pays. Judged
            // fine, and deliberately so — a hit already paid that identical scan (the lookup below
            // is unchanged), and the sole production caller is EntityTypingEventHandler.PromoteAsync
            // on a detached background task, one call per write event, never in a loop. Trading a
            // correct answer for a stat on the rare path is the wrong way round.
            var current = LoadEntriesFromSidecars(storeDir)
                .Select(e => e.NormalizeAttributesKey())
                .FirstOrDefault(e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
            if (current is null) return false;

            var updated = mutate(current);
            if (updated is null) return false;

            WriteSidecar(updated, storeDir);

            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir).Select(e => e.NormalizeAttributesKey()).ToList();
            _indexCache[scope] = new CachedIndex(entries);

            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _cachedTopics = null;

            lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }

            return true;
        });
    }

    // ── The write surface: raw input in, derived entry out ───────────────────

    /// <inheritdoc/>
    public async Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Content is null || write.Content.Length == 0)
            throw new ArgumentException("A store writes at least one chunk.", nameof(write));

        // Ephemerality is the store's question, not the pipeline's — it decides where the memory
        // lands, and it suppresses the auto-derived review condition (D-05).
        bool ephemeral = IsEphemeral(write.Name);

        // The one call. `write.Keywords` is the caller's RAW list by MemoryWrite's contract (D-01);
        // nothing this method computes is ever fed back in as input.
        var derived = MemoryDerivation.ForStore(
            write.Content, write.Description, write.Attributes, write.Keywords,
            isEphemeral: ephemeral,
            fileHasher: HashWorkspaceFile);

        string artifact = write.Content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(write.Content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(write.Content);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(string.Concat(write.Content));

        if (ephemeral)
        {
            var existingEph = GetEphemeral(MemoryNaming.StripEphemeralPrefix(write.Name));
            return PersistEphemeral(write.Name, artifact, originalBytes, chunkCount, derived,
                createdAt: existingEph?.CreatedAt ?? DateTimeOffset.UtcNow,
                updatedAt: existingEph is not null ? DateTimeOffset.UtcNow : null);
        }

        string scope = write.Scope;
        string subject = write.Name;

        // Case-insensitive, and the resolved entry's own casing travels forward. SanitizeName does
        // not fold case, so an ordinal match here reports "new memory" for a re-store under a
        // different casing — which skips the archive below and resets CreatedAt while the write
        // still lands on the same file on a case-insensitive filesystem. Matching the name is only
        // half of it: everything below turns `subject` back into a path (ArchiveVersion,
        // WriteArtifactAsync, the sidecar written by Upsert, and the entry's own Name), so leaving
        // the caller's casing here would leave the fix depending on the filesystem to fold case on
        // its behalf — right on NTFS, and a twin alongside the original anywhere case-sensitive.
        // Twelve sites of this exact shape were fixed across two earlier branches; this is not one.
        var existingEntry = LoadIndex(scope).FirstOrDefault(
            e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        if (existingEntry is not null)
            subject = existingEntry.Name;

        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        if (existingEntry is not null)
            ArchiveVersion(subject, scope);

        await WriteArtifactAsync(subject, scope, artifact, ct: ct);

        var entry = BuildDerivedEntry(subject, scope, originalBytes, chunkCount, createdAt, updatedAt, derived);

        try
        {
            Upsert(entry, scope);
        }
        catch
        {
            // Only a BRAND-NEW memory is rolled back, and the rollback is removing the artifact
            // nothing points at. An existing memory is left alone: its prior content is in
            // versions/ and its sidecar still describes it, whereas deleting on a transient sidecar
            // write failure would destroy a memory the caller only asked to overwrite.
            if (existingEntry is null)
            {
                try { DeleteArtifact(subject, scope); } catch { /* best-effort rollback */ }
            }
            throw;
        }

        return entry;
    }

    /// <inheritdoc/>
    public async Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Content is null || write.Content.Length != 1)
            throw new ArgumentException(
                "An append adds exactly one chunk, so MemoryWrite.Content must hold one element.",
                nameof(write));

        bool ephemeral = IsEphemeral(write.Name);
        string scope = write.Scope;
        string subject = write.Name;
        ArtifactEntry? existingEntry = null;
        EphemeralEntry? existingEph = null;
        string? existingArtifact = null;

        if (ephemeral)
        {
            existingEph = GetEphemeral(MemoryNaming.StripEphemeralPrefix(write.Name));
            existingArtifact = existingEph?.Artifact;
        }
        else
        {
            // Identity is resolved from the index BEFORE the artifact is read, and the ORDER is the
            // fix: reading under the caller's casing threw FileNotFoundException on a case-sensitive
            // filesystem, fell through to the create-new path, and overwrote the memory with the
            // appended chunk alone — append() silently behaving as replace().
            existingEntry = LoadIndex(scope).FirstOrDefault(
                e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is not null)
                subject = existingEntry.Name;

            try { existingArtifact = await ReadArtifactAsync(subject, scope, ct); }
            catch (FileNotFoundException) { /* nothing to append to — created below */ }
        }

        if (existingArtifact is null)
            return await StoreAsync(write, ct);

        string appended = write.Content[0];
        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, appended);

        // The pipeline derives over the memory's FULL post-append content with the appended chunk
        // last (D-22 reads only that last element for attributes and the new chunk entry).
        int priorChunks = Nmp2ChunkedEncoder.GetChunkCount(existingArtifact);
        string[] fullContent = new string[priorChunks + 1];
        for (int i = 0; i < priorChunks; i++)
            fullContent[i] = Nmp2ChunkedEncoder.DecodeChunk(existingArtifact, i + 1);
        fullContent[priorChunks] = appended;

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(string.Concat(fullContent));

        // Everything the memory already carries that an append must not destroy. A server-side REST
        // append used to lose the curated keywords (D-12) and the drift baseline (D-21) outright.
        // EphemeralEntry has no FileHashes to carry — see PersistEphemeral.
        var existing = new ExistingMetadata(
            Attributes: existingEntry?.Attributes ?? existingEph?.Attributes,
            Keywords: existingEntry?.Keywords ?? existingEph?.Keywords,
            ContentPreview: existingEntry?.ContentPreview ?? existingEph?.ContentPreview,
            ChunkEntries: existingEntry?.ChunkEntries ?? existingEph?.ChunkEntries,
            FileHashes: existingEntry?.FileHashes);

        var derived = MemoryDerivation.ForAppend(
            fullContent, write.Description, write.Attributes, write.Keywords, existing);

        if (ephemeral)
            return PersistEphemeral(write.Name, newArtifact, originalBytes, chunkCount, derived,
                createdAt: existingEph!.CreatedAt, updatedAt: DateTimeOffset.UtcNow);

        // The bytes to revert to, already in hand from the read above: appending to an existing
        // memory must restore its content on failure, not delete it. Nothing reads versions/ back,
        // so a delete here would leave the user no way to recover.
        string? previousArtifact = existingEntry is not null ? existingArtifact : null;
        if (existingEntry is not null)
            ArchiveVersion(subject, scope);

        await WriteArtifactAsync(subject, scope, newArtifact, ct: ct);

        var entry = BuildDerivedEntry(subject, scope, originalBytes, chunkCount,
            createdAt: existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            derived);

        try
        {
            Upsert(entry, scope);
        }
        catch
        {
            // CancellationToken.None: a cancelled append must still be rolled back.
            try
            {
                if (previousArtifact is not null)
                    await WriteArtifactAsync(subject, scope, previousArtifact, ct: CancellationToken.None);
                else
                    DeleteArtifact(subject, scope);
            }
            catch { /* best-effort rollback */ }
            throw;
        }

        return entry;
    }

    /// <summary>Assembles the index entry from the derived half plus the store's own bookkeeping.
    /// <see cref="AttributeMigration.Forward"/> is the same safety net both pre-collapse write paths
    /// ran: idempotent here (the pipeline already folds file hashes and routes <c>key:value</c>
    /// keywords), and load-bearing for a pre-attributes entry appended to for the first time.</summary>
    private ArtifactEntry BuildDerivedEntry(
        string subject, string scope, long originalBytes, int chunkCount,
        DateTimeOffset createdAt, DateTimeOffset? updatedAt, DerivedMetadata derived) =>
        AttributeMigration.Forward(new ArtifactEntry(
            Name: subject,
            Uri: ArtifactUri(subject, scope),
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: derived.ContentPreview,
            Keywords: derived.Keywords.Length > 0 ? derived.Keywords : null,
            TermFrequencies: derived.TermFrequencies.Count > 0 ? derived.TermFrequencies : null,
            UpdatedAt: updatedAt,
            ChunkEntries: derived.ChunkEntries,
            FileHashes: derived.FileHashes,
            Attributes: derived.Attributes));

    /// <summary>Persists an ephemeral write and projects it back as an <see cref="ArtifactEntry"/>,
    /// the way <see cref="ListEphemeral"/> does.
    /// <para><see cref="EphemeralEntry"/> has no <c>FileHashes</c> field, so the projection reports
    /// none — it describes what was stored, not what was derived. The hashed paths themselves
    /// survive as the searchable <c>file</c> attribute. Ephemerals had no drift baseline before this
    /// surface either; giving them one is a storage-model change, not a write-path one.</para></summary>
    private ArtifactEntry PersistEphemeral(
        string name, string artifact, long originalBytes, int chunkCount,
        DerivedMetadata derived, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        string key = MemoryNaming.StripEphemeralPrefix(name);
        string[]? keywords = derived.Keywords.Length > 0 ? derived.Keywords : null;
        Dictionary<string, int>? tf = derived.TermFrequencies.Count > 0 ? derived.TermFrequencies : null;

        RememberEphemeral(key, new EphemeralEntry(
            Name: key,
            Artifact: artifact,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: derived.ContentPreview,
            Keywords: keywords,
            TermFrequencies: tf,
            UpdatedAt: updatedAt,
            ChunkEntries: derived.ChunkEntries,
            Attributes: derived.Attributes));

        return new ArtifactEntry(
            Name: key,
            Uri: "",
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: derived.ContentPreview,
            Keywords: keywords,
            TermFrequencies: tf,
            UpdatedAt: updatedAt,
            ChunkEntries: derived.ChunkEntries,
            Attributes: derived.Attributes);
    }

    /// <summary>
    /// The pipeline's one contact with the filesystem (D-04): resolves a <c>file</c> attribute value
    /// against this store's workspace root and hashes it, or returns null when it cannot — outside
    /// the workspace, missing, or unreadable.
    /// <para>Supplying this is what gives a REMOTE write a drift baseline. The old rule was "hash
    /// only when the store is local", so a write through <c>HttpMemoryStore</c> got none at all;
    /// derivation now runs inside whichever store owns the workspace, and the server's own store is
    /// a <see cref="FileMemoryStore"/> with one.</para>
    /// </summary>
    private string? HashWorkspaceFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath.Trim()));
            if (!PathContainment.IsWithin(_workspaceRoot, full) || !File.Exists(full))
                return null;
            return Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full)));
        }
        catch
        {
            // An unhashable reference is not a failed write — the memory stores without a baseline.
            return null;
        }
    }

    // ── Artifact file I/O ────────────────────────────────────────────────────

    public async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
    {
        // meta is deliberately ignored. The local store receives an entry's metadata through
        // Upsert immediately after this call, which is richer than meta and is the authority.
        // The parameter exists for HttpMemoryStore, whose Upsert is a no-op because the server
        // owns persistence — for it, this call is the only channel the metadata has.
        string path = ArtifactPath(subject, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAllTextAsync(path, artifactText, ct);
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
        if (!File.Exists(path))
        {
            // RULING (remote-mode-leftovers, final fixes, Finding 2): this fallback is the exact
            // counterpart of the one in Remove above.
            //
            // forget() calls Remove and DeleteArtifact as a pair (MemoryTools.Core.cs, and
            // MemoryOrchestrator.ForgetAsync). Remove's name match is case-insensitive; this one was
            // not. On a case-sensitive filesystem forget("mixedcase") against a memory stored as
            // "MixedCase" therefore returned removed=true, deleted=false — the caller reported
            // "Forgot: mixedcase." and the server answered success, while MixedCase.nmp2 stayed on
            // disk with its sidecar gone: orphaned, unreachable through list() or search(), and
            // invisible to the next forget(). Before the sidecar lookup became case-insensitive both
            // halves returned false and the user got a clean "no artifact found"; fixing one half
            // and not the other turned a correct no-op into a partial delete reported as success.
            //
            // Fixed here rather than at the three forget() call sites: DeleteArtifact is a public
            // store primitive, the pairing with Remove is what has to agree, and the file:// forget
            // path (MemoryTools.Core.cs) sweeps every read scope with a name taken from a URI and
            // has no entry in hand to canonicalize from. Aligning the primitive fixes all callers,
            // including any added later; canonicalizing at call sites fixes only the ones edited.
            //
            // Pinned by CaseSensitiveFileStoreTests.DeleteArtifact_DeletesCaseMismatchedName in
            // tests/Scrinia.Tests, which runs against a directory carrying NTFS's per-directory
            // case-sensitivity flag — so this is exercised on Windows, not only on a Linux CI leg.
            // The test skips (never silently passes) on a volume that cannot provide the flag. NTFS
            // folds case by default, so on an ordinary Windows volume the probe above always
            // succeeds and this branch is never entered; the guard test's directory opts out of
            // that default, which is what exercises it.
            //
            // Tie-break: identical to Remove's, and it matters for the same reason — this is a
            // delete, a case-sensitive filesystem holding pre-branch data can hold both
            // "Note.nmp2" and "note.nmp2", and this branch is only entered when the caller asked
            // for neither exactly. Directory enumeration order is not guaranteed, so candidates are
            // sorted ordinally and the first is taken. Ordinal sort puts uppercase first, so
            // "Note.nmp2" wins over "note.nmp2" — the same coin, decided the same way, as Remove's.
            string dir = Path.GetDirectoryName(path)!;
            string wanted = Path.GetFileName(path);
            string? actual = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.nmp2")
                    .Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (actual is null) return false;
            path = actual;
        }

        File.Delete(path);
        // Invalidate covers the differently-cased key too: ArtifactLruCache.Invalidate matches its
        // prefix with OrdinalIgnoreCase, and read keys are "{scope}|{subject}|{ticks}".
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
            if (!PathContainment.IsWithin(scriniDir, filePath))
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

    // ── Entity Facts ─────────────────────────────────────────────────────────

    /// <summary>
    /// The merged cross-scope <see cref="EntityFactStore"/>. Built lazily and cached; invalidated on
    /// any Upsert/Remove. Each scope's facts persist in a <c>facts.json</c> sidecar, reused when its
    /// validity token matches the current index and rebuilt-and-rewritten otherwise (never trusted blindly).
    /// </summary>
    public EntityFactStore GetEntityFactStore()
    {
        long gen;
        lock (_factStoreLock)
        {
            if (_factStore is not null) return _factStore;   // fast path
            gen = _factStoreGeneration;
        }

        // Build WITHOUT holding _factStoreLock — ListScoped/LoadIndex take the per-scope index
        // locks below, and Upsert/Remove acquire those in the opposite order (index lock, then
        // _factStoreLock) to invalidate. Holding _factStoreLock across this loop would invert
        // that order and deadlock against a concurrent mutation on the same scope.
        var merged = new List<MemoryEntities>();
        foreach (var group in ListScoped(null).GroupBy(sa => sa.Scope, StringComparer.Ordinal))
        {
            string scope = group.Key;
            var items = group.ToList();

            // "ephemeral" (and any other scope with no on-disk store) has no directory to
            // hold a sidecar — GetStoreDirForScope throws for it. Derive its facts in-memory
            // only; everything else about the merge is unaffected.
            string? sidecarPath = null;
            try { sidecarPath = Path.Combine(GetStoreDirForScope(scope), "facts.json"); }
            catch (ArgumentException) { /* no on-disk store for this scope */ }

            string token = EntityFactSidecar.ComputeToken(
                items.Select(sa => ($"{scope}|{sa.Entry.Name}",
                                    sa.Entry.UpdatedAt ?? sa.Entry.CreatedAt,
                                    EntitySignature(sa.Entry))));

            IReadOnlyList<MemoryEntities>? mems = null;
            if (sidecarPath is not null && File.Exists(sidecarPath))
            {
                var loaded = EntityFactSidecar.TryLoad(SafeReadAllText(sidecarPath));
                if (loaded is { } l && l.Token == token) mems = l.Memories;
            }

            if (mems is null)
            {
                mems = items.Select(EntityFactDerivation.Derive).Where(m => m is not null).Select(m => m!).ToArray();
                if (sidecarPath is not null)
                    TryWriteAllText(sidecarPath, EntityFactSidecar.Serialize(token, mems));
            }

            merged.AddRange(mems);
        }
        var built = new EntityFactStore(merged);

        lock (_factStoreLock)
        {
            if (_factStore is not null) return _factStore;          // another thread published while we built
            if (_factStoreGeneration == gen) _factStore = built;    // no mutation during build -> safe to cache
            return built;                                           // else return fresh but don't cache a stale snapshot
        }
    }

    /// <summary>
    /// Deterministic, order-independent summary of everything <see cref="EntityFactDerivation.Derive"/>
    /// reads from an entry — <c>Attributes</c> and <c>Keywords</c> (the same union
    /// <c>EntityGraph.ParseEntityKeywords ∪ ParseEntityAttributes</c> consumes) — for use as the
    /// content component of a <see cref="EntityFactSidecar.ComputeToken"/> entry. Attribute keys and,
    /// within each key, its values are sorted so two attribute dictionaries that differ only in
    /// insertion/enumeration order produce the same signature. Keywords are sorted the same way.
    /// Over-invalidating on a non-entity field (e.g. capitalization noise elsewhere) is acceptable —
    /// it just costs a safe rebuild; under-invalidating (missing a real entity change) is the bug
    /// this signature exists to close.
    /// </summary>
    private static string EntitySignature(ArtifactEntry entry)
    {
        string attrs = entry.Attributes is null
            ? string.Empty
            : string.Join(";", entry.Attributes
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key.ToLowerInvariant()}=[{string.Join(",", kv.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))}]"));

        string kws = entry.Keywords is null
            ? string.Empty
            : string.Join(",", entry.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        return $"{attrs}|kw=[{kws}]";
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch { return string.Empty; }
    }

    private static void TryWriteAllText(string path, string content)
    {
        try { File.WriteAllText(path, content); } catch { /* in-memory store is still correct */ }
    }

    private static List<TopicInfo> BuildTopicInfosFromCandidates(IReadOnlyList<ScopedArtifact> candidates)
    {
        var topics = new List<TopicInfo>();
        foreach (var group in candidates
            .Where(c => c.Scope.StartsWith("local:", StringComparison.Ordinal))
            .GroupBy(c => c.Scope, StringComparer.OrdinalIgnoreCase))
        {
            string topicName = group.Key["local:".Length..];
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
    /// Filters candidates and topics BEFORE the scorer's top-K cut, so a selective filter fills to
    /// <c>Limit</c> instead of returning the post-limit remnant. Every filter dimension is a
    /// property of the parent candidate (path/attributes) or the topic (scope label); chunk results
    /// inherit their parent's, so filtering parents covers them.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return [];

        bool hasPath = !string.IsNullOrWhiteSpace(query.PathPrefix);
        bool hasAttr = query.AttributeFilter is { Count: > 0 };

        // Candidates set means the caller (SearchPipeline) already materialized this slice for
        // the contributors — skip the second ListScoped. It is a hint about COST, never about
        // the ANSWER: re-apply this query's own scope and excludeTopics filter so a caller who
        // supplies a wider or differently-scoped list gets exactly what it would have got with
        // no Candidates at all. (A list narrower than the query's scopes is not corrected here —
        // it still under-returns.) One HashSet build plus a Contains per candidate — cheap, and
        // there is no re-listing to compare it against in this branch.
        IReadOnlyList<ScopedArtifact> allCandidates =
            query.Candidates is { } supplied
                ? FilterToQueryScopes(supplied, query.Scopes, query.ExcludeTopics)
                : ListScoped(query.Scopes, query.ExcludeTopics);

        IReadOnlyList<ScopedArtifact> entryCandidates = hasPath || hasAttr
            ? allCandidates
                .Where(c => SearchResultFilter.CandidateMatches(this, c, query.PathPrefix, query.AttributeFilter))
                .ToList()
            : allCandidates;

        // Topics carry no attributes: an active attribute filter drops them all. Otherwise filter
        // topics by their own scope label, derived from the UNFILTERED candidate set (a topic can
        // match even when its entries are filtered out by a longer path prefix, and vice versa).
        List<TopicInfo> topics;
        if (hasAttr)
            topics = [];
        else if (hasPath)
            topics = BuildTopicInfosFromCandidates(allCandidates)
                .Where(t => SearchResultFilter.TopicMatches(t, query.PathPrefix))
                .ToList();
        else
            topics = BuildTopicInfosFromCandidates(allCandidates);

        var searcher = new WeightedAttributeScorer(RankerOptions);
        return searcher.SearchAll(query.Query, entryCandidates, topics, query.Limit,
            query.SupplementalScores, query.EntityGraphScores);
    }

    /// <summary>
    /// Constrains a caller-supplied candidate list to exactly the scopes this query resolves to, so
    /// a wider or differently-scoped <see cref="SearchQuery.Candidates"/> cannot change a search's
    /// answer. A list narrower than the query's scopes is not corrected here — it still under-returns.
    /// Mirrors <see cref="ListScoped(string?, string?)"/>'s scope resolution, including its rule
    /// for when ephemeral memories are in play.
    /// </summary>
    private List<ScopedArtifact> FilterToQueryScopes(
        IReadOnlyList<ScopedArtifact> candidates, string? scopes, string? excludeTopics)
    {
        var allowed = new HashSet<string>(
            ResolveReadScopes(scopes, excludeTopics), StringComparer.OrdinalIgnoreCase);

        bool includeEphemeral = string.IsNullOrWhiteSpace(scopes)
            || scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Any(s => s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
        if (includeEphemeral)
            allowed.Add("ephemeral");

        var result = new List<ScopedArtifact>(candidates.Count);
        foreach (var candidate in candidates)
            if (allowed.Contains(candidate.Scope))
                result.Add(candidate);
        return result;
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
                ContentPreview: entry.ContentPreview,
                ChunkEntries: entry.ChunkEntries,
                Attributes: entry.Attributes);
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

        // Resolve the source entry ONCE, case-insensitively, before the artifact path is built
        // from its name. Both branches below used to run this lookup themselves with an ordinal
        // `e.Name == srcSubject`, so a source stored as "SrcMixed" and copied as "srcmixed" found
        // its file (NTFS folds case) and not its entry — and the copy silently fell through to the
        // synthesized-defaults branch, carrying the bytes and dropping the attributes, keywords and
        // curated preview. Adopting the entry's own casing here fixes the path as well as the
        // match: FindArtifactPath below is a plain Path.Combine over SanitizeName, and would
        // otherwise still be relying on the filesystem to fold case on this code's behalf.
        ArtifactEntry? sourceEntry = LoadIndex(srcScope).FirstOrDefault(
            e => e.Name.Equals(srcSubject, StringComparison.OrdinalIgnoreCase));
        if (sourceEntry is not null)
            srcSubject = sourceEntry.Name;

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

            var ephEntry = new EphemeralEntry(
                Name: dstKey,
                Artifact: artifact,
                OriginalBytes: sourceEntry?.OriginalBytes ?? 0,
                ChunkCount: sourceEntry?.ChunkCount ?? Nmp2ChunkedEncoder.GetChunkCount(artifact),
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: sourceEntry?.ContentPreview ?? $"Loaded from {sourceName}",
                ChunkEntries: sourceEntry?.ChunkEntries,
                Attributes: sourceEntry?.Attributes);
            RememberEphemeral(dstKey, ephEntry);
            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
        }

        // Persistent → Persistent
        var (persistDstScope, persistDstSubject) = ParseQualifiedName(dstKey);

        // Resolve the DESTINATION through the index and adopt its casing, exactly as the source side
        // does above — and for the same reason, which is not only the comparison. `persistDstSubject`
        // decides three things: whether the existence check below sees a collision, where the bytes
        // land, and what the new entry is called. This check used to ask the filesystem
        // (`File.Exists(ArtifactPath(...))`), which reads case-insensitively on an ordinary NTFS
        // volume and case-sensitively elsewhere; on a case-sensitive filesystem, copying onto
        // "Archive" while "archive" existed reported no collision and wrote a SECOND memory, and
        // with overwrite=true wrote a twin instead of replacing. Fixing only the check would have
        // left the twin. The memory that already exists keeps its own casing, as every other
        // resolved name in this store now does.
        //
        // This settles the last open question of the case-sensitivity class: "archive" and "Archive"
        // are ONE memory. The alternative — case-sensitive names everywhere — is a coherent design,
        // but it is a storage-format decision (two names differing only in case need two files, which
        // an ordinary NTFS volume cannot hold), not a change to this line.
        ArtifactEntry? destinationExisting = LoadIndex(persistDstScope).FirstOrDefault(
            e => e.Name.Equals(persistDstSubject, StringComparison.OrdinalIgnoreCase));
        if (destinationExisting is not null)
            persistDstSubject = destinationExisting.Name;

        string destPathP = ArtifactPath(persistDstSubject, persistDstScope);
        if (File.Exists(destPathP) && !overwrite)
        {
            message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPathP)!);
        AtomicFileCopy(sourcePath, destPathP, overwrite);

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
                ContentPreview: $"Copied from {sourceName}");
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

    // ── Topic discovery ──────────────────────────────────────────────────────

    public string[] DiscoverTopics()
    {
        // Cache hit — only re-scans when explicitly invalidated by Upsert / SaveIndex.
        if (_cachedTopics is not null)
            return _cachedTopics;

        // Gate first: on the very first store access this migrates any legacy content into
        // memories/ before the scan below runs, so a first-ever call with real legacy content
        // sees it exactly once, at its post-migration v3 location.
        string memoriesRoot = MemoriesRoot;

        var results = new List<string>();
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
            results.Add($"local:{relativePath}");

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
            if (!scope.StartsWith("local:", StringComparison.Ordinal))
                continue;

            string topicName = scope["local:".Length..];
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

        // ToList() because this method mutates the list below and hands it to SaveIndex, which
        // takes ownership of the instance it is given (it becomes the cache). Mutating the
        // cache's own backing list here would corrupt every other reader's live view.
        var existingEntries = LoadIndex(topicScope).ToList();

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
