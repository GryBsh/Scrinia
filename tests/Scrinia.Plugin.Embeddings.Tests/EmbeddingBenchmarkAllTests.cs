using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Embeddings;
using Scrinia.Tests.Benchmarks;
using Xunit;
using Xunit.Abstractions;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Shared fixture: downloads models once, creates providers once per test class collection.
/// </summary>
public sealed class EmbeddingProvidersFixture : IAsyncLifetime, IDisposable
{
    private static readonly string Model2VecDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");
    private static readonly string VulkanModelDir = Path.Combine(AppContext.BaseDirectory, "models", "vulkan");

    public Model2VecProvider? Model2Vec { get; private set; }
    public VulkanEmbeddingProvider? Vulkan { get; private set; }
    public TimeSpan M2vDownloadTime { get; private set; }
    public TimeSpan VulkanDownloadTime { get; private set; }
    public bool M2vWasCached { get; private set; }
    public bool VulkanWasCached { get; private set; }
    public string? M2vSkipReason { get; private set; }
    public string? VulkanSkipReason { get; private set; }
    public bool HasAnyProvider => Model2Vec is not null || Vulkan is not null;

    public async Task InitializeAsync()
    {
        // Model2Vec download
        M2vWasCached = Model2VecModelManager.IsModelAvailable(Model2VecDir);
        var sw = Stopwatch.StartNew();
        try
        {
            await Model2VecModelManager.EnsureModelAsync(Model2VecDir, NullLogger.Instance);
            sw.Stop();
            M2vDownloadTime = sw.Elapsed;
            Model2Vec = Model2VecProvider.Load(Model2VecDir, NullLogger.Instance);
        }
        catch (Exception ex)
        {
            M2vSkipReason = $"Model2Vec failed: {ex.GetType().Name}: {ex.Message}";
        }

        // Vulkan model download
        VulkanWasCached = VulkanModelManager.IsModelAvailable(VulkanModelDir);
        sw.Restart();
        try
        {
            await VulkanModelManager.EnsureModelAsync(VulkanModelDir, NullLogger.Instance);
            sw.Stop();
            VulkanDownloadTime = sw.Elapsed;

            string modelPath = VulkanModelManager.GetModelPath(VulkanModelDir);
            Vulkan = VulkanEmbeddingProvider.Create(modelPath, 384, NullLogger.Instance);
        }
        catch (Exception ex)
        {
            VulkanSkipReason = $"Vulkan failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        Model2Vec?.Dispose();
        Vulkan?.Dispose();
    }
}

/// <summary>
/// Full 3-way benchmark: BM25-only vs Model2Vec (built-in) vs Vulkan (LLamaSharp GPU).
/// Downloads both models once via shared fixture; each provider skips independently if its download fails.
/// </summary>
public sealed class EmbeddingBenchmarkAllTests(ITestOutputHelper output, EmbeddingProvidersFixture fx)
    : IClassFixture<EmbeddingProvidersFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    // ── Helpers ──────────────────────────────────────────────────────────

    private EmbeddingHarness CreateHarness(IEmbeddingProvider? provider)
    {
        var h = new EmbeddingHarness(provider);
        _disposables.Add(h);
        return h;
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    // ── Benchmarks ──────────────────────────────────────────────────────

    [SkippableFact]
    public void ModelDownload()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available (no network?)");

        output.WriteLine("=== Model Download ===");
        output.WriteLine("");

        if (fx.Model2Vec is not null)
        {
            if (fx.M2vWasCached)
                output.WriteLine("  Model2Vec:  cached");
            else
                output.WriteLine($"  Model2Vec:  downloaded in {fx.M2vDownloadTime.TotalSeconds:N1}s");

            output.WriteLine($"              {fx.Model2Vec.Dimensions}d");
        }
        else
        {
            output.WriteLine($"  Model2Vec:  SKIPPED ({fx.M2vSkipReason})");
        }

        if (fx.Vulkan is not null)
        {
            if (fx.VulkanWasCached)
                output.WriteLine("  Vulkan:     cached");
            else
                output.WriteLine($"  Vulkan:     downloaded in {fx.VulkanDownloadTime.TotalSeconds:N1}s");

            output.WriteLine($"              {fx.Vulkan.Dimensions}d");
        }
        else
        {
            output.WriteLine($"  Vulkan:     SKIPPED ({fx.VulkanSkipReason})");
        }
    }

