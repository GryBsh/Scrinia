using System.Collections.Concurrent;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Llm;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// <see cref="IRetriever"/> backed by a per-question <see cref="FileMemoryStore"/>.
///
/// <para>Pure BM25 mode (constructor without an <see cref="IEmbeddingProvider"/>): ingest
/// populates <c>TermFrequencies</c> via <see cref="TextAnalysis.AnalyzeText"/> and stores
/// the original doc text in <see cref="ArtifactEntry.ContentPreview"/> so the scorer can
/// do substring matching without re-reading the artifact file. No artifact file is
/// written — the metadata sidecar carries everything the BM25 path needs.</para>
///
/// <para>BM25+embeddings mode (constructor with an <see cref="IEmbeddingProvider"/>):
/// additionally embeds every ingested doc, caches the vector in memory, and at query
/// time embeds the query, computes cosine similarity against every cached vector, and
/// passes the result as supplemental scores to
/// <see cref="FileMemoryStore.SearchAll(SearchQuery)"/>.
/// Mirrors the production <see cref="HybridReranker"/> pattern (similarity × weight, default
/// weight = 50.0) but with an in-memory vector cache rather than a SQLite-backed
/// <see cref="VectorStore"/>, since each benchmark question's haystack is small and
/// short-lived.</para>
/// </summary>
public sealed class ScriniaRetriever : IRetriever
{
    /// <summary>Chunk-id separator. <c>{parentId}::chunk_{index}</c> at ingest time;
    /// <see cref="StripChunkSuffix"/> reverses at retrieve time. <c>LastIndexOf</c> is
    /// used so parent IDs that themselves contain <c>::chunk_</c> substrings still
    /// dedup correctly.</summary>
    private const string ChunkSeparator = "::chunk_";

    private readonly string _rootDir;
    private readonly RankerOptions _rankerOptions;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly double _semanticWeight;
    private readonly double _diversityLambda;
    private readonly int _diversityInnerLimit;
    private readonly bool _chunked;
    private readonly int _chunkWindowSize;
    private readonly int _chunkOverlap;
    // FileMemoryStore.Upsert is not thread-safe. When the runner calls IngestAsync
    // concurrently (--ingest-concurrency > 1), this lock serializes the index write
    // so two parallel docs don't corrupt the in-memory index. The lock is cheap when
    // uncontended; per-doc the embed time dominates.
    private readonly object _ingestLock = new();
    // Process-lifetime embedding cache, keyed by SHA256(text). NOT wiped on
    // ResetAsync — same doc text appearing across multiple questions is embedded
    // exactly once. The per-question _vectorCache below is still rebuilt fresh per
    // question (it's keyed by sanitized entry name, used by BuildSupplementalScores
    // for the in-question lookup); this layer just feeds it without re-calling the
    // embedder. ~1.5KB per 384-dim vector; bounded by unique haystack docs across
    // the whole bench process (MemBench worst case ~75 MB).
    private readonly ConcurrentDictionary<string, float[]> _embeddingCacheByContent = new();
    // Sibling caches for sentence-level vectors used by the optional reranker.
    // Same shape and rationale as the whole-doc caches: per-question reset of the
    // name-keyed lookup, process-lifetime content-hash cache to amortize embed cost.
    // Empty when _buildSentenceIndex is false, so the reranker-off path pays nothing.
    private readonly ConcurrentDictionary<string, IReadOnlyList<float[]>> _sentenceCacheByContent = new();
    private readonly bool _buildSentenceIndex;
    // Entity-graph recall probe (off by default — adds zero cost when disabled). When on, the
    // query path builds a bipartite memory↔entity graph per query from the candidates' own
    // attributes/keywords and scores Personalized PageRank via the SAME production
    // EntityGraphScoreContributor the server uses, passed as its OWN dedicated SearchAll source
    // (not the supplemental channel). Null when disabled.
    private readonly EntityGraphScoreContributor? _entityGraphContributor;
    private FileMemoryStore _store;
    private int _resetCount;
    private ConcurrentDictionary<string, float[]> _vectorCache;
    private ConcurrentDictionary<string, IReadOnlyList<float[]>> _sentenceVectorCache;

