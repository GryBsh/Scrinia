using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Combines multiple <see cref="ISearchScoreContributor"/> instances into one, summing
/// per-key scores. Lets us stack independent signals — e.g. embeddings semantic similarity
/// plus path-segment overlap — without changing <see cref="SearchContributorContext"/>'s
/// single-valued contract or pushing per-key score merging into <c>WeightedFieldScorer</c>.
///
/// <para>Sum (not max) matches the additive semantics already baked into the scorer:
/// each contributor is an independent signal whose contribution adds to the final
/// relevance term.</para>
/// </summary>
public sealed class CompositeSearchScoreContributor : ISearchScoreContributor
{
    private readonly ISearchScoreContributor[] _inner;

    public CompositeSearchScoreContributor(params ISearchScoreContributor[] inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (_inner.Length == 0) return null;
        if (_inner.Length == 1)
            return await _inner[0].ComputeScoresAsync(query, candidates, store, ct).ConfigureAwait(false);

        var merged = new Dictionary<string, double>();
        foreach (var contributor in _inner)
        {
            var scores = await contributor.ComputeScoresAsync(query, candidates, store, ct).ConfigureAwait(false);
            if (scores is null || scores.Count == 0) continue;
            foreach (var kvp in scores)
            {
                merged.TryGetValue(kvp.Key, out double existing);
                merged[kvp.Key] = existing + kvp.Value;
            }
        }
        return merged.Count > 0 ? merged : null;
    }
}
