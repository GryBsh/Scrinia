using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
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
/// session runs a real LLM tool-calling loop (remember/search)
/// against a per-question <see cref="FileMemoryStore"/>, exactly like a live agent
/// deciding what's worth remembering.
///
/// <para>Queries are then answered mechanically via <see cref="FileMemoryStore.SearchAll"/>
/// (BM25 + fields) when no embedder is configured. When an embedder IS configured (CLI
/// <c>--mode bm25_embeddings</c>), queries additionally blend in the production semantic +
/// path-segment score contributor (<see cref="HybridReranker"/> + <see cref="PathSegmentScoreContributor"/>,
/// via a post-episode <see cref="EmbeddingReindexer"/> pass into a shared <see cref="VectorStore"/>) —
/// the same hybrid retrieval path production uses. Each returned memory is mapped back to the
/// haystack doc IDs it cited by parsing <c>D{n}:{i}</c>-style citations out of the
/// memory's full decoded content via <see cref="DialogRefParser"/> and resolving each
/// against the buffered doc ids, so the existing R@k scorer — which compares doc IDs —
/// is unchanged.</para>
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
public sealed class AgenticScriniaRetriever : IRetriever, ISurfacedContentProvider
{
    private readonly string _rootDir;
    private readonly AgenticIngestHarness _harness;
    private readonly EpisodeCache _cache;
    private readonly string _modelSlug;
    private readonly RankerOptions _rankerOptions;
    // Owned by the caller (factory) and shared across ResetAsync — never disposed here.
    // Kept only when usable, mirroring ScriniaRetriever's guard.
    private readonly IEmbeddingProvider? _embedder;
    private readonly EmbeddingOptions _embeddingOptions;
    // Entity-graph recall probe (off unless --entity-graph). Built per query by the SAME
    // production EntityGraphScoreContributor the server uses, from each candidate memory's
    // attributes — the agent writes those natively via remember/append, so this is effectively
    // attributes-only (the agent's freeform keywords carry no type:value prefix and never form
    // graph nodes). Passed as the dedicated SearchAll entity-graph source. Null when disabled.
    private readonly EntityGraphScoreContributor? _entityGraphContributor;

    // Buffering — under _ingestLock. Sessions preserve first-seen order, both within
    // and across sessions, so episodes run in the order docs arrived.
    private readonly object _ingestLock = new();
    private readonly List<string> _sessionOrder = [];
    private readonly Dictionary<string, List<HaystackDoc>> _sessionDocs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HaystackDoc> _docsById = new(StringComparer.Ordinal);