    /// <summary>
    /// Optional second-stage reranker. Set by <see cref="Cli.RetrieverFactory"/> after
    /// construction so the reranker can be wired with a sentence-lookup pointing back
    /// at this retriever's per-question index. Null disables reranking — the retriever
    /// returns first-stage results unchanged.
    /// </summary>
    public ISearchReranker? Reranker { get; set; }

    /// <summary>
    /// Sentence-vector lookup for an entry by its sanitized name. Returns null when
    /// no sentence index was built (reranker disabled, embedder unavailable, or the
    /// doc was too short to split). Reranker captures this as a delegate at
    /// construction time.
    /// </summary>
    public IReadOnlyList<float[]>? GetSentenceVectors(string name) =>
        _sentenceVectorCache.TryGetValue(name, out var vecs) ? vecs : null;

    public ScriniaRetriever(
        string rootDir,
        RankerOptions? rankerOptions = null,
        IEmbeddingProvider? embeddingProvider = null,
        double semanticWeight = 50.0,
        double diversityLambda = 1.0,
        int diversityInnerLimit = 0,
        bool chunked = false,
        int chunkWindowSize = TextChunker.DefaultWindowSize,
        int chunkOverlap = TextChunker.DefaultOverlap,
        bool buildSentenceIndex = false,
        bool entityGraphEnabled = false,
        double entityGraphAlpha = 0.15,
        int entityGraphIterations = 20,
        double entityGraphWeight = 50.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        _rootDir = rootDir;
        _rankerOptions = rankerOptions ?? RankerOptions.Default;
        _embeddingProvider = embeddingProvider is { IsAvailable: true } ? embeddingProvider : null;
        _semanticWeight = semanticWeight;
        // diversityInnerLimit > 0 enables MMR. lambda is clamped to [0, 1] by the reranker;
        // a 1.0 + nonzero inner-limit still applies MMR but degenerates to relevance-sort.
        _diversityLambda = diversityLambda;
        _diversityInnerLimit = diversityInnerLimit;
        _chunked = chunked;
        _chunkWindowSize = chunkWindowSize;
        _chunkOverlap = chunkOverlap;
        // Building the sentence index only makes sense if we have an embedder. Off by
        // default keeps the embed cost path unchanged (~1 embed per doc) when no
        // reranker is configured.
        _buildSentenceIndex = buildSentenceIndex && _embeddingProvider is not null;
        _entityGraphContributor = entityGraphEnabled
            ? new EntityGraphScoreContributor(entityGraphWeight, entityGraphAlpha, entityGraphIterations)
            : null;
        _store = CreateStore();
        _vectorCache = new ConcurrentDictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        _sentenceVectorCache = new ConcurrentDictionary<string, IReadOnlyList<float[]>>(StringComparer.OrdinalIgnoreCase);
    }

