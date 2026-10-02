using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Hybrid re-ranking search contributor: scores only the vectors belonging to the BM25 +
/// attribute candidate set, rather than the whole corpus, making semantic search efficient for
/// large stores.
///
/// Flow:
/// 1. WeightedAttributeScorer produces BM25+attribute scored candidates
/// 2. This contributor filters vectors down to that candidate set (<c>candidateVectors</c>)
/// 3. Every surviving vector is then scored — <see cref="VectorIndex.Search"/> is called with
///    the full candidate count rather than a smaller K, because the candidate filter is what
///    already bounds the work
/// 4. Cosine similarity scores are returned as supplemental scores
///
/// This is more efficient than embedding the entire corpus on every search,
/// since only the top candidates (typically 20-50) need embedding.
/// </summary>
public sealed class HybridReranker : ISearchScoreContributor
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _store;
    private readonly double _weight;

    public HybridReranker(IEmbeddingProvider provider, VectorStore store, double weight = 50.0)
    {
        _provider = provider;
        _store = store;
        _weight = weight;
    }

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (!_provider.IsAvailable || candidates.Count == 0)
            return null;

        var queryVec = await _provider.EmbedAsync(query, ct);
        if (queryVec is null)
            return null;

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Group candidates by scope for efficient vector lookup
        var byScope = candidates.GroupBy(c => c.Scope, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byScope)
        {
            var candidateNames = new HashSet<string>(
                group.Select(c => c.Entry.Name), StringComparer.OrdinalIgnoreCase);
            var candidateVectors = _store.GetCandidateVectors(group.Key, candidateNames);

            if (candidateVectors.Count == 0) continue;

            var topK = VectorIndex.Search(queryVec, candidateVectors, candidateVectors.Count);
            VectorScoreProjection.Project(
                group.Key, topK.Select(t => (t.Entry, (double)t.Similarity)), _weight, scores);
        }

        return scores.Count > 0 ? scores : null;
    }
}
