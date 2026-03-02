using FluentAssertions;
using Scrinia.Plugin.Embeddings;

namespace Scrinia.Plugin.Embeddings.Tests;

public class HnswIndexTests
{
    private static float[] RandomVector(int dims, Random rng)
    {
        float[] v = new float[dims];
        for (int i = 0; i < dims; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        L2Normalize(v);
        return v;
    }

    private static void L2Normalize(float[] v)
    {
        float norm = 0;
        foreach (float f in v) norm += f * f;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    [Fact]
    public void Insert_And_Search_FindsClosest()
    {
        var index = new HnswIndex();
        float[] target = [1f, 0f, 0f];
        float[] similar = [0.9f, 0.1f, 0f]; L2Normalize(similar);
        float[] distant = [0f, 0f, 1f];

        index.Insert("target", target);
        index.Insert("similar", similar);
        index.Insert("distant", distant);

        var results = index.Search([1f, 0f, 0f], topK: 2);
        results.Should().HaveCount(2);
        results[0].Key.Should().Be("target");
        results[0].Similarity.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void Remove_ExcludesFromSearch()
    {
        var index = new HnswIndex();
        index.Insert("a", [1f, 0f, 0f]);
        index.Insert("b", [0.9f, 0.1f, 0f]);

        index.Remove("a");

        var results = index.Search([1f, 0f, 0f], topK: 5);
        results.Should().NotContain(r => r.Key == "a");
    }

    [Fact]
    public void Count_ReflectsInsertAndRemove()
    {
        var index = new HnswIndex();
        index.Insert("a", [1f, 0f]);
        index.Insert("b", [0f, 1f]);
        index.Count.Should().Be(2);

        index.Remove("a");
        index.Count.Should().Be(1);
    }

    [Fact]
    public void Serialization_RoundTrips()
    {
        var index = new HnswIndex();
        var rng = new Random(42);

        for (int i = 0; i < 50; i++)
            index.Insert($"vec-{i}", RandomVector(16, rng));

        // Serialize
        using var ms = new MemoryStream();
        index.Save(ms);

        // Deserialize
        ms.Position = 0;
        var loaded = HnswIndex.Load(ms);
        loaded.Count.Should().Be(50);

        // Search should work on loaded index
        float[] query = RandomVector(16, rng);
        var origResults = index.Search(query, topK: 5);
        var loadedResults = loaded.Search(query, topK: 5);

        loadedResults.Should().HaveCount(origResults.Count);
        // Same top result
        loadedResults[0].Key.Should().Be(origResults[0].Key);
    }

    [Fact]
    public void Insert_UpdatesExistingKey()
    {
        var index = new HnswIndex();
        index.Insert("a", [1f, 0f, 0f]); // decoy
        index.Insert("key", [1f, 0f, 0f]);
        index.Insert("key", [0f, 1f, 0f]); // update (old node marked deleted)

        // Count should reflect only live nodes
        index.Count.Should().Be(2); // "a" and updated "key"

        var results = index.Search([0f, 1f, 0f], topK: 5);
        results.Select(r => r.Key).Should().OnlyHaveUniqueItems();
        results.Should().Contain(r => r.Key == "key");
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var index = new HnswIndex();
        var results = index.Search([1f, 0f], topK: 5);
        results.Should().BeEmpty();
    }
}
