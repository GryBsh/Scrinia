using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

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

        using var ms = new MemoryStream();
        index.Save(ms);

        ms.Position = 0;
        var loaded = HnswIndex.Load(ms);
        loaded.Count.Should().Be(50);

        float[] query = RandomVector(16, rng);
        var origResults = index.Search(query, topK: 5);
        var loadedResults = loaded.Search(query, topK: 5);

        loadedResults.Should().HaveCount(origResults.Count);
        loadedResults[0].Key.Should().Be(origResults[0].Key);
    }

    [Fact]
    public void Insert_UpdatesExistingKey()
    {
        var index = new HnswIndex();
        index.Insert("a", [1f, 0f, 0f]);
        index.Insert("key", [1f, 0f, 0f]);
        index.Insert("key", [0f, 1f, 0f]);

        index.Count.Should().Be(2);

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

    [Fact]
    public async Task ConcurrentSearchesAndInserts_ProduceConsistentResults()
    {
        // Reader/writer lock should allow concurrent searches without blocking, while
        // serializing inserts. The test asserts both correctness (Count reflects every
        // insert exactly once) and absence of deadlock under load.
        using var index = new HnswIndex();
        var rng = new Random(1);

        // Seed with a base set so searches have a non-empty graph to traverse.
        const int seedCount = 100;
        for (int i = 0; i < seedCount; i++)
            index.Insert($"seed-{i}", RandomVector(32, rng));

        const int searchTaskCount = 8;
        const int insertTaskCount = 2;
        const int searchesPerTask = 200;
        const int insertsPerTask = 50;

        var insertedKeys = new System.Collections.Concurrent.ConcurrentBag<string>();
        var searchTasks = new Task[searchTaskCount];
        var insertTasks = new Task[insertTaskCount];

        for (int s = 0; s < searchTaskCount; s++)
        {
            int taskId = s;
            searchTasks[s] = Task.Run(() =>
            {
                var localRng = new Random(100 + taskId);
                for (int i = 0; i < searchesPerTask; i++)
                {
                    var query = RandomVector(32, localRng);
                    var hits = index.Search(query, topK: 5);
                    hits.Should().NotBeNull();
                }
            });
        }

        for (int w = 0; w < insertTaskCount; w++)
        {
            int taskId = w;
            insertTasks[w] = Task.Run(() =>
            {
                var localRng = new Random(500 + taskId);
                for (int i = 0; i < insertsPerTask; i++)
                {
                    string key = $"concurrent-{taskId}-{i}";
                    index.Insert(key, RandomVector(32, localRng));
                    insertedKeys.Add(key);
                }
            });
        }

        await Task.WhenAll(searchTasks.Concat(insertTasks));

        index.Count.Should().Be(seedCount + insertTaskCount * insertsPerTask,
            because: "every concurrent insert must be reflected in Count exactly once");
        insertedKeys.Should().HaveCount(insertTaskCount * insertsPerTask);
    }
}
