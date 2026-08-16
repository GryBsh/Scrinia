using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Finding H3, read side. Four hand-copied projection loops each decided the key contract for
/// themselves; one of them (EmbeddingsTools.Search) did not max-aggregate at all, so a chunk
/// hit overwrote the memory-level score instead of raising it.
/// </summary>
public sealed class VectorScoreProjectionTests
{
    [Fact]
    public void EmitsBothTheMemoryKeyAndTheElementKey()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // element 2, window 0
        int chunk = 2 * MemoryVectorIndexer.ChunkIndexStride;

        VectorScoreProjection.Project(
            "local", [(new VectorEntry("note", chunk, [1f]), 0.5)], weight: 100.0, into: scores);

        scores.Should().ContainKey("local|note");
        scores.Should().ContainKey("local|note|2",
            because: "the scorer's chunk pass probes the ELEMENT index, not the window ordinal");
    }

    [Fact]
    public void ElementKey_TakesTheMaxOverThatElementsWindows()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int w0 = 1 * MemoryVectorIndexer.ChunkIndexStride + 0;
        int w1 = 1 * MemoryVectorIndexer.ChunkIndexStride + 1;

        VectorScoreProjection.Project("local", [
            (new VectorEntry("note", w0, [1f]), 0.2),
            (new VectorEntry("note", w1, [1f]), 0.9),
        ], weight: 100.0, into: scores);

        scores["local|note|1"].Should().BeApproximately(90.0, 1e-9);
    }

    [Fact]
    public void MemoryKey_TakesTheMaxAcrossEveryElement()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int e1 = 1 * MemoryVectorIndexer.ChunkIndexStride;
        int e2 = 2 * MemoryVectorIndexer.ChunkIndexStride;

        VectorScoreProjection.Project("local", [
            (new VectorEntry("note", e1, [1f]), 0.3),
            (new VectorEntry("note", e2, [0f]), 0.8),
        ], weight: 100.0, into: scores);

        scores["local|note"].Should().BeApproximately(80.0, 1e-9);
    }

    [Fact]
    public void NullChunkIndex_ContributesToTheMemoryKeyOnly()
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        VectorScoreProjection.Project(
            "local", [(new VectorEntry("note", null, [1f]), 0.4)], weight: 100.0, into: scores);

        scores.Should().ContainKey("local|note");
        scores.Keys.Should().NotContain(k => k.Count(c => c == '|') == 2,
            because: "vectors written before this branch carry a null chunk index and have no element");
    }
}
