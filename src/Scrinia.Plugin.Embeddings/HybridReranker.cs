using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Hybrid re-ranking search contributor: embeds only the BM25 top-K candidates
/// rather than the entire corpus, making semantic search efficient for large stores.
///
/// Flow:
/// 1. WeightedFieldScorer produces BM25+field scored candidates
/// 2. This contributor receives those candidates and embeds only them
/// 3. Cosine similarity scores are returned as supplemental scores
///
/// This is more efficient than embedding the entire corpus on every search,
/// since only the top candidates (typically 20–50) need embedding.
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
            var vectors = _store.GetVectors(group.Key);
            if (vectors.Count == 0) continue;

            // Build a name set of candidate entries for fast lookup
            var candidateNames = new HashSet<string>(
                group.Select(c => c.Entry.Name), StringComparer.OrdinalIgnoreCase);

            // Filter vectors to only those matching candidates
            var candidateVectors = vectors
                .Where(v => candidateNames.Contains(v.Name))
                .ToList();

            if (candidateVectors.Count == 0) continue;

            var topK = VectorIndex.Search(queryVec, candidateVectors, candidateVectors.Count);
            foreach (var (entry, similarity) in topK)
            {
                string key = entry.ChunkIndex is not null
                    ? $"{group.Key}|{entry.Name}|{entry.ChunkIndex}"
                    : $"{group.Key}|{entry.Name}";

                scores[key] = similarity * _weight;
            }
        }

        return scores.Count > 0 ? scores : null;
    }
}
