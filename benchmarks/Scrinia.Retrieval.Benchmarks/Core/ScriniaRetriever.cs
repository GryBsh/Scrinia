using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

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
/// <see cref="FileMemoryStore.SearchAll(string, string?, int, IReadOnlyDictionary{string, double}?)"/>.
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
    private FileMemoryStore _store;
    private int _resetCount;
    private Dictionary<string, float[]> _vectorCache;

    public ScriniaRetriever(
        string rootDir,
        RankerOptions? rankerOptions = null,
        IEmbeddingProvider? embeddingProvider = null,
        double semanticWeight = 50.0,
        double diversityLambda = 1.0,
        int diversityInnerLimit = 0,
        bool chunked = false,
        int chunkWindowSize = TextChunker.DefaultWindowSize,
        int chunkOverlap = TextChunker.DefaultOverlap)
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
        _store = CreateStore();
        _vectorCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task IngestAsync(HaystackDoc doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ct.ThrowIfCancellationRequested();

        if (!_chunked)
        {
            await IngestEntryAsync(doc.Id, doc.Text, doc.Timestamp, ct);
            return;
        }

        // Chunked path: slice via prod's TextChunker so window boundaries match prod
        // semantics, ingest each window as its own ArtifactEntry keyed by
        // `{parentId}::chunk_{i}`. RetrieveAsync collapses chunks back to one hit per
        // parent. Short docs (≤ windowSize) produce a single chunk and behave like
        // the whole-doc path — that's TextChunker's contract.
        var windows = TextChunker.SliceWindows(doc.Text, _chunkWindowSize, _chunkOverlap);
        if (windows.Count == 0) return;
        foreach (var (idx, text) in windows)
        {
            string chunkId = $"{doc.Id}{ChunkSeparator}{idx}";
            await IngestEntryAsync(chunkId, text, doc.Timestamp, ct);
        }
    }

    private async Task IngestEntryAsync(string id, string text, DateTimeOffset? timestamp, CancellationToken ct)
    {
        string sanitized = SanitizeName(id);
        var (keywords, tf) = TextAnalysis.AnalyzeText(text);
        var entry = new ArtifactEntry(
            // Description and Name are both scored by the field scorer; we keep the
            // benchmark doc ID *only* in Uri so query tokens never accidentally match
            // an ID component (e.g. "user" / "msg" in `convomem_user_5_msg_12`).
            Name: sanitized,
            Uri: $"bench://{id}",
            OriginalBytes: text.Length,
            ChunkCount: 0,
            CreatedAt: timestamp ?? DateTimeOffset.UtcNow,
            Description: string.Empty,
            Keywords: keywords,
            TermFrequencies: tf,
            ContentPreview: text);

        _store.Upsert(entry);

        if (_embeddingProvider is not null)
        {
            var vec = await _embeddingProvider.EmbedAsync(text, ct);
            if (vec is not null) _vectorCache[sanitized] = vec;
        }
    }

    public async Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, double>? supplemental = null;
        if (_embeddingProvider is not null && _vectorCache.Count > 0)
        {
            var queryVec = await _embeddingProvider.EmbedAsync(query, ct);
            if (queryVec is not null)
                supplemental = BuildSupplementalScores(queryVec);
        }

        // In chunked mode we need a wider candidate set than topK because dedup will
        // collapse multiple chunks of the same parent to one hit. 10x is generous;
        // SearchAll returns whatever the store has if the limit exceeds it.
        int searchLimit = _diversityInnerLimit > 0 ? Math.Max(_diversityInnerLimit, topK) : Math.Max(1, topK);
        if (_chunked) searchLimit = Math.Max(searchLimit, topK * 10);

        IReadOnlyList<SearchResult> results =
            _store.SearchAll(query, scopes: null, limit: searchLimit, supplementalScores: supplemental);

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
        _vectorCache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
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
        foreach (var (sanitizedName, docVec) in _vectorCache)
        {
            double sim = CosineSimilarity(queryVec, docVec);
            supplemental[$"local|{sanitizedName}"] = sim * _semanticWeight;
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
