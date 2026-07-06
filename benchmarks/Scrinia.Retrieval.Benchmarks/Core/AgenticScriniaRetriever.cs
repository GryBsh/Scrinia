using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Agent;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Whole-run summary counters for the agentic-ingest arm (Task 6 reads this after
/// the benchmark completes). <see cref="Episodes"/> is the total number of session
/// episodes attempted; <see cref="AgenticEpisodes"/> counts those resolved by a
/// (fresh or cache-replayed) successful LLM episode; <see cref="FailedEpisodes"/>
/// counts those that failed twice and stored NOTHING for the session — there is no
/// mechanical fallback, so a failed episode honestly degrades the arm's recall rather
/// than borrowing non-agentic results. <see cref="MemoriesWritten"/> counts
/// agent-authored memory entries; <see cref="MemoriesWithoutRefs"/> counts those with
/// no usable <c>ref:</c> citation (unscoreable); <see cref="UnresolvableRefs"/> counts
/// individual <c>ref:</c> ids that didn't match any buffered doc id.
/// </summary>
public sealed record AgenticIngestStats(
    int Episodes,
    int AgenticEpisodes,
    int FailedEpisodes,
    int MemoriesWritten,
    int MemoriesWithoutRefs,
    int UnresolvableRefs,
    int ToolCallErrors);

/// <summary>
/// <see cref="IRetriever"/> for the agentic-ingest benchmark arm. Unlike
/// <see cref="ScriniaRetriever"/> (which writes one mechanical entry per haystack
/// doc), this retriever BUFFERS docs at ingest time, grouped into ordered sessions,
/// and defers all store writes to a lazy one-shot pass on the first
/// <see cref="RetrieveAsync"/>: one <see cref="AgenticIngestHarness"/> episode per
/// session runs a real LLM tool-calling loop (memory_remember/memory_search)
/// against a per-question <see cref="FileMemoryStore"/>, exactly like a live agent
/// deciding what's worth remembering.
///
/// <para>Queries are then answered mechanically via <see cref="FileMemoryStore.SearchAll"/>
/// — no agent involvement at query time. Each returned memory is mapped back to the
/// haystack doc IDs it cited (<c>ref:</c> lines, extracted into <c>ArtifactEntry.Fields["ref"]</c>
/// by <see cref="Scrinia.Core.Search.FieldExtractor"/> at write time) so the existing
/// R@k scorer — which compares doc IDs — is unchanged.</para>
///
/// <para>Episode results are cached by <see cref="EpisodeCache"/>, keyed on
/// (model, prompt version, session transcript), so reruns of the same haystack
/// replay the recorded tool calls instead of re-invoking the LLM. A failed episode
/// is retried ONCE (to absorb a transient transport blip); a second failure stores
/// NOTHING for that session and is counted as a failed episode. There is deliberately
/// no mechanical-ingestion fallback — substituting verbatim doc ingest would prop the
/// agentic arm's recall up with non-agentic work and measure the wrong thing. Failed
/// episodes are never cached.</para>
/// </summary>
public sealed class AgenticScriniaRetriever : IRetriever
{
    private readonly string _rootDir;
    private readonly AgenticIngestHarness _harness;
    private readonly EpisodeCache _cache;
    private readonly string _modelSlug;
    private readonly RankerOptions _rankerOptions;

    // Buffering — under _ingestLock. Sessions preserve first-seen order, both within
    // and across sessions, so episodes run in the order docs arrived.
    private readonly object _ingestLock = new();
    private readonly List<string> _sessionOrder = [];
    private readonly Dictionary<string, List<HaystackDoc>> _sessionDocs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HaystackDoc> _docsById = new(StringComparer.Ordinal);

    // Per-question state, rebuilt every ResetAsync.
    private FileMemoryStore _store;
    private int _resetCount;
    private bool _ingested;
    private readonly SemaphoreSlim _episodeGate = new(1, 1);
    // entryName -> ordered doc ids the memory resolves to (empty = no usable citation).
    private Dictionary<string, IReadOnlyList<string>> _provenance = new(StringComparer.Ordinal);

    // Cumulative run stats — NOT reset by ResetAsync; this is a whole-run summary
    // (Task 6 reads Stats after the full benchmark completes).
    private int _episodes;
    private int _agenticEpisodes;
    private int _failedEpisodes;
    private int _memoriesWritten;
    private int _memoriesWithoutRefs;
    private int _unresolvableRefs;
    private int _toolCallErrors;

    public AgenticIngestStats Stats => new(
        Episodes: _episodes,
        AgenticEpisodes: _agenticEpisodes,
        FailedEpisodes: _failedEpisodes,
        MemoriesWritten: _memoriesWritten,
        MemoriesWithoutRefs: _memoriesWithoutRefs,
        UnresolvableRefs: _unresolvableRefs,
        ToolCallErrors: _toolCallErrors);

    public AgenticScriniaRetriever(
        string rootDir,
        AgenticIngestHarness harness,
        EpisodeCache cache,
        string modelSlug,
        RankerOptions? rankerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSlug);
        _rootDir = rootDir;
        _harness = harness;
        _cache = cache;
        _modelSlug = modelSlug;
        _rankerOptions = rankerOptions ?? RankerOptions.Default;
        _store = CreateStore();
    }

    // ── Ingest (buffer only — this arm's real ingestion is the episode) ─────────

    public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) =>
        IngestAsync(doc, anchors: null, keywords: null, ct);

    /// <inheritdoc/>
    public Task IngestAsync(HaystackDoc doc, IReadOnlyList<string>? anchors, CancellationToken ct = default) =>
        IngestAsync(doc, anchors, keywords: null, ct);

    /// <summary>Anchors and keywords are IGNORED in this arm — the agent decides what to remember.</summary>
    public Task IngestAsync(
        HaystackDoc doc, IReadOnlyList<string>? anchors, IReadOnlyList<string>? keywords, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ct.ThrowIfCancellationRequested();

        lock (_ingestLock)
        {
            _docsById[doc.Id] = doc;
            string sessionKey = doc.SessionId ?? doc.Id;
            if (!_sessionDocs.TryGetValue(sessionKey, out var list))
            {
                list = [];
                _sessionDocs[sessionKey] = list;
                _sessionOrder.Add(sessionKey);
            }
            list.Add(doc);
        }
        return Task.CompletedTask;
    }

    // ── Retrieve ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        await EnsureEpisodesRunAsync(ct);

        // Expansion + dedup collapses memories to docs, so a wider first-stage
        // candidate set is needed to still surface topK unique docs.
        int searchLimit = Math.Max(topK * 10, topK);
        IReadOnlyList<SearchResult> results = _store.SearchAll(query, scopes: null, limit: searchLimit);

        var hits = new List<RetrievalHit>(topK);
        var seenDocIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (hits.Count >= topK) break;

            string entryName;
            double score;
            switch (r)
            {
                case EntryResult er:
                    entryName = er.Item.Entry.Name;
                    score = er.Score;
                    break;
                case ChunkEntryResult cr:
                    entryName = cr.ParentItem.Entry.Name;
                    score = cr.Score;
                    break;
                default:
                    continue;
            }

            if (!_provenance.TryGetValue(entryName, out var docIds) || docIds.Count == 0)
                continue;

            foreach (string docId in docIds)
            {
                if (hits.Count >= topK) break;
                if (!seenDocIds.Add(docId)) continue;
                string text = _docsById.TryGetValue(docId, out var doc) ? doc.Text : string.Empty;
                hits.Add(new RetrievalHit(Id: docId, Text: text, Score: score));
            }
        }
        return hits;
    }

    // ── Lazy episode run ─────────────────────────────────────────────────────

    private async Task EnsureEpisodesRunAsync(CancellationToken ct)
    {
        if (_ingested) return;
        await _episodeGate.WaitAsync(ct);
        try
        {
            if (_ingested) return;

            List<string> sessionOrder;
            Dictionary<string, List<HaystackDoc>> sessionDocs;
            lock (_ingestLock)
            {
                sessionOrder = [.. _sessionOrder];
                sessionDocs = new Dictionary<string, List<HaystackDoc>>(_sessionDocs, StringComparer.Ordinal);
            }

            MemoryStoreContext.Current = _store;
            try
            {
                foreach (string sessionKey in sessionOrder)
                {
                    await RunSessionEpisodeAsync(sessionDocs[sessionKey], ct);
                    _episodes++;
                }
            }
            finally
            {
                MemoryStoreContext.Current = null;
            }

            BuildProvenanceMap();
            _ingested = true;
        }
        finally
        {
            _episodeGate.Release();
        }
    }

    private async Task RunSessionEpisodeAsync(IReadOnlyList<HaystackDoc> sessionDocs, CancellationToken ct)
    {
        string transcript = AgenticIngestPrompts.SessionUserMessage(sessionDocs);
        string key = EpisodeCache.Key(_modelSlug, AgenticIngestPrompts.PromptVersion, transcript);

        if (_cache.TryGet(key, out var cachedCalls))
        {
            await AgenticIngestHarness.ReplayAsync(cachedCalls, ct);
            _agenticEpisodes++;
            return;
        }

        var preEpisode = new HashSet<string>(
            _store.ListScoped().Select(a => a.Entry.Name), StringComparer.Ordinal);

        EpisodeResult result = await _harness.RunEpisodeAsync(sessionDocs, ct);
        _toolCallErrors += result.ToolCallErrors;
        if (!result.Succeeded)
        {
            // One retry to absorb a transient transport blip. Roll back the failed
            // attempt's partial writes first so the retry starts from clean pre-episode
            // state (otherwise residue would survive alongside the retry's writes and
            // diverge from cache-replay, which only replays the successful attempt).
            RollbackTo(preEpisode);
            result = await _harness.RunEpisodeAsync(sessionDocs, ct);
            _toolCallErrors += result.ToolCallErrors;
        }

        if (result.Succeeded)
        {
            // NEVER cache a failed episode — only a Succeeded run.
            _cache.Put(key, result.ToolCalls);
            _agenticEpisodes++;
            return;
        }

        // Both attempts failed. Discard partial writes and store NOTHING for this
        // session — there is deliberately no mechanical-ingestion fallback. Substituting
        // verbatim doc ingest would inflate the agentic arm's recall with non-agentic
        // work; a failed episode must honestly degrade recall instead. Counted + logged
        // loudly so the failure is visible, not silently absorbed.
        RollbackTo(preEpisode);
        Console.Error.WriteLine(
            $"[scrinia-bench:agentic-ingest] session FAILED (episode failed twice, " +
            $"session of {sessionDocs.Count} doc(s)); nothing stored for this session.");
        _failedEpisodes++;
    }

    /// Removes every entry added to the store since <paramref name="preEpisode"/> was
    /// captured — used to discard a failed episode attempt's partial writes so retry and
    /// cache-replay see identical state. Cross-session memories committed by earlier
    /// sessions are in the snapshot and are preserved.
    private void RollbackTo(HashSet<string> preEpisode)
    {
        // Snapshot the current list first — Remove mutates the store, don't enumerate live.
        foreach (var a in _store.ListScoped().ToList())
            if (!preEpisode.Contains(a.Entry.Name))
                _store.Remove(a.Entry.Name, a.Scope);
    }

    /// <summary>
    /// Builds entryName → ordered doc-ids provenance from every agent-authored memory
    /// currently in the store, resolving each via <c>Fields["ref"]</c> (JSON round-trip
    /// drops the dictionary's original comparer, so the "ref" key is matched with an
    /// explicit OrdinalIgnoreCase comparison rather than a direct dictionary lookup).
    /// </summary>
    private void BuildProvenanceMap()
    {
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var sa in _store.ListScoped())
        {
            ArtifactEntry entry = sa.Entry;

            string[]? refs = null;
            if (entry.Fields is not null)
            {
                foreach (var kv in entry.Fields)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(kv.Key, "ref"))
                    {
                        refs = kv.Value;
                        break;
                    }
                }
            }

            _memoriesWritten++;

            if (refs is null || refs.Length == 0)
            {
                _memoriesWithoutRefs++;
                provenance[entry.Name] = [];
                continue;
            }

            var resolved = new List<string>(refs.Length);
            foreach (string r in refs)
            {
                if (_docsById.ContainsKey(r))
                    resolved.Add(r);
                else
                    _unresolvableRefs++;
            }
            provenance[entry.Name] = resolved;
        }

        _provenance = provenance;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public Task ResetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Clean up the previous question's dir, matching ScriniaRetriever's q{n} lifecycle.
        string previous = Path.Combine(_rootDir, $"q{_resetCount:D6}");
        try
        {
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }

        _resetCount++;
        _store = CreateStore();

        lock (_ingestLock)
        {
            _sessionOrder.Clear();
            _sessionDocs.Clear();
            _docsById.Clear();
        }
        _ingested = false;
        _provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        return ValueTask.CompletedTask;
    }

    private FileMemoryStore CreateStore()
    {
        string per = Path.Combine(_rootDir, $"q{_resetCount:D6}");
        Directory.CreateDirectory(per);
        return new FileMemoryStore(per) { RankerOptions = _rankerOptions };
    }
}
