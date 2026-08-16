using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// The finding-H3 conformance proof, and the one test that fails on every pre-branch path.
///
/// <para>WeightedAttributeScorer's chunk pass probes "{scope}|{name}|{chunkIndex}" where
/// chunkIndex comes from ChunkEntry — 1-based over nmp2 content elements (TextAnalysis
/// emits `ChunkIndex: i + 1`). Before this branch, Core and Server wrote 0-based CHAR-WINDOW
/// ordinals into that key position, so the probe could never hit; the plugin wrote matching
/// element indices but never windowed, so a long element became one mean-pooled vector.
/// This asserts both halves at once.</para>
/// </summary>
public sealed class IndexerScorerContractTests
{
    private sealed class OrdinalProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:ordinal";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
        public void Dispose() { }
    }

    private static VectorStore NewStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VectorStore(dir);
    }

    [Fact]
    public async Task EveryKeyTheScorerProbes_IsAKeyTheIndexerProduces()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new VectorStore(dir);
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var indexer = new MemoryVectorIndexer(new OrdinalProvider(), store, NullLogger.Instance, options);

        // Three elements; the second is long enough to window several times.
        string[] content = ["short one", new string('b', 400), "short three"];
        await indexer.IndexAsync("local", "note", content, default);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hits = store.GetVectors("local").Select(v => (Entry: v, Similarity: 0.5)).ToList();
        VectorScoreProjection.Project("local", hits, weight: 100.0, into: scores);

        using var scope = new FluentAssertions.Execution.AssertionScope();

        scores.Should().ContainKey("local|note");
        for (int elementIndex = 1; elementIndex <= content.Length; elementIndex++)
        {
            scores.Should().ContainKey($"local|note|{elementIndex}",
                because: "TextAnalysis stamps ChunkEntry.ChunkIndex as i+1, and "
                       + "WeightedAttributeScorer.GetSupplemental probes exactly that value");
        }

        // And the long element really was windowed rather than mean-pooled over 400 chars.
        store.GetVectors("local")
            .Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 2)
            .Should().BeGreaterThan(1);
    }
}