    [SkippableFact]
    public async Task EmbeddingThroughput()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available");

        var corpus = BenchmarkCorpus.Generate(100);
        var texts = corpus.Select(f => f.Content).ToArray();

        var rows = new List<string[]>();

        if (fx.Model2Vec is not null)
        {
            await fx.Model2Vec.EmbedAsync(texts[0]); // warmup
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < texts.Length; i++)
                await fx.Model2Vec.EmbedAsync(texts[i]);
            sw.Stop();
            int count = texts.Length - 1;
            double perSec = count / sw.Elapsed.TotalSeconds;
            rows.Add(["Model2Vec", $"{fx.Model2Vec.Dimensions}", $"{sw.Elapsed.TotalMilliseconds / count:N3}", $"{perSec:N0}"]);
        }

        if (fx.Vulkan is not null)
        {
            await fx.Vulkan.EmbedAsync(texts[0]); // warmup
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < texts.Length; i++)
                await fx.Vulkan.EmbedAsync(texts[i]);
            sw.Stop();
            int count = texts.Length - 1;
            double perSec = count / sw.Elapsed.TotalSeconds;
            rows.Add(["Vulkan", $"{fx.Vulkan.Dimensions}", $"{sw.Elapsed.TotalMilliseconds / count:N3}", $"{perSec:N0}"]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            $"Embedding Throughput ({texts.Length - 1} texts)",
            ["Provider", "Dims", "Per-embed (ms)", "Throughput (emb/s)"],
            rows);
    }

    [SkippableFact]
    public async Task StoreLatency_Comparison()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available");

        var corpus = BenchmarkCorpus.Generate(50);

        // BM25 baseline
        var bm25H = CreateHarness(null);
        await bm25H.StoreFactAsync(corpus[0]); // warmup
        var bm25Sw = Stopwatch.StartNew();
        for (int i = 1; i < corpus.Count; i++) await bm25H.StoreFactAsync(corpus[i]);
        bm25Sw.Stop();

        int count = corpus.Count - 1;
        double bm25Ms = bm25Sw.Elapsed.TotalMilliseconds / count;

        var rows = new List<string[]> { new[] { "BM25-only", $"{bm25Sw.Elapsed.TotalMilliseconds:N1}", $"{bm25Ms:N2}", "baseline" } };

        if (fx.Model2Vec is not null)
        {
            var h = CreateHarness(fx.Model2Vec);
            await h.StoreFactAsync(corpus[0]);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < corpus.Count; i++) await h.StoreFactAsync(corpus[i]);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / count;
            rows.Add(["Model2Vec", $"{sw.Elapsed.TotalMilliseconds:N1}", $"{ms:N2}", $"{ms / bm25Ms:N1}x"]);
        }

        if (fx.Vulkan is not null)
        {
            var h = CreateHarness(fx.Vulkan);
            await h.StoreFactAsync(corpus[0]);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < corpus.Count; i++) await h.StoreFactAsync(corpus[i]);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / count;
            rows.Add(["Vulkan", $"{sw.Elapsed.TotalMilliseconds:N1}", $"{ms:N2}", $"{ms / bm25Ms:N1}x"]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            $"Store Latency ({count} facts)",
            ["Method", "Total (ms)", "Per-fact (ms)", "Overhead"],
            rows);
    }

    [SkippableFact]
    public async Task SearchLatency_Comparison()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        // BM25 baseline
        var bm25H = CreateHarness(null);
        await bm25H.SetupCorpusAsync(corpus);
        bm25H.Search(queries[0].Question); // warmup
        var bm25Sw = Stopwatch.StartNew();
        for (int i = 1; i < queries.Count; i++) bm25H.Search(queries[i].Question);
        bm25Sw.Stop();
        int count = queries.Count - 1;
        double bm25Ms = bm25Sw.Elapsed.TotalMilliseconds / count;

        var rows = new List<string[]> { new[] { "BM25-only", $"{bm25Sw.Elapsed.TotalMilliseconds:N1}", $"{bm25Ms:N2}", "baseline" } };

        if (fx.Model2Vec is not null)
        {
            var h = CreateHarness(fx.Model2Vec);
            await h.SetupCorpusAsync(corpus);
            h.Search(queries[0].Question);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < queries.Count; i++) h.Search(queries[i].Question);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / count;
            rows.Add(["Model2Vec", $"{sw.Elapsed.TotalMilliseconds:N1}", $"{ms:N2}", $"{ms / bm25Ms:N1}x"]);
        }

        if (fx.Vulkan is not null)
        {
            var h = CreateHarness(fx.Vulkan);
            await h.SetupCorpusAsync(corpus);
            h.Search(queries[0].Question);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < queries.Count; i++) h.Search(queries[i].Question);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / count;
            rows.Add(["Vulkan", $"{sw.Elapsed.TotalMilliseconds:N1}", $"{ms:N2}", $"{ms / bm25Ms:N1}x"]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            $"Search Latency ({count} queries, {corpus.Count}-fact corpus)",
            ["Method", "Total (ms)", "Per-query (ms)", "Overhead"],
            rows);
    }

    [SkippableFact]
    public async Task SearchQuality_Recall_Comparison()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();
        int n = queries.Count;

        // BM25 baseline
        var bm25H = CreateHarness(null);
        await bm25H.SetupCorpusAsync(corpus);
        int bm25At1 = 0, bm25At5 = 0;
        foreach (var f in queries)
        {
            string t = $"{f.Topic}:{f.Key}";
            var r = bm25H.SearchNames(f.Question, 10);
            if (r.Count > 0 && r[0] == t) bm25At1++;
            if (r.Take(5).Contains(t)) bm25At5++;
        }

        var rows = new List<string[]>
        {
            new[] { "BM25-only", $"{bm25At1}/{n} ({bm25At1 * 100.0 / n:N1}%)", $"{bm25At5}/{n} ({bm25At5 * 100.0 / n:N1}%)" },
        };

        if (fx.Model2Vec is not null)
        {
            var h = CreateHarness(fx.Model2Vec);
            await h.SetupCorpusAsync(corpus);
            int at1 = 0, at5 = 0;
            foreach (var f in queries)
            {
                string t = $"{f.Topic}:{f.Key}";
                var r = h.SearchNames(f.Question, 10);
                if (r.Count > 0 && r[0] == t) at1++;
                if (r.Take(5).Contains(t)) at5++;
            }
            rows.Add(["Model2Vec", $"{at1}/{n} ({at1 * 100.0 / n:N1}%)", $"{at5}/{n} ({at5 * 100.0 / n:N1}%)"]);
        }

        if (fx.Vulkan is not null)
        {
            var h = CreateHarness(fx.Vulkan);
            await h.SetupCorpusAsync(corpus);
            int at1 = 0, at5 = 0;
            foreach (var f in queries)
            {
                string t = $"{f.Topic}:{f.Key}";
                var r = h.SearchNames(f.Question, 10);
                if (r.Count > 0 && r[0] == t) at1++;
                if (r.Take(5).Contains(t)) at5++;
            }
            rows.Add(["Vulkan", $"{at1}/{n} ({at1 * 100.0 / n:N1}%)", $"{at5}/{n} ({at5 * 100.0 / n:N1}%)"]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            $"Search Quality ({n} queries, {corpus.Count}-fact corpus)",
            ["Method", "Recall@1", "Recall@5"],
            rows);
    }

    [SkippableFact]
    public async Task Summary()
    {
        Skip.If(!fx.HasAnyProvider, "No embedding models available");

        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();
        int n = queries.Count;

        var providers = new List<(string Name, string Dims, string Download, IEmbeddingProvider? Provider)>
        {
            ("BM25-only", "n/a", "n/a", null),
        };

        if (fx.Model2Vec is not null)
            providers.Add(("Model2Vec", $"{fx.Model2Vec.Dimensions}", fx.M2vWasCached ? "cached" : $"{fx.M2vDownloadTime.TotalSeconds:N1}s", fx.Model2Vec));
        if (fx.Vulkan is not null)
            providers.Add(("Vulkan", "384", fx.VulkanWasCached ? "cached" : $"{fx.VulkanDownloadTime.TotalSeconds:N1}s", fx.Vulkan));

        var headers = new[] { "Metric" }.Concat(providers.Select(p => p.Name)).ToArray();
        var downloadRow = new[] { "Model download" }.Concat(providers.Select(p => p.Download)).ToArray();
        var dimsRow = new[] { "Dimensions" }.Concat(providers.Select(p => p.Dims)).ToArray();
        var nativeDepsRow = new[] { "Native deps" }.Concat(providers.Select(p =>
            p.Name == "Vulkan" ? "LLamaSharp" : "none")).ToArray();

        var storeRow = new string[providers.Count + 1];
        storeRow[0] = "Store (per fact)";
        var searchRow = new string[providers.Count + 1];
        searchRow[0] = "Search (per query)";
        var recallRow = new string[providers.Count + 1];
        recallRow[0] = $"Recall@5 ({n} queries)";

        for (int p = 0; p < providers.Count; p++)
        {
            var h = CreateHarness(providers[p].Provider);
            var sw = Stopwatch.StartNew();
            await h.SetupCorpusAsync(corpus);
            sw.Stop();
            storeRow[p + 1] = $"{sw.Elapsed.TotalMilliseconds / corpus.Count:N2} ms";

            int hits = 0;
            double searchMs = 0;
            foreach (var fact in queries)
            {
                string target = $"{fact.Topic}:{fact.Key}";
                sw.Restart();
                var names = h.SearchNames(fact.Question, 10);
                searchMs += sw.Elapsed.TotalMilliseconds;
                if (names.Take(5).Contains(target)) hits++;
            }
            searchRow[p + 1] = $"{searchMs / n:N2} ms";
            recallRow[p + 1] = $"{hits}/{n} ({hits * 100.0 / n:N0}%)";
        }

        output.WriteLine("");
        output.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        output.WriteLine("║               Embedding Benchmark Summary (100 facts)               ║");
        output.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");

        BenchmarkReporter.WriteComparisonTable(output, "Summary", headers,
            [downloadRow, storeRow, searchRow, recallRow, dimsRow, nativeDepsRow]);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    /// <summary>
    /// Isolated test harness wrapping FileMemoryStore + optional embeddings.
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
            Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia", "store"));
            _store = new FileMemoryStore(_tempDir);

            if (provider is not null && provider.IsAvailable)
            {
                string embDir = Path.Combine(_tempDir, ".scrinia", "embeddings");
                Directory.CreateDirectory(embDir);
                _vectorStore = new VectorStore(embDir);
                _reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);
            }
        }

        public async Task StoreFactAsync(BenchmarkFact fact)
        {
            string scope = $"local-topic:{fact.Topic}";
            string artifact = Nmp2ChunkedEncoder.Encode(fact.Content);
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
            long originalBytes = System.Text.Encoding.UTF8.GetByteCount(fact.Content);

            var entry = new ArtifactEntry(
                Name: fact.Key, Uri: "", OriginalBytes: originalBytes, ChunkCount: chunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                Description: fact.Content.Length > 200 ? fact.Content[..200] : fact.Content,
                ContentPreview: _store.GenerateContentPreview(fact.Content));

            _store.Upsert(entry, scope);
            await _store.WriteArtifactAsync(fact.Key, scope, artifact);

            if (_provider is not null && _vectorStore is not null)
            {
                var vec = await _provider.EmbedAsync(fact.Content);
                if (vec is not null)
                    await _vectorStore.UpsertAsync(scope, fact.Key, null, vec);
            }
        }

        public async Task SetupCorpusAsync(IReadOnlyList<BenchmarkFact> corpus)
        {
            foreach (var fact in corpus)
                await StoreFactAsync(fact);
        }

        public IReadOnlyList<SearchResult> Search(string query, int limit = 10)
        {
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

                string topic = scope.StartsWith("local-topic:", StringComparison.Ordinal)
                    ? scope["local-topic:".Length..] : scope;
                names.Add($"{topic}:{name}");
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
