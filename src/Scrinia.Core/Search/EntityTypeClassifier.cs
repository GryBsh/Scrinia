using System.Collections.Concurrent;
using Scrinia.Core.Embeddings;

namespace Scrinia.Core.Search;

/// <summary>
/// Hybrid entity typing for prose spans (Piece 2, Stage 2): nearest existing typed entity, then
/// prototype centroid, then null (stay <c>mention:</c>). Static bag-of-words embeddings type only
/// roughly, so thresholds are deliberately conservative — mention: is the expected common case.
/// Seeds are tunable on real-workflow signal, never a benchmark fit.
/// </summary>
public sealed class EntityTypeClassifier(IEmbeddingProvider provider)
{
    // Conservative floors; calibrate on real Model2Vec vectors later.
    private const double LinkThreshold = 0.60;
    private const double CentroidThreshold = 0.50;
    private const double Margin = 0.05;

    /// <summary>Per-type prototype seed vocabulary. Keys are <see cref="KnownAttributes.EntityKeys"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SeedVocab =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["person"] = ["person", "people", "man", "woman", "name", "he", "she"],
            ["place"]  = ["place", "city", "country", "location", "region", "town"],
            ["org"]    = ["organization", "company", "corporation", "team", "agency", "institution"],
            ["thing"]  = ["thing", "object", "item", "device", "product", "tool"],
            ["topic"]  = ["topic", "subject", "concept", "theme", "idea", "matter"],
        };

    private readonly ConcurrentDictionary<string, float[]?> _vectorCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, float[]>? _centroids;

    public async Task<string?> TypeAsync(
        string value, IReadOnlyList<(string Value, string Type)> existingTyped, CancellationToken ct)
    {
        float[]? vec = await EmbedCachedAsync(value, ct);
        if (vec is null || IsZero(vec)) return null;

        // (1) nearest existing typed entity
        string? bestType = null;
        double bestSim = double.NegativeInfinity;
        foreach (var (ev, et) in existingTyped)
        {
            float[]? evVec = await EmbedCachedAsync(ev, ct);
            if (evVec is null || IsZero(evVec)) continue;
            double sim = VectorIndex.CosineSimilarity(vec, evVec);
            if (sim > bestSim) { bestSim = sim; bestType = et; }
        }
        if (bestType is not null && bestSim >= LinkThreshold) return bestType;

        // (2) prototype centroid
        var centroids = await GetCentroidsAsync(ct);
        string? topType = null, secondType = null;
        double top = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var (type, cen) in centroids)
        {
            double sim = VectorIndex.CosineSimilarity(vec, cen);
            if (sim > top) { second = top; secondType = topType; top = sim; topType = type; }
            else if (sim > second) { second = sim; secondType = type; }
        }
        if (topType is not null && top >= CentroidThreshold && (top - second) >= Margin)
            return topType;

        // (3) stay mention:
        return null;
    }

    private async Task<IReadOnlyDictionary<string, float[]>> GetCentroidsAsync(CancellationToken ct)
    {
        if (_centroids is not null) return _centroids;
        var built = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, seeds) in SeedVocab)
        {
            var vecs = new List<float[]>();
            foreach (var s in seeds)
            {
                var v = await EmbedCachedAsync(s, ct);
                if (v is not null && !IsZero(v)) vecs.Add(v);
            }
            if (vecs.Count > 0) built[type] = Mean(vecs);
        }
        _centroids = built;
        return built;
    }

    // Vectors are L2-normalized on cache so VectorIndex.CosineSimilarity (dot product over
    // normalized vectors) can be reused as-is (DRY with HybridReranker/HnswIndex).
    private async Task<float[]?> EmbedCachedAsync(string text, CancellationToken ct)
    {
        if (_vectorCache.TryGetValue(text, out var cached)) return cached;
        var v = await provider.EmbedAsync(text, ct);
        if (v is not null) VectorMath.L2Normalize(v);
        _vectorCache[text] = v;
        return v;
    }

    private static float[] Mean(List<float[]> vecs)
    {
        int dim = vecs[0].Length;
        var acc = new float[dim];
        foreach (var v in vecs) for (int i = 0; i < dim; i++) acc[i] += v[i];
        for (int i = 0; i < dim; i++) acc[i] /= vecs.Count;
        VectorMath.L2Normalize(acc);
        return acc;
    }

    private static bool IsZero(float[] v)
    {
        foreach (var x in v) if (x != 0f) return false;
        return true;
    }
}
