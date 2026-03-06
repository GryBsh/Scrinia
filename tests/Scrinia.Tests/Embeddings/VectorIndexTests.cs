using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Tests.Embeddings;

public class VectorIndexTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_Returns1()
    {
        float[] a = [0.6f, 0.8f];
        float norm = MathF.Sqrt(a[0] * a[0] + a[1] * a[1]);
        a[0] /= norm; a[1] /= norm;

        float sim = VectorIndex.CosineSimilarity(a, a);
        sim.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_Returns0()
    {
        float[] a = [1f, 0f];
        float[] b = [0f, 1f];

        float sim = VectorIndex.CosineSimilarity(a, b);
        sim.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegative1()
    {
        float[] a = [1f, 0f];
        float[] b = [-1f, 0f];

        float sim = VectorIndex.CosineSimilarity(a, b);
        sim.Should().BeApproximately(-1.0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_HighDimensional_UsesSimd()
    {
        var rng = new Random(42);
        float[] a = new float[384];
        float[] b = new float[384];
        for (int i = 0; i < 384; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2 - 1);
            b[i] = (float)(rng.NextDouble() * 2 - 1);
        }
        L2Normalize(a);
        L2Normalize(b);

        float sim = VectorIndex.CosineSimilarity(a, b);
        sim.Should().BeInRange(-1.0f, 1.0f);
    }

    [Fact]
    public void CosineSimilarity_MismatchedDimensions_Throws()
    {
        float[] a = [1f, 0f];
        float[] b = [1f, 0f, 0f];

        Action act = () => VectorIndex.CosineSimilarity(a, b);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Search_ReturnsTopK_SortedByDescSimilarity()
    {
        float[] query = [1f, 0f, 0f];

        var entries = new List<VectorEntry>
        {
            new("far", null, [0f, 0f, 1f]),
            new("close", null, [0.9f, 0.1f, 0f]),
            new("medium", null, [0.5f, 0.5f, 0f]),
        };
        foreach (var e in entries) L2Normalize(e.Vector);

        var results = VectorIndex.Search(query, entries, 2);

        results.Should().HaveCount(2);
        results[0].Entry.Name.Should().Be("close");
        results[1].Entry.Name.Should().Be("medium");
        results[0].Similarity.Should().BeGreaterThan(results[1].Similarity);
    }

    [Fact]
    public void Search_EmptyEntries_ReturnsEmpty()
    {
        float[] query = [1f, 0f];
        var results = VectorIndex.Search(query, [], 10);
        results.Should().BeEmpty();
    }

    private static void L2Normalize(float[] v)
    {
        float norm = 0;
        foreach (float f in v) norm += f * f;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }
}
