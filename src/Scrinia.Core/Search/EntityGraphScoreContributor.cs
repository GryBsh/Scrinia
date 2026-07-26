using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Scores candidates by Personalized PageRank over an <see cref="EntityFactStore"/>'s entity-to-doc
/// adjacency. The caller selects the store: cached cross-scope when the search is fully unfiltered,
/// else a scoped build from the query's candidates. Surfaces memories associated to a query entity
/// through a shared entity that lexical/semantic scoring buries (query names A; the gold memory is
/// about B, which co-occurs with A). Validated as a within-pool precision lever in finding #8 of
/// <c>docs/research/benchmark-methodology-findings.md</c>.
///
/// <para>Unlike the embeddings/path-segment contributors (which sum into the shared
/// supplemental channel), this signal is consumed as its <i>own</i> fusion source in
/// <c>WeightedAttributeScorer</c> via <see cref="EntityGraphScoreContext"/> — so its mass never
/// collapses into the embedding rank under RRF fusion.</para>
///
/// <para>Stateless and graceful: returns <c>null</c> when the query has no matching entity
/// seeds or no reachable documents, so it is inert (zero effect) until agents supply entity facts.
/// Per-query cost is the PPR iterations over the adjacency — the same order as the embeddings flat
/// scan already on the hot path.</para>
/// </summary>
public sealed class EntityGraphScoreContributor : ISearchScoreContributor
{
    /// <summary>Scale applied to the max-normalized PPR mass, matching the embedding
    /// supplemental scale (cosine × 50) so the entity source is comparable in magnitude.</summary>
    public const double DefaultWeight = 50.0;

    /// <summary>PPR teleport (restart) probability. Validated bench value.</summary>
    public const double DefaultAlpha = 0.15;

    /// <summary>PPR power-iteration count. Validated bench value.</summary>
    public const int DefaultIterations = 20;

    private readonly double _weight;
    private readonly double _alpha;
    private readonly int _iterations;

    public EntityGraphScoreContributor(
        double weight = DefaultWeight, double alpha = DefaultAlpha, int iterations = DefaultIterations)
    {
        _weight = weight;
        _alpha = alpha;
        _iterations = iterations;
    }

    /// <summary>Retained for <see cref="ISearchScoreContributor"/> conformance (benchmarks/tests); production call sites invoke <see cref="Score"/> directly.</summary>
    public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates, IMemoryStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates is null || candidates.Count == 0)
            return None;
        return Task.FromResult(Score(EntityFactDerivation.BuildStore(candidates), query));
    }

    /// <summary>Runs the entity-graph PPR signal over a ready <see cref="EntityFactStore"/> — the caller
    /// selects the store (cached cross-scope when fully unfiltered, else a scoped build over the query's
    /// candidates). Max-normalizes the PPR mass and scales by the weight; keyed by "{scope}|{name}".
    /// Returns null when there are no seeds, no reachable docs, or zero mass (the None short-circuits).</summary>
    public IReadOnlyDictionary<string, double>? Score(EntityFactStore factStore, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var (entityToDocs, docToEntities) = factStore.ScoringAdjacency();
        if (entityToDocs.Count == 0) return null;

        var seeds = EntityQuerySeeder.Seed(query, entityToDocs.Keys);
        if (seeds.Count == 0) return null;

        var ppr = PersonalizedPageRank.Score(entityToDocs, docToEntities, seeds, _alpha, _iterations);
        if (ppr.Count == 0) return null;

        double max = 0;
        foreach (double v in ppr.Values)
            if (v > max) max = v;
        if (max <= 0) return null;

        var scores = new Dictionary<string, double>(ppr.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mass) in ppr)
            scores[key] = (mass / max) * _weight;
        return scores;
    }

    private static Task<IReadOnlyDictionary<string, double>?> None =>
        Task.FromResult<IReadOnlyDictionary<string, double>?>(null);
}