    public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) =>
        IngestAsync(doc, agentAttributes: null, agentKeywords: null, ct);

    /// <summary>
    /// Full ingest overload mirroring the production <c>remember</c> surface. Agent
    /// <paramref name="agentAttributes"/> flow into <see cref="ArtifactEntry.Attributes"/>
    /// (so the field scorer + entity graph see the typed facets) and their key/value tokens
    /// seed BM25 at +3; agent <paramref name="agentKeywords"/> are merged into
    /// <see cref="ArtifactEntry.Keywords"/> and their value tokens seed BM25 at +5 —
    /// prod-faithful weights (Store: agent keywords +5, fields/attributes +3, auto +2). Both
    /// null preserves baseline mechanical behavior.
    /// </summary>
    public async Task IngestAsync(
        HaystackDoc doc,
        IReadOnlyDictionary<string, string[]>? agentAttributes,
        IReadOnlyList<string>? agentKeywords,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ct.ThrowIfCancellationRequested();

        if (!_chunked)
        {
            await IngestEntryAsync(doc.Id, doc.Text, doc.Timestamp, agentAttributes, agentKeywords, ct);
            return;
        }

        var windows = TextChunker.SliceWindows(doc.Text, _chunkWindowSize, _chunkOverlap);
        if (windows.Count == 0) return;
        foreach (var (idx, text) in windows)
        {
            string chunkId = $"{doc.Id}{ChunkSeparator}{idx}";
            await IngestEntryAsync(chunkId, text, doc.Timestamp, agentAttributes, agentKeywords, ct);
        }
    }

    /// <summary>Tokenizes <paramref name="text"/> and adds <paramref name="boost"/> to each
    /// token's count in <paramref name="tf"/> — the field/attribute TF-seeding primitive,
    /// mirroring <c>AttributeExtractor.SeedTermFrequencies</c>.</summary>
    private static void SeedTokens(Dictionary<string, int> tf, string text, int boost)
    {
        foreach (string token in TextAnalysis.Tokenize(text))
        {
            tf.TryGetValue(token, out int count);
            tf[token] = count + boost;
        }
    }

    private async Task IngestEntryAsync(
        string id, string text, DateTimeOffset? timestamp,
        IReadOnlyDictionary<string, string[]>? agentAttributes, IReadOnlyList<string>? agentKeywords, CancellationToken ct)
    {
        string sanitized = SanitizeName(id);
        var (keywords, tf) = TextAnalysis.AnalyzeText(text);

        // Materialise agent signals. Attributes = the typed facet map the agent supplied;
        // keywords = the agent's freeform search terms.
        Dictionary<string, string[]>? attributeSource = agentAttributes is { Count: > 0 }
            ? new Dictionary<string, string[]>(agentAttributes, StringComparer.OrdinalIgnoreCase)
            : null;
        string[]? keywordSource = agentKeywords is { Count: > 0 }
            ? (agentKeywords as string[] ?? [.. agentKeywords])
            : null;

        // Seed agent-derived tokens into the TF dict. Prod-faithful weights (see
        // AttributeExtractor.SeedTermFrequencies and MemoryTools remember): attribute key +
        // value tokens +3, agent keyword VALUE tokens +5. IndexTokensForKeyword strips a
        // type prefix and tokenizes the value so a "topic:home renovation" keyword seeds
        // [home, renovation], not a dead monolithic key.
        Dictionary<string, int> mergedTf = tf;
        if (attributeSource is { Count: > 0 } || keywordSource is { Length: > 0 })
        {
            mergedTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in tf)
                mergedTf[k] = v;  // mergedTf starts empty — direct copy of text-derived TF
            if (attributeSource is { Count: > 0 })
            {
                foreach (var (key, values) in attributeSource)
                {
                    SeedTokens(mergedTf, key, boost: 3);
                    foreach (string value in values)
                        SeedTokens(mergedTf, value, boost: 3);
                }
            }
            if (keywordSource is { Length: > 0 })
            {
                foreach (string kw in keywordSource)
                    foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
                    {
                        mergedTf.TryGetValue(token, out int count);
                        mergedTf[token] = count + 5;
                    }
            }
        }

        // Entry keywords = auto-extracted ∪ agent keywords, so the field scorer's
        // exact/contains paths see the agent's search terms too.
        string[] entryKeywords = keywordSource is { Length: > 0 }
            ? keywords.Concat(keywordSource).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : keywords;

        var entry = new ArtifactEntry(
            Name: sanitized,
            Uri: $"bench://{id}",
            OriginalBytes: text.Length,
            ChunkCount: 0,
            CreatedAt: timestamp ?? DateTimeOffset.UtcNow,
            Keywords: entryKeywords,
            TermFrequencies: mergedTf,
            ContentPreview: text,
            Attributes: attributeSource);

        // FileMemoryStore.Upsert isn't thread-safe — serialize it across concurrent doc
        // ingests (parallel mode). The embedding call below is awaited OUTSIDE this lock so
        // multiple embed calls run concurrently; only the index write is serialized here.
        lock (_ingestLock)
        {
            _store.Upsert(entry);
        }

        if (_embeddingProvider is not null)
        {
            // Content-keyed shortcut: same text in a different question (or chunk of
            // the same parent appearing across questions) reuses the prior vector.
            // Persists across ResetAsync, so the embedding cost — model2vec at ~1 ms,
            // vulkan/GGUF at ~50 ms — is paid exactly once per unique text body.
            string contentHash = LlmResponseCache.Hash(text);
            if (!_embeddingCacheByContent.TryGetValue(contentHash, out var vec))
            {
                vec = await _embeddingProvider.EmbedAsync(text, ct);
                if (vec is not null) _embeddingCacheByContent[contentHash] = vec;
            }
            if (vec is not null) _vectorCache[sanitized] = vec;

            // Sentence index for the optional MaxSim reranker. Built ONLY when
            // explicitly opted in via the constructor flag — otherwise we'd pay
            // ~30× the embedding cost per doc for nothing. Sentence vectors are
            // content-hash-cached identically to whole-doc vectors so a 50q
            // benchmark with repeating haystack docs pays the split + embed cost
            // exactly once per unique text.
            if (_buildSentenceIndex)
            {
                if (!_sentenceCacheByContent.TryGetValue(contentHash, out var sentenceVecs))
                {
                    var sentences = SentenceSplitter.Split(text);
                    if (sentences.Count > 0)
                    {
                        var built = new List<float[]>(sentences.Count);
                        foreach (string s in sentences)
                        {
                            var sv = await _embeddingProvider.EmbedAsync(s, ct);
                            if (sv is not null) built.Add(sv);
                        }
                        sentenceVecs = built;
                        _sentenceCacheByContent[contentHash] = sentenceVecs;
                    }
                }
                if (sentenceVecs is { Count: > 0 }) _sentenceVectorCache[sanitized] = sentenceVecs;
            }
        }
    }

    public async Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, double>? supplemental = null;
        if (_embeddingProvider is not null && !_vectorCache.IsEmpty)
        {
            var queryVec = await _embeddingProvider.EmbedAsync(query, ct);
            if (queryVec is not null)
                supplemental = BuildSupplementalScores(queryVec);
        }

        // Entity-graph PPR recall signal as its OWN scoring source — matches production
        // (a dedicated channel, not summed into supplemental). Built per query by the same
        // production EntityGraphScoreContributor from the candidates' attributes/keywords;
        // inert (null) until memories carry entity tags, so baseline vs treatment isolates PPR.
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (_entityGraphContributor is not null)
        {
            var entityCandidates = _store.ListScoped(scopes: null);
            entityScores = await _entityGraphContributor.ComputeScoresAsync(query, entityCandidates, _store, ct);
        }

        // In chunked mode we need a wider candidate set than topK because dedup will
        // collapse multiple chunks of the same parent to one hit. 10x is generous;
        // SearchAll returns whatever the store has if the limit exceeds it.
        int searchLimit = _diversityInnerLimit > 0 ? Math.Max(_diversityInnerLimit, topK) : Math.Max(1, topK);
        if (_chunked) searchLimit = Math.Max(searchLimit, topK * 10);

        IReadOnlyList<SearchResult> results =
            _store.SearchAll(new SearchQuery(query)
            {
                Limit = searchLimit,
                SupplementalScores = supplemental,
                EntityGraphScores = entityScores,
            });

        // Second-stage reranker runs BEFORE MMR so diversity sees reranker-ordered
        // scores, and before chunk dedup so per-chunk MaxSim scores are independent.
        // No-op when reranker is null or candidates are empty.
        if (Reranker is not null && results.Count > 0)
        {
            results = await Reranker.RerankAsync(query, results, ct);
        }

        if (_diversityInnerLimit > 0)
        {
            // Let MMR rerank the wider chunked set so diversity sees parents-via-chunks;
            // dedup below still trims to topK unique parents.
            int mmrLimit = _chunked ? Math.Max(topK * 10, 1) : Math.Max(1, topK);
            results = MmrReranker.Rerank(results, mmrLimit, _diversityLambda);
        }

        var hits = new List<RetrievalHit>(topK);
        HashSet<string>? seenParents = _chunked ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var r in results)
        {
            if (hits.Count >= topK) break;

            string id;
            string text;
            double score;
            switch (r)
            {
                case EntryResult er:
                    id = ExtractDocId(er.Item.Entry.Uri);
                    text = er.Item.Entry.ContentPreview ?? string.Empty;
                    score = er.Score;
                    break;
                case ChunkEntryResult cr:
                    id = ExtractDocId(cr.ParentItem.Entry.Uri);
                    text = cr.ParentItem.Entry.ContentPreview ?? string.Empty;
                    score = cr.Score;
                    break;
                default:
                    continue;
            }

            if (_chunked)
            {
                string parentId = StripChunkSuffix(id);
                if (!seenParents!.Add(parentId)) continue;  // already presented this parent
                id = parentId;
            }

            hits.Add(new RetrievalHit(Id: id, Text: text, Score: score));
        }
        return hits;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Clean up the previous question's dir before moving on so a 500q LongMemEval
        // run doesn't accumulate 26k metadata files on disk for the full run duration.
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
        _vectorCache = new ConcurrentDictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        _sentenceVectorCache = new ConcurrentDictionary<string, IReadOnlyList<float[]>>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Cosine similarity between the query and every cached doc vector, scaled by
    /// <see cref="_semanticWeight"/>, keyed for the search supplemental-score lookup
    /// (<c>"{scope}|{name}"</c>). All vectors land in scope <c>"local"</c> because
    /// <see cref="FileMemoryStore.Upsert(ArtifactEntry, string)"/> defaults there.
    /// Model2Vec output is already L2-normalized so dot product equals cosine.
    /// </summary>
    private Dictionary<string, double> BuildSupplementalScores(float[] queryVec)
    {
        var supplemental = new Dictionary<string, double>(_vectorCache.Count, StringComparer.OrdinalIgnoreCase);
        // Snapshot to prevent enumeration from racing with a late embedding write —
        // not strictly necessary because RetrieveAsync only runs after all
        // IngestAsync awaits complete (per-question barrier in BenchmarkRunner),
        // but cheap insurance and matches ConcurrentDictionary's enumeration contract.
        foreach (var kvp in _vectorCache)
        {
            double sim = CosineSimilarity(queryVec, kvp.Value);
            supplemental[$"local|{kvp.Key}"] = sim * _semanticWeight;
        }
        return supplemental;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (int i = 0; i < n; i++) dot += a[i] * b[i];
        return dot; // model2vec output is L2-normalized → dot product == cosine sim
    }

    private FileMemoryStore CreateStore()
    {
        // Per-question subdirs keep haystacks isolated and cheap to discard.
        string per = Path.Combine(_rootDir, $"q{_resetCount:D6}");
        Directory.CreateDirectory(per);
        return new FileMemoryStore(per) { RankerOptions = _rankerOptions };
    }

    /// <summary>
    /// Maps a benchmark doc ID into a filename-safe artifact name. The benchmark IDs
    /// (e.g. <c>conv_5_msg_12</c>, session UUIDs) already use safe characters, but we
    /// guard against slashes and Windows-reserved characters defensively.
    /// </summary>
    private static string SanitizeName(string id)
    {
        Span<char> buf = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return new string(buf);
    }

    /// <summary>Reads the benchmark doc ID from the entry's <c>bench://</c> URI.</summary>
    private static string ExtractDocId(string uri) =>
        uri.StartsWith("bench://", StringComparison.Ordinal) ? uri["bench://".Length..] : uri;

    /// <summary>
    /// Reverses <see cref="IngestAsync"/>'s chunked ID convention. Uses <c>LastIndexOf</c>
    /// so a parent ID that itself contains <c>::chunk_</c> still strips only the trailing
    /// chunk suffix. Returns the original id unchanged if no chunk suffix is present
    /// (whole-doc mode or pre-existing tests).
    /// </summary>
    private static string StripChunkSuffix(string id)
    {
        int idx = id.LastIndexOf(ChunkSeparator, StringComparison.Ordinal);
        return idx < 0 ? id : id[..idx];
    }
}