    // Per-question state, rebuilt every ResetAsync.
    private FileMemoryStore _store;
    private int _resetCount;
    // Shared VectorStore instance for the current question — the SAME instance the
    // HybridReranker inside _searchContributor reads from, so a post-episode
    // EmbeddingReindexer.ReindexAsync pass (into this instance) is immediately visible
    // to the query path. Null when no embedder was supplied (BM25-only, unchanged).
    private VectorStore? _vectorStore;
    // Task 2's query path reads this to add the semantic + path-segment score
    // contributions. Null on the no-embedder path.
    private ISearchScoreContributor? _searchContributor;
    private bool _ingested;
    private readonly SemaphoreSlim _episodeGate = new(1, 1);
    // entryName -> ordered doc ids the memory resolves to (empty = no usable citation).
    private Dictionary<string, IReadOnlyList<string>> _provenance = new(StringComparer.Ordinal);
    // Memory names whose full-content resolve already failed and was logged — dedupes
    // the warning so a systemic resolve failure logs once per memory, not once per hit.
    private readonly HashSet<string> _loggedResolveFailures = new(StringComparer.Ordinal);
    // entryName -> full decoded plaintext, resolved ONCE after episodes (the store is
    // immutable during the query phase). Read by RetrieveAsync, BuildProvenanceMap, and
    // GetSurfacedContentsAsync so full content is resolved at most once per Reset.
    private Dictionary<string, string> _fullContentByEntryName = new(StringComparer.Ordinal);

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
        RankerOptions? rankerOptions = null,
        IEmbeddingProvider? embedder = null,
        EmbeddingOptions? embeddingOptions = null,
        bool entityGraphEnabled = false,
        double entityGraphAlpha = EntityGraphScoreContributor.DefaultAlpha,
        int entityGraphIterations = EntityGraphScoreContributor.DefaultIterations,
        double entityGraphWeight = EntityGraphScoreContributor.DefaultWeight)
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
        _embedder = embedder is { IsAvailable: true } ? embedder : null;
        _embeddingOptions = embeddingOptions ?? new EmbeddingOptions();
        _entityGraphContributor = entityGraphEnabled
            ? new EntityGraphScoreContributor(entityGraphWeight, entityGraphAlpha, entityGraphIterations)
            : null;
        _store = CreateStore();
        RebuildEmbeddingPipeline(PerQuestionDir());
    }

    /// <summary>Test-only hook: number of vectors currently held by the shared
    /// per-question <see cref="VectorStore"/> (0 when no embedder is configured).</summary>
    internal int EmbeddedVectorCount => _vectorStore?.Count() ?? 0;

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
        IReadOnlyList<SearchResult> results;
        if (_searchContributor is not null)
        {
            var candidates = _store.ListScoped(scopes: null);
            var supplemental = await _searchContributor.ComputeScoresAsync(query, candidates, _store, ct);
            var entityScores = await ComputeEntityScoresAsync(query, candidates, ct);
            results = _store.SearchAll(query, scopes: null, searchLimit, supplemental, entityGraphScores: entityScores);
        }
        else if (_entityGraphContributor is not null)
        {
            var candidates = _store.ListScoped(scopes: null);
            var entityScores = await ComputeEntityScoresAsync(query, candidates, ct);
            results = _store.SearchAll(query, scopes: null, searchLimit, supplementalScores: null, entityGraphScores: entityScores);
        }
        else
        {
            results = _store.SearchAll(query, scopes: null, limit: searchLimit);
        }

        var hits = new List<RetrievalHit>(topK);
        var seenDocIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (hits.Count >= topK) break;

            string entryName;
            string previewContent;
            double score;
            switch (r)
            {
                case EntryResult er:
                    entryName = er.Item.Entry.Name;
                    previewContent = er.Item.Entry.ContentPreview ?? string.Empty;
                    score = er.Score;
                    break;
                case ChunkEntryResult cr:
                    entryName = cr.ParentItem.Entry.Name;
                    previewContent = cr.ParentItem.Entry.ContentPreview ?? string.Empty;
                    score = cr.Score;
                    break;
                default:
                    continue;
            }

            string entryContent = _fullContentByEntryName.TryGetValue(entryName, out var full)
                ? full
                : previewContent;

            if (!_provenance.TryGetValue(entryName, out var docIds) || docIds.Count == 0)
                continue;

            foreach (string docId in docIds)
            {
                if (hits.Count >= topK) break;
                if (!seenDocIds.Add(docId)) continue;
                hits.Add(new RetrievalHit(Id: docId, Text: entryContent, Score: score));
            }
        }
        return hits;
    }

    /// <summary>
    /// Top-<paramref name="k"/> matching memories' full content, deduped by entry, with
    /// NO provenance gate — this is "what the store surfaces for the query" for the judge.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSurfacedContentsAsync(
        string query, int k, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();
        await EnsureEpisodesRunAsync(ct);

        // Over-fetch so chunk-results collapsing to the same parent memory still yield k distinct memories.
        int searchLimit = Math.Max(k * 4, k);
        IReadOnlyList<SearchResult> results;
        if (_searchContributor is not null)
        {
            var candidates = _store.ListScoped(scopes: null);
            var supplemental = await _searchContributor.ComputeScoresAsync(query, candidates, _store, ct);
            var entityScores = await ComputeEntityScoresAsync(query, candidates, ct);
            results = _store.SearchAll(query, scopes: null, searchLimit, supplemental, entityGraphScores: entityScores);
        }
        else if (_entityGraphContributor is not null)
        {
            var candidates = _store.ListScoped(scopes: null);
            var entityScores = await ComputeEntityScoresAsync(query, candidates, ct);
            results = _store.SearchAll(query, scopes: null, searchLimit, supplementalScores: null, entityGraphScores: entityScores);
        }
        else
        {
            results = _store.SearchAll(query, scopes: null, limit: searchLimit);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contents = new List<string>(k);
        foreach (var r in results)
        {
            if (contents.Count >= k) break;
            string? name = r switch
            {
                EntryResult er => er.Item.Entry.Name,
                ChunkEntryResult cr => cr.ParentItem.Entry.Name,
                _ => null,
            };
            if (name is null || !seen.Add(name)) continue;
            contents.Add(_fullContentByEntryName.TryGetValue(name, out var full) ? full : string.Empty);
        }
        return contents;
    }

    /// <summary>
    /// Entity-graph PPR scores for the query over the candidate memories, via the production
    /// <see cref="EntityGraphScoreContributor"/>. Null when the probe is disabled or no candidate
    /// carries entity attributes (the contributor returns null → the dedicated SearchAll channel
    /// stays inert). Attributes-only in practice: the agent tags entities as memory attributes,
    /// and its freeform keywords carry no type:value prefix, so only attributes form graph nodes.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, double>?> ComputeEntityScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates, CancellationToken ct) =>
        _entityGraphContributor is null
            ? null
            : await _entityGraphContributor.ComputeScoresAsync(query, candidates, _store, ct);

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

            // Reindex into the SAME VectorStore instance the query path's HybridReranker
            // reads from (Task 2) — NOT EmbeddingReindexer.ForceReindexAsync, which would
            // construct its own VectorStore the reranker would never see. Runs after every
            // episode has populated _store and before the full-content cache / provenance
            // build, so the reranker sees fresh vectors on the very first query.
            if (_embedder is not null && _vectorStore is not null)
                await EmbeddingReindexer.ReindexAsync(_store, _embedder, _vectorStore,
                    NullLogger.Instance, progress: null, ct, _embeddingOptions);

            await BuildFullContentCacheAsync(ct);
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

        // Retry once when the attempt is unusable: a transport failure (Succeeded==false),
        // OR an episode where the model emitted no tool calls AT ALL — not even a malformed
        // one — i.e. it replied with pure text and stored nothing. Both leave the session
        // empty and both deserve a fresh re-roll (RunEpisodeAsync starts a new conversation
        // each call). Roll back any partial writes first so the retry starts from clean
        // pre-episode state (otherwise residue would survive alongside the retry's writes
        // and diverge from cache-replay, which only replays the kept attempt).
        if (!result.Succeeded || MadeNoToolCalls(result))
        {
            RollbackTo(preEpisode);
            result = await _harness.RunEpisodeAsync(sessionDocs, ct);
            _toolCallErrors += result.ToolCallErrors;
        }

        if (result.Succeeded && !MadeNoToolCalls(result))
        {
            // Cache ONLY a run that succeeded AND actually did work — never a failed run,
            // never an empty one (caching empty would freeze this session at "stores
            // nothing" on every future cache-hit run).
            _cache.Put(key, result.ToolCalls);
            _agenticEpisodes++;
            return;
        }

        // Both attempts were unusable (transport failure, or the model never made a tool
        // call). Discard partial writes and store NOTHING for this session — there is
        // deliberately no mechanical-ingestion fallback. Substituting verbatim doc ingest
        // would inflate the agentic arm's recall with non-agentic work; an unusable episode
        // must honestly degrade recall instead. Counted + logged loudly so it is visible,
        // not silently absorbed.
        RollbackTo(preEpisode);
        string reason = result.Succeeded
            ? "made no tool calls on either attempt"
            : "episode failed twice";
        Console.Error.WriteLine(
            $"[scrinia-bench:agentic-ingest] session FAILED ({reason}, " +
            $"session of {sessionDocs.Count} doc(s)); nothing stored for this session.");
        _failedEpisodes++;
    }

    /// <summary>
    /// True when the episode emitted no tool calls whatsoever — no successful call
    /// (<see cref="EpisodeResult.ToolCalls"/> empty) and no malformed one
    /// (<see cref="EpisodeResult.ToolCallErrors"/> zero). This is the "model just replied
    /// with text and stored nothing" case, distinct from a run that tried and erred. Such
    /// an episode is retried once; if it recurs, the session is counted as failed rather
    /// than cached empty.
    /// </summary>
    private static bool MadeNoToolCalls(EpisodeResult result) =>
        result.ToolCalls.Count == 0 && result.ToolCallErrors == 0;

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
    /// Resolves every stored memory's full plaintext once, via the entry's own Uri
    /// (the authoritative file://…nmp2 path) + NMP/2 decode. On failure, falls back to
    /// the entry's ContentPreview and logs once per entry.
    /// </summary>
    private async Task BuildFullContentCacheAsync(CancellationToken ct)
    {
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sa in _store.ListScoped())
        {
            ArtifactEntry entry = sa.Entry;
            string content;
            try
            {
                string artifact = await _store.ResolveArtifactAsync(entry.Uri, ct);
                content = System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact));
            }
            catch (Exception ex)
            {
                if (_loggedResolveFailures.Add(entry.Name))
                    Console.Error.WriteLine(
                        $"[scrinia-bench:agentic-ingest] full-content resolve failed for {entry.Name}: {ex.Message}; using preview");
                content = entry.ContentPreview ?? string.Empty;
            }
            cache[entry.Name] = content;
        }
        _fullContentByEntryName = cache;
    }

    /// <summary>
    /// Builds entryName → ordered doc-ids provenance from every agent-authored memory
    /// currently in the store, parsing dialog ids directly out of the memory's full
    /// decoded content (<see cref="_fullContentByEntryName"/>) via
    /// <see cref="DialogRefParser.ExtractDialogIds"/> and resolving each against
    /// <see cref="_docsById"/>. This does not depend on production's
    /// <c>Fields["ref"]</c> field extraction.
    /// </summary>
    private void BuildProvenanceMap()
    {
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var sa in _store.ListScoped())
        {
            ArtifactEntry entry = sa.Entry;
            _memoriesWritten++;

            string content = _fullContentByEntryName.TryGetValue(entry.Name, out var full)
                ? full
                : string.Empty;
            var citedIds = DialogRefParser.ExtractDialogIds(content);

            if (citedIds.Count == 0)
            {
                _memoriesWithoutRefs++;
                provenance[entry.Name] = [];
                continue;
            }

            var resolved = new List<string>(citedIds.Count);
            foreach (string id in citedIds)
            {
                if (_docsById.ContainsKey(id))
                    resolved.Add(id);
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
        RebuildEmbeddingPipeline(PerQuestionDir());

        lock (_ingestLock)
        {
            _sessionOrder.Clear();
            _sessionDocs.Clear();
            _docsById.Clear();
        }
        _ingested = false;
        _provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        _fullContentByEntryName = new Dictionary<string, string>(StringComparer.Ordinal);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Owned by us (unlike _embedder, which the caller owns and shares across resets).
        // Idempotent: a second call sees _vectorStore already null and no-ops.
        _vectorStore?.Dispose();
        _vectorStore = null;
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        return ValueTask.CompletedTask;
    }

    private string PerQuestionDir() => Path.Combine(_rootDir, $"q{_resetCount:D6}");

    private FileMemoryStore CreateStore()
    {
        string per = PerQuestionDir();
        Directory.CreateDirectory(per);
        return new FileMemoryStore(per) { RankerOptions = _rankerOptions };
    }

    /// <summary>
    /// (Re)builds the per-question embedding pipeline sharing ONE <see cref="VectorStore"/>
    /// instance between the post-episode reindex (<see cref="EmbeddingReindexer.ReindexAsync"/>)
    /// and the query-time <see cref="HybridReranker"/> — the coherence this arm depends on.
    /// No-op (both fields left null) when no usable embedder was supplied, so the BM25-only
    /// path is unchanged.
    /// </summary>
    private void RebuildEmbeddingPipeline(string perQuestionDir)
    {
        _vectorStore?.Dispose();
        _vectorStore = null;
        _searchContributor = null;

        if (_embedder is null) return;

        string embeddingsDir = Path.Combine(perQuestionDir, "embeddings");
        string signature = ChunkedSignature.Compose(
            _embedder.Signature, _embeddingOptions.ChunkSize, _embeddingOptions.ChunkOverlap);
        _vectorStore = new VectorStore(embeddingsDir, signature);
        var reranker = new HybridReranker(_embedder, _vectorStore, _embeddingOptions.SemanticWeight);
        _searchContributor = new CompositeSearchScoreContributor(reranker, new PathSegmentScoreContributor());
    }
}
