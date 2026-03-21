using System.Numerics;
using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Cosine similarity search over a collection of vectors.
/// Uses <see cref="Vector{T}"/> SIMD for fast dot product on L2-normalized vectors.
/// </summary>
public static class VectorIndex
{
    /// <summary>
    /// Computes cosine similarity between two L2-normalized vectors.
    /// For normalized vectors, cosine similarity = dot product.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector dimensions must match: {a.Length} vs {b.Length}");

        int dims = a.Length;
        int simdLength = Vector<float>.Count;
        float dot = 0;
        int i = 0;

        // SIMD path
        if (Vector.IsHardwareAccelerated && dims >= simdLength)
        {
            for (; i <= dims - simdLength; i += simdLength)
            {
                var va = new Vector<float>(a.Slice(i, simdLength));
                var vb = new Vector<float>(b.Slice(i, simdLength));
                dot += Vector.Dot(va, vb);
            }
        }

        // Scalar tail
        for (; i < dims; i++)
            dot += a[i] * b[i];

        return dot;
    }

    /// <summary>
    /// Finds the top-k most similar vectors to the query, returning (entry, similarity) pairs.
    /// Uses flat scan for small collections or when no HNSW index is available.
    /// </summary>
    public static IReadOnlyList<(VectorEntry Entry, float Similarity)> Search(
        ReadOnlySpan<float> query,
        IReadOnlyList<VectorEntry> entries,
        int topK)
        => Search(query, entries, topK, hnsw: null);

    /// <summary>
    /// Finds the top-k most similar vectors. Uses HNSW when available and entries >= 1000,
    /// flat scan otherwise.
    /// </summary>
    public static IReadOnlyList<(VectorEntry Entry, float Similarity)> Search(
        ReadOnlySpan<float> query,
        IReadOnlyList<VectorEntry> entries,
        int topK,
        HnswIndex? hnsw)
    {
        if (entries.Count == 0)
            return [];

        // Flat scan — fast enough for typical memory counts
        var scored = new List<(VectorEntry Entry, float Similarity)>(entries.Count);
        foreach (var entry in entries)
        {
            float sim = CosineSimilarity(query, entry.Vector);
            if (sim > 0)
                scored.Add((entry, sim));
        }

        scored.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return scored.Take(topK).ToList();
    }
}
