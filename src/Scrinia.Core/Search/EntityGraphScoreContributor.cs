using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Scores candidates by Personalized PageRank over an entity graph built — statelessly,
/// per query — from the candidates' own typed keywords (person/place/org/thing/topic).
/// Surfaces memories
/// associated to a query entity through a shared entity that lexical/semantic scoring
/// buries (query names A; the gold memory is about B, which co-occurs with A). Validated
/// as a within-pool precision lever in finding #8 of
/// <c>docs/research/benchmark-methodology-findings.md</c>.
///
/// <para>Unlike the embeddings/path-segment contributors (which sum into the shared
/// supplemental channel), this signal is consumed as its <i>own</i> fusion source in
/// <c>WeightedFieldScorer</c> via <see cref="EntityGraphScoreContext"/> — so its mass never
/// collapses into the embedding rank under RRF fusion.</para>
///
/// <para>Stateless and graceful: returns <c>null</c> when the query has no matching entity
/// seeds or no candidate carries typed keywords, so it is inert (zero effect) until agents
/// supply typed keywords. Cost is O(candidates × keywords) to build the graph
/// plus the PPR iterations — the same per-query order as the embeddings flat scan already on
/// the hot path.</para>
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

    public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates, IMemoryStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates is null || candidates.Count == 0)
            return None;

        // Build the bipartite graph from the candidates we were handed, keyed by the same
        // "{scope}|{name}" the scorer looks up supplemental scores with. Build assigns every
        // attribute (type, value) a uniform "type:value" node, so unrelated keys sharing a
        // value stay distinct; the full candidate set is passed so all associations are seen.
        var docs = new List<(string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)>(candidates.Count);
        foreach (var c in candidates)
            docs.Add(($"{c.Scope}|{c.Entry.Name}", c.Entry.Keywords, c.Entry.Attributes));
        var graph = EntityGraph.Build(docs);
        if (graph.DocCount == 0) return None;

        var ppr = graph.Score(query, _alpha, _iterations);
        if (ppr.Count == 0) return None;

        // Max-normalize (every graph-reached memory keeps a positive boost — min-max would
        // zero the weakest reached node, which is the one most in need of a lift) and scale.
        double max = 0;
        foreach (double v in ppr.Values)
            if (v > max) max = v;
        if (max <= 0) return None;

        var scores = new Dictionary<string, double>(ppr.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mass) in ppr)
            scores[key] = (mass / max) * _weight;

        return Task.FromResult<IReadOnlyDictionary<string, double>?>(scores);
    }

    private static Task<IReadOnlyDictionary<string, double>?> None =>
        Task.FromResult<IReadOnlyDictionary<string, double>?>(null);
}
