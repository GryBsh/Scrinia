using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies that the BM25+embeddings retrieval mode wires the supplemental-score
/// path correctly: embeddings actually change the ranking on a vocabulary-mismatch
/// query that BM25 alone would mishandle. Skipped automatically when the
/// model2vec MiniLM model isn't available on disk (CI without `scri setup`).
/// </summary>
public class EmbeddingsRetrieverTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private IEmbeddingProvider? _provider;

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-retbench-emb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        string modelDir = FindModelDir();
        if (Model2VecModelManager.IsModelAvailable(modelDir))
            _provider = Model2VecProvider.Load(modelDir, NullLogger.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Vocabulary_mismatch_query_finds_semantic_match()
    {
        if (_provider is null)
        {
            // Model not downloaded — skip rather than fail. CI without `scri setup` would
            // otherwise red-light. The plain ScriniaRetrieverTests still cover BM25.
            return;
        }

        await using var r = new ScriniaRetriever(_tempDir, embeddingProvider: _provider);

        // Doc text uses different vocabulary than the query — BM25 alone would miss this.
        // Embeddings should bridge the gap because "automobile" and "car" are close in MiniLM space.
        await r.IngestAsync(new HaystackDoc("auto", "My favorite automobile is a vintage convertible", null));
        await r.IngestAsync(new HaystackDoc("food", "Pizza is delicious especially with extra cheese", null));
        await r.IngestAsync(new HaystackDoc("travel", "Flights to Tokyo are expensive in summer", null));

        var hits = await r.RetrieveAsync("what car do they own", topK: 3);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("auto",
            because: "MiniLM embeddings put 'car' close to 'automobile' even though BM25 has no lexical overlap");
    }

    [Fact]
    public async Task Reset_clears_vector_cache()
    {
        if (_provider is null) return;

        await using var r = new ScriniaRetriever(_tempDir, embeddingProvider: _provider);
        await r.IngestAsync(new HaystackDoc("d1", "kubernetes deployment manifest", null));
        var before = await r.RetrieveAsync("orchestration platform", topK: 3);
        before.Should().NotBeEmpty();

        await r.ResetAsync();
        var after = await r.RetrieveAsync("orchestration platform", topK: 3);
        after.Should().BeEmpty();
    }

    private static string FindModelDir()
    {
        // Walk up from the test bin dir to find ./models/m2v-MiniLM-L6-v2/. CI checkouts
        // and dev workspaces both have this at the repo root.
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
        {
            string candidate = Path.Combine(cur, "models", "m2v-MiniLM-L6-v2");
            if (Directory.Exists(candidate)) return candidate;
            cur = Path.GetDirectoryName(cur) ?? string.Empty;
        }
        return Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");
    }
}
