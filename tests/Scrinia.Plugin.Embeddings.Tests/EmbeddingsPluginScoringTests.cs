using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Pins that <see cref="EmbeddingsPlugin.ComputeScoresAsync"/> routes through
/// <see cref="VectorScoreProjection.Project"/> rather than the ternary it used to hand-roll
/// (Task 5 review, Finding 4). The old code emitted exactly ONE key per hit —
/// <c>"{scope}|{name}|{chunkIndex}"</c> using the RAW stored chunk index when
/// <c>ChunkIndex</c> was non-null, or <c>"{scope}|{name}"</c> only when it was null. Under the
/// windowed contract every vector's <c>ChunkIndex</c> is non-null (there is no more whole-document
/// null-chunk vector), so the old code would never emit a memory-level key at all, and would key
/// elements by the RAW stored chunk index (e.g. 1000/2000) rather than the element index (1/2)
/// <see cref="VectorScoreProjection"/> maps it to. Nothing else in this test suite exercised the
/// plugin's read side at all.
/// </summary>
public sealed class EmbeddingsPluginScoringTests
{
    // Both vectors are already unit-length so VectorIndex.CosineSimilarity (dot product on
    // pre-normalized vectors, per its own doc comment) returns exact cosine similarities without
    // this test having to normalize anything itself. Element 1 is parallel to the query
    // (similarity 1.0); element 2 is NOT orthogonal (similarity 0.0 would be filtered out by
    // VectorIndex.Search's `if (sim > 0)` guard) but at a shallower angle (similarity 0.6), so
    // both elements produce a hit and the memory-level key's max is provably element 1's, not
    // element 2's.
    private sealed class TwoVectorProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:two-vector";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([[1f, 0f], [0.6f, 0.8f]]);

        public void Dispose() { }
    }

    [Fact]
    public async Task ComputeScoresAsync_EmitsBothMemoryAndElementKeys_ViaVectorScoreProjection()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-scoring-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);
        string vecDir = Path.Combine(root, "vec");
        Directory.CreateDirectory(vecDir);
        using var vectors = new VectorStore(vecDir, expectedSignature: null);

        using var plugin = new EmbeddingsPlugin();
        plugin.InitializeForTests(new TwoVectorProvider(), vectors, NullLogger.Instance);

        // Two content elements -> two window vectors at ChunkIndex 1000 (element 1, sim 1.0) and
        // 2000 (element 2, sim 0.6), written through the plugin's real write path.
        await plugin.OnStoredAsync("/note", ["elementA", "elementB"], store, default);

        var candidates = new[]
        {
            new ScopedArtifact("local", new ArtifactEntry("note", "", 0, 1, DateTimeOffset.UtcNow)),
        };
        var scores = await plugin.ComputeScoresAsync("query", candidates, store, default);

        scores.Should().NotBeNull();
        scores!.Should().ContainKey("local|note");
        scores["local|note"].Should().BeApproximately(50.0, 0.01,
            because: "the memory-level key must carry the MAX across the memory's window vectors " +
                     "(element 1's similarity 1.0 * default weight 50), not just whichever hit was seen last");
        scores.Should().ContainKey("local|note|1");
        scores["local|note|1"].Should().BeApproximately(50.0, 0.01,
            because: "element 1's own key, keyed by ELEMENT index — not the raw stored chunk index 1000");
        scores.Should().ContainKey("local|note|2");
        scores["local|note|2"].Should().BeApproximately(30.0, 0.01,
            because: "element 2's own key (similarity 0.6 * weight 50), keyed by element index 2, not raw chunk index 2000");
    }
}
