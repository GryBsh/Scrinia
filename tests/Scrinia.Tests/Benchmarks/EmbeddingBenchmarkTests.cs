using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Benchmarks comparing BM25-only and Model2Vec embedding methods for store and search.
/// Downloads the Model2Vec model on first run; skips if the download fails (no network, etc.).
/// </summary>
public sealed class EmbeddingBenchmarkTests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");

    private readonly List<IDisposable> _disposables = [];
    private Model2VecProvider? _model2vec;
    private TimeSpan _downloadTime;
    private bool _modelWasAlreadyCached;

    // ── Lifecycle ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _modelWasAlreadyCached = Model2VecModelManager.IsModelAvailable(ModelDir);

        var sw = Stopwatch.StartNew();
        try
        {
            await Model2VecModelManager.EnsureModelAsync(ModelDir, NullLogger.Instance);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            output.WriteLine($"Model download failed ({ex.GetType().Name}: {ex.Message}) — embedding benchmarks will be skipped.");
            return;
        }
        sw.Stop();
        _downloadTime = sw.Elapsed;

        _model2vec = Model2VecProvider.Load(ModelDir, NullLogger.Instance);
        _disposables.Add(_model2vec);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private EmbeddingHarness CreateHarness(IEmbeddingProvider? provider)
    {
        var h = new EmbeddingHarness(provider);
        _disposables.Add(h);
        return h;
    }

    // ── Benchmarks ──────────────────────────────────────────────────────

    [SkippableFact]
    public void ModelDownload()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        output.WriteLine("=== Model Download ===");
        output.WriteLine("");
        if (_modelWasAlreadyCached)
        {
            output.WriteLine("  Status:  Already cached (no download needed)");
        }
        else
        {
            output.WriteLine($"  Status:  Downloaded");
            output.WriteLine($"  Time:    {_downloadTime.TotalSeconds:N1}s");
        }
        output.WriteLine($"  Path:    {ModelDir}");

        var files = Directory.GetFiles(ModelDir);
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        output.WriteLine($"  Files:   {files.Length}");
        output.WriteLine($"  Size:    {totalBytes / (1024.0 * 1024):N1} MB");
    }

    [SkippableFact]
    public async Task StoreLatency_Comparison()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var corpus = BenchmarkCorpus.Generate(50);

        var bm25Harness = CreateHarness(null);
        var m2vHarness = CreateHarness(_model2vec);

        // Warmup
        await bm25Harness.StoreFactAsync(corpus[0]);
        await m2vHarness.StoreFactAsync(corpus[0]);

        // BM25-only
        var bm25Sw = Stopwatch.StartNew();
        for (int i = 1; i < corpus.Count; i++)
            await bm25Harness.StoreFactAsync(corpus[i]);
        bm25Sw.Stop();

        // Model2Vec
        var m2vSw = Stopwatch.StartNew();
        for (int i = 1; i < corpus.Count; i++)
            await m2vHarness.StoreFactAsync(corpus[i]);
        m2vSw.Stop();

        int count = corpus.Count - 1;
        double bm25Ms = bm25Sw.Elapsed.TotalMilliseconds / count;
        double m2vMs = m2vSw.Elapsed.TotalMilliseconds / count;

        BenchmarkReporter.WriteComparisonTable(output,
            $"Store Latency ({count} facts)",
            ["Method", "Total (ms)", "Per-fact (ms)", "Overhead"],
            [
                ["BM25-only", $"{bm25Sw.Elapsed.TotalMilliseconds:N1}", $"{bm25Ms:N2}", "baseline"],
                ["Model2Vec", $"{m2vSw.Elapsed.TotalMilliseconds:N1}", $"{m2vMs:N2}", $"{m2vMs / bm25Ms:N1}x"],
            ]);
    }

    [SkippableFact]
    public async Task SearchLatency_Comparison()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        var bm25Harness = CreateHarness(null);
        var m2vHarness = CreateHarness(_model2vec);

        await bm25Harness.SetupCorpusAsync(corpus);
        await m2vHarness.SetupCorpusAsync(corpus);

        // Warmup
        bm25Harness.Search(queries[0].Question);
        m2vHarness.Search(queries[0].Question);

        // BM25-only
        var bm25Sw = Stopwatch.StartNew();
        for (int i = 1; i < queries.Count; i++)
            bm25Harness.Search(queries[i].Question);
        bm25Sw.Stop();

        // Model2Vec
        var m2vSw = Stopwatch.StartNew();
        for (int i = 1; i < queries.Count; i++)
            m2vHarness.Search(queries[i].Question);
        m2vSw.Stop();

        int count = queries.Count - 1;
        double bm25Ms = bm25Sw.Elapsed.TotalMilliseconds / count;
        double m2vMs = m2vSw.Elapsed.TotalMilliseconds / count;

        BenchmarkReporter.WriteComparisonTable(output,
            $"Search Latency ({count} queries, {corpus.Count}-fact corpus)",
            ["Method", "Total (ms)", "Per-query (ms)", "Overhead"],
            [
                ["BM25-only", $"{bm25Sw.Elapsed.TotalMilliseconds:N1}", $"{bm25Ms:N2}", "baseline"],
                ["Model2Vec", $"{m2vSw.Elapsed.TotalMilliseconds:N1}", $"{m2vMs:N2}", $"{m2vMs / bm25Ms:N1}x"],
            ]);
    }

    [SkippableFact]
    public async Task SearchQuality_Recall_Comparison()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        var bm25Harness = CreateHarness(null);
        var m2vHarness = CreateHarness(_model2vec);

        await bm25Harness.SetupCorpusAsync(corpus);
        await m2vHarness.SetupCorpusAsync(corpus);

        int bm25At1 = 0, m2vAt1 = 0;
        int bm25At5 = 0, m2vAt5 = 0;

        foreach (var fact in queries)
        {
            string targetName = $"{fact.Topic}:{fact.Key}";

            var bm25Results = bm25Harness.SearchNames(fact.Question, 10);
            if (bm25Results.Count > 0 && bm25Results[0] == targetName) bm25At1++;
            if (bm25Results.Take(5).Contains(targetName)) bm25At5++;

            var m2vResults = m2vHarness.SearchNames(fact.Question, 10);
            if (m2vResults.Count > 0 && m2vResults[0] == targetName) m2vAt1++;
            if (m2vResults.Take(5).Contains(targetName)) m2vAt5++;
        }

        int n = queries.Count;
        BenchmarkReporter.WriteComparisonTable(output,
            $"Search Quality ({n} queries, {corpus.Count}-fact corpus)",
            ["Method", "Recall@1", "Recall@5"],
            [
                ["BM25-only", $"{bm25At1}/{n} ({bm25At1 * 100.0 / n:N1}%)", $"{bm25At5}/{n} ({bm25At5 * 100.0 / n:N1}%)"],
                ["Model2Vec", $"{m2vAt1}/{n} ({m2vAt1 * 100.0 / n:N1}%)", $"{m2vAt5}/{n} ({m2vAt5 * 100.0 / n:N1}%)"],
            ]);

        output.WriteLine("");
        BenchmarkReporter.WriteVerdict(output, "Recall@1",
            m2vAt1 >= bm25At1 ? "Model2Vec" : "BM25-only",
            $"Model2Vec={m2vAt1}/{n}, BM25={bm25At1}/{n}");
        BenchmarkReporter.WriteVerdict(output, "Recall@5",
            m2vAt5 >= bm25At5 ? "Model2Vec" : "BM25-only",
            $"Model2Vec={m2vAt5}/{n}, BM25={bm25At5}/{n}");
    }

    [SkippableFact]
    public async Task SemanticSearch_Model2Vec_FindsParaphrasedQueries()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var harness = CreateHarness(_model2vec);

        // Store facts with specific keywords
        await harness.StoreAsync("auth-flow", "api", "OAuth2 authentication flow uses PKCE for single-page applications");
        await harness.StoreAsync("db-scaling", "arch", "PostgreSQL read replicas handle horizontal scaling for query load");
        await harness.StoreAsync("k8s-deploy", "deploy", "Kubernetes rolling update strategy with health check probes");
        await harness.StoreAsync("memory-leak", "debug", "Heap dump analysis for detecting managed memory leaks in production");
        await harness.StoreAsync("cache-config", "config", "Redis cache TTL configuration with sliding expiration windows");

        // Paraphrased queries — no exact keyword overlap
        var paraphrases = new (string Query, string ExpectedName)[]
        {
            ("login security for browser apps", "auth-flow"),
            ("database horizontal read performance", "db-scaling"),
            ("container orchestration deployment", "k8s-deploy"),
            ("finding resource leaks in running services", "memory-leak"),
            ("time-based eviction settings for distributed cache", "cache-config"),
        };

        int hits = 0;
        var rows = new List<string[]>();
        foreach (var (query, expectedName) in paraphrases)
        {
            var names = harness.SearchNames(query, 5);
            string topResult = names.Count > 0 ? names[0] : "(none)";
            bool found = names.Take(3).Any(n => n.EndsWith(expectedName));
            if (found) hits++;
            rows.Add([query, expectedName, topResult, found ? "HIT" : "MISS"]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            "Semantic Search — Paraphrased Queries (Model2Vec)",
            ["Query", "Expected", "Top Result", "Status"],
            rows);

        output.WriteLine("");
        output.WriteLine($"Semantic recall: {hits}/{paraphrases.Length} ({hits * 100.0 / paraphrases.Length:N0}%)");

        hits.Should().BeGreaterThan(0, "Model2Vec should find at least one paraphrased result");
    }

    [SkippableFact]
    public async Task EmbeddingThroughput_Model2Vec()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var corpus = BenchmarkCorpus.Generate(200);
        var texts = corpus.Select(f => f.Content).ToArray();

        // Warmup
        await _model2vec.EmbedAsync(texts[0]);

        // Single-threaded throughput
        var sw = Stopwatch.StartNew();
        for (int i = 1; i < texts.Length; i++)
            await _model2vec.EmbedAsync(texts[i]);
        sw.Stop();

        int count = texts.Length - 1;
        double perMs = sw.Elapsed.TotalMilliseconds / count;
        double perSec = count / sw.Elapsed.TotalSeconds;

        output.WriteLine($"=== Model2Vec Embedding Throughput ===");
        output.WriteLine($"");
        output.WriteLine($"  Texts embedded:  {count}");
        output.WriteLine($"  Total time:      {sw.Elapsed.TotalMilliseconds:N1} ms");
        output.WriteLine($"  Per embedding:   {perMs:N2} ms");
        output.WriteLine($"  Throughput:      {perSec:N0} embeddings/sec");
        output.WriteLine($"  Dimensions:      {_model2vec.Dimensions}");
    }

    [SkippableFact]
    public async Task ScalingBehavior_StoreAndSearch()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        int[] scales = [25, 50, 100, 200];
        var bm25StoreTimes = new double[scales.Length];
        var m2vStoreTimes = new double[scales.Length];
        var bm25SearchTimes = new double[scales.Length];
        var m2vSearchTimes = new double[scales.Length];

        for (int s = 0; s < scales.Length; s++)
        {
            var corpus = BenchmarkCorpus.Generate(scales[s]);
            var queries = corpus.Where((_, i) => i % 5 == 0).Take(10).ToList();

            // Store benchmark
            var bm25H = CreateHarness(null);
            var m2vH = CreateHarness(_model2vec);

            var sw = Stopwatch.StartNew();
            await bm25H.SetupCorpusAsync(corpus);
            bm25StoreTimes[s] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            await m2vH.SetupCorpusAsync(corpus);
            m2vStoreTimes[s] = sw.Elapsed.TotalMilliseconds;

            // Search benchmark
            sw.Restart();
            foreach (var q in queries) bm25H.Search(q.Question);
            bm25SearchTimes[s] = sw.Elapsed.TotalMilliseconds / queries.Count;

            sw.Restart();
            foreach (var q in queries) m2vH.Search(q.Question);
            m2vSearchTimes[s] = sw.Elapsed.TotalMilliseconds / queries.Count;
        }

        BenchmarkReporter.WriteScalingTable(output,
            "Store Time (ms total) vs Corpus Size",
            "facts",
            scales,
            new Dictionary<string, double[]>
            {
                ["BM25-only"] = bm25StoreTimes,
                ["Model2Vec"] = m2vStoreTimes,
            });

        BenchmarkReporter.WriteScalingTable(output,
            "Search Latency (ms/query) vs Corpus Size",
            "facts",
            scales,
            new Dictionary<string, double[]>
            {
                ["BM25-only"] = bm25SearchTimes,
                ["Model2Vec"] = m2vSearchTimes,
            });
    }

    [SkippableFact]
    public async Task Summary_AllMethods()
    {
        Skip.If(_model2vec is null, "Model download failed (no network?)");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        // Setup
        var bm25H = CreateHarness(null);
        var m2vH = CreateHarness(_model2vec);

        var bm25StoreSw = Stopwatch.StartNew();
        await bm25H.SetupCorpusAsync(corpus);
        bm25StoreSw.Stop();

        var m2vStoreSw = Stopwatch.StartNew();
        await m2vH.SetupCorpusAsync(corpus);
        m2vStoreSw.Stop();

        // Search latency + recall
        int bm25Hits = 0, m2vHits = 0;
        double bm25SearchMs = 0, m2vSearchMs = 0;

        foreach (var fact in queries)
        {
            string target = $"{fact.Topic}:{fact.Key}";

            var sw = Stopwatch.StartNew();
            var bm25Names = bm25H.SearchNames(fact.Question, 10);
            bm25SearchMs += sw.Elapsed.TotalMilliseconds;
            if (bm25Names.Take(5).Contains(target)) bm25Hits++;

            sw.Restart();
            var m2vNames = m2vH.SearchNames(fact.Question, 10);
            m2vSearchMs += sw.Elapsed.TotalMilliseconds;
            if (m2vNames.Take(5).Contains(target)) m2vHits++;
        }

        int n = queries.Count;

        output.WriteLine("");
        output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        output.WriteLine("║            Embedding Benchmark Summary (100 facts)          ║");
        output.WriteLine("╠══════════════════════════════════════════════════════════════╣");

        BenchmarkReporter.WriteComparisonTable(output,
            "Summary",
            ["Metric", "BM25-only", "Model2Vec (256d)"],
            [
                ["Model download",         "n/a", _modelWasAlreadyCached ? "cached" : $"{_downloadTime.TotalSeconds:N1}s"],
                ["Store (100 facts)",       $"{bm25StoreSw.Elapsed.TotalMilliseconds:N0} ms",  $"{m2vStoreSw.Elapsed.TotalMilliseconds:N0} ms"],
                ["Store (per fact)",        $"{bm25StoreSw.Elapsed.TotalMilliseconds / corpus.Count:N2} ms", $"{m2vStoreSw.Elapsed.TotalMilliseconds / corpus.Count:N2} ms"],
                ["Search (per query)",      $"{bm25SearchMs / n:N2} ms",  $"{m2vSearchMs / n:N2} ms"],
                ["Recall@5 (20 queries)",   $"{bm25Hits}/{n} ({bm25Hits * 100.0 / n:N0}%)", $"{m2vHits}/{n} ({m2vHits * 100.0 / n:N0}%)"],
                ["Dimensions",              "n/a",   "256"],
                ["Native deps",             "none",  "none"],
                ["Model size",              "n/a",   "~30 MB"],
            ]);

        output.WriteLine("");
        output.WriteLine("  Notes:");
        output.WriteLine("  - BM25-only uses keyword matching + weighted field scoring");
        output.WriteLine("  - Model2Vec adds semantic reranking (m2v-MiniLM-L6-v2, 384d, pure C#)");
        output.WriteLine("  - Both Model2Vec and Vulkan use MiniLM-L6-v2 (384d, same vector space)");
    }

    // ── Harness ─────────────────────────────────────────────────────────

    /// <summary>
    /// Isolated test harness wrapping FileMemoryStore + optional embeddings.
    /// Each instance gets its own temp directory and vector store.
    /// </summary>
    private sealed class EmbeddingHarness : IDisposable
    {
        private readonly string _tempDir;
        private readonly FileMemoryStore _store;
        private readonly VectorStore? _vectorStore;
        private readonly HybridReranker? _reranker;
        private readonly IEmbeddingProvider? _provider;

        public EmbeddingHarness(IEmbeddingProvider? provider)
        {
            _provider = provider;
            _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_embbench_{Guid.NewGuid():N}");
            string storeDir = Path.Combine(_tempDir, ".scrinia", "store");
            Directory.CreateDirectory(storeDir);
            _store = new FileMemoryStore(_tempDir);

            if (provider is not null && provider.IsAvailable)
            {
                string embDir = Path.Combine(_tempDir, ".scrinia", "embeddings");
                Directory.CreateDirectory(embDir);
                _vectorStore = new VectorStore(embDir);
                _reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);
            }
        }

        public async Task StoreAsync(string name, string topic, string content)
        {
            // Use local-topic:{topic} scope for topic-scoped storage
            string scope = $"local-topic:{topic}";
            string artifact = Nmp2ChunkedEncoder.Encode(content);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            string preview = _store.GenerateContentPreview(content);

            var entry = new ArtifactEntry(
                Name: name,
                Uri: "",
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: content.Length > 200 ? content[..200] : content,
                ContentPreview: preview);

            _store.Upsert(entry, scope);
            await _store.WriteArtifactAsync(name, scope, artifact);

            // Index vector if embeddings available
            if (_provider is not null && _vectorStore is not null)
            {
                var vec = await _provider.EmbedAsync(content);
                if (vec is not null)
                    await _vectorStore.UpsertAsync(scope, name, null, vec);
            }
        }

        public async Task StoreFactAsync(BenchmarkFact fact)
        {
            await StoreAsync(fact.Key, fact.Topic, fact.Content);
        }

        public async Task SetupCorpusAsync(IReadOnlyList<BenchmarkFact> corpus)
        {
            foreach (var fact in corpus)
                await StoreFactAsync(fact);
        }

        public IReadOnlyList<SearchResult> Search(string query, int limit = 10)
        {
            // Get supplemental scores from reranker if available
            IReadOnlyDictionary<string, double>? supplemental = null;
            if (_reranker is not null)
            {
                var candidates = _store.ListScoped();
                supplemental = _reranker.ComputeScoresAsync(query, candidates, _store, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            return _store.SearchAll(query, null, limit, supplemental);
        }

        public List<string> SearchNames(string query, int limit = 10)
        {
            var results = Search(query, limit);
            var names = new List<string>();
            foreach (var result in results)
            {
                string scope, name;
                if (result is EntryResult er)
                    (scope, name) = (er.Item.Scope, er.Item.Entry.Name);
                else if (result is ChunkEntryResult cr)
                    (scope, name) = (cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                else continue;

                // Convert "local-topic:api" scope to "api" for matching
                string topicPrefix = scope.StartsWith("local-topic:", StringComparison.Ordinal)
                    ? scope["local-topic:".Length..]
                    : scope;
                names.Add($"{topicPrefix}:{name}");
            }
            return names;
        }

        public void Dispose()
        {
            _vectorStore?.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
