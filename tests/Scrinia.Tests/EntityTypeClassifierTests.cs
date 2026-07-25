using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

// Deterministic stub: returns a caller-provided vector per exact text; unknown text -> zero vector.
// internal (not private) so Task 6's tests in this same assembly can reuse it without redefining it.
internal sealed class DictEmbeddingProvider : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _map;
    private readonly int _dim;
    public DictEmbeddingProvider(Dictionary<string, float[]> map, int dim) { _map = map; _dim = dim; }
    public bool IsAvailable => true;
    public int Dimensions => _dim;
    public string Signature => "stub:dict";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<float[]?>(_map.TryGetValue(text, out var v) ? v : new float[_dim]);
    public void Dispose() { }
}

public class EntityTypeClassifierTests
{
    private static EntityTypeClassifier Make(Dictionary<string, float[]> map) =>
        new(new DictEmbeddingProvider(map, 3));

    [Fact]
    public async Task NearestExistingEntity_Wins()
    {
        // "seraphim" identical vector to an existing thing:seraphim -> typed thing.
        var map = new Dictionary<string, float[]>
        {
            ["seraphim"] = [0f, 0f, 1f],
            ["susie"] = [0f, 0f, 1f],
        };
        var c = Make(map);
        var t = await c.TypeAsync("seraphim", [("susie", "thing")], default);
        t.Should().Be("thing");
    }

    [Fact]
    public async Task CentroidFallback_WhenNoNeighbor()
    {
        // Give the classifier a place-pointing value and no existing typed entities.
        var map = new Dictionary<string, float[]> { ["france"] = [0f, 1f, 0f] };
        // The classifier embeds its own seed vocab; make the place seeds point at [0,1,0].
        foreach (var seed in EntityTypeClassifier.SeedVocab["place"]) map[seed] = [0f, 1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["person"]) map[seed] = [1f, 0f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["thing"]) map[seed] = [0f, 0f, 1f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["org"]) map[seed] = [1f, 1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["topic"]) map[seed] = [0f, 1f, 1f];

        var c = Make(map);
        (await c.TypeAsync("france", [], default)).Should().Be("place");
    }

    [Fact]
    public async Task BelowThreshold_ReturnsNull()
    {
        // Zero vector for the value -> no cosine anywhere -> stays mention.
        var c = Make(new Dictionary<string, float[]>());
        (await c.TypeAsync("weirdtoken", [], default)).Should().BeNull();
    }

    [Fact]
    public async Task CentroidNormalization_IsLoadBearing_ForSpreadSeeds()
    {
        // "place" seeds map to three DISTINCT unit vectors clustered around the x-axis (not the
        // identical-vector trick the other centroid test uses), so their RAW (pre-normalization)
        // mean has magnitude ~0.449 -- below CentroidThreshold (0.50) -- even though every seed
        // agrees in direction with the x-axis test value. Only L2-normalizing the centroid
        // (restoring magnitude 1, same direction) pushes the similarity to 1.0. Without that
        // normalization step this test flips: the classifier would return null instead of "place".
        float[] v1 = [1f, 0f, 0f];
        float[] v2 = [0.1736f, 0.9848f, 0f];   // +80 degrees off the x-axis
        float[] v3 = [0.1736f, -0.9848f, 0f];  // -80 degrees off the x-axis
        float[][] spread = [v1, v2, v3];

        var map = new Dictionary<string, float[]> { ["axis"] = [1f, 0f, 0f] };
        var placeSeeds = EntityTypeClassifier.SeedVocab["place"];
        for (int i = 0; i < placeSeeds.Length; i++) map[placeSeeds[i]] = spread[i % spread.Length];

        // Other four types point along orthogonal-ish axes so their similarity to "axis" stays
        // ~0 regardless of normalization -- only "place"'s centroid magnitude is under test.
        foreach (var seed in EntityTypeClassifier.SeedVocab["person"]) map[seed] = [0f, 1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["org"]) map[seed] = [0f, 0f, 1f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["thing"]) map[seed] = [0f, -1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["topic"]) map[seed] = [0f, 0f, -1f];

        var c = Make(map);
        (await c.TypeAsync("axis", [], default)).Should().Be("place");
    }
}
