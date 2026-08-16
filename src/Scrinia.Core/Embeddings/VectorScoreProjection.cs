using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single read-side projection from vector hits to supplemental score keys.
///
/// <para><see cref="Search.WeightedAttributeScorer"/> probes exactly two key shapes
/// (<c>GetSupplemental</c>): <c>"{scope}|{name}"</c> for its whole-memory pass and
/// <c>"{scope}|{name}|{chunkIndex}"</c> for its chunk pass, where <c>chunkIndex</c> is
/// <c>ChunkEntry.ChunkIndex</c> — 1-based over content elements. Window ordinals never appear
/// in a key; <see cref="MemoryVectorIndexer.ElementIndexOf"/> maps a stored chunk index back to
/// its element, and both keys carry the maximum over the vectors that roll up into them
/// ("the best-matching window's score wins").</para>
///
/// <para>Replaces four hand-copied loops (finding H3), one of which — the plugin's MCP search
/// tool — did not aggregate at all, so a low-scoring chunk hit could overwrite a high-scoring
/// memory-level score rather than lose to it.</para>
/// </summary>
public static class VectorScoreProjection
{
    public static void Project(
        string scope,
        IEnumerable<(VectorEntry Entry, double Similarity)> hits,
        double weight,
        IDictionary<string, double> into)
    {
        foreach (var (entry, similarity) in hits)
        {
            double score = similarity * weight;

            string memoryKey = $"{scope}|{entry.Name}";
            if (!into.TryGetValue(memoryKey, out double bestForMemory) || score > bestForMemory)
                into[memoryKey] = score;

            if (entry.ChunkIndex is not { } stored) continue;

            // Vectors written before this branch carry a raw ordinal (or null). Anything below
            // one stride has no element, so it contributes to the memory key only.
            int elementIndex = MemoryVectorIndexer.ElementIndexOf(stored);
            if (elementIndex == 0) continue;

            string elementKey = $"{scope}|{entry.Name}|{elementIndex}";
            if (!into.TryGetValue(elementKey, out double bestForElement) || score > bestForElement)
                into[elementKey] = score;
        }
    }
}
