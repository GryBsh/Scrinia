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
///
/// <para><b>The <c>elementIndex == 0</c> guard, and why it is safe.</b> A stored chunk index
/// below <see cref="MemoryVectorIndexer.ChunkIndexStride"/> decodes to element 0, which does not
/// exist — 1-based elements start at 1. That shape only arises from a vector written by
/// pre-branch code, which stamped a raw window ordinal (0-based, never multiplied by the
/// stride) or a 1-based content-element index too small to reach the stride on its own. Once
/// this branch's signature bump lands (<c>ChunkedSignature.LayoutRevision</c>), every such
/// vector's file fails <see cref="VectorStore"/>'s signature check on load and is quarantined
/// wholesale — not partially read — so in the shipped state no legacy-shaped chunk index ever
/// reaches this method; it is rebuilt through <see cref="MemoryVectorIndexer"/> first. The guard
/// therefore only matters for the window between this task landing and the signature bump
/// landing, where a store built with the old signature could still hold vectors in the old
/// shape. Below one stride, such a vector's similarity still counts toward the memory key — it
/// is real data — it just cannot be legitimately relabeled with the wrong element.</para>
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
