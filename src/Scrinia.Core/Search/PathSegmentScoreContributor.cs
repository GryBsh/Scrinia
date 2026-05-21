using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Supplemental search-score contributor that boosts memories whose qualified path
/// shares tokens with the query. A memory at <c>/findings/auth-refactor</c> gets a
/// lift on a query like "auth" even if the body never says the word — the path was
/// where the agent chose to file it, which is a signal in its own right.
///
/// <para>Tokenization reuses <see cref="TextAnalysis.Tokenize"/> for parity with BM25:
/// alphanumeric splits, lowercase, stop-word filter, &gt;=2 chars. The score for a
/// candidate is <c>weight × |queryTokens ∩ pathTokens|</c>, so a perfect 3-segment
/// match earns 3× weight. Empty intersection contributes nothing — additive, never
/// penalizes.</para>
///
/// <para>Compose with the embeddings contributor via
/// <see cref="CompositeSearchScoreContributor"/>; this class is intentionally
/// dependency-free so it can run in BM25-only deployments where no embedding
/// model is loaded.</para>
/// </summary>
public sealed class PathSegmentScoreContributor : ISearchScoreContributor
{
    public const double DefaultWeight = 8.0;

    private readonly double _weight;

    /// <param name="weight">Multiplier on the path-token intersection count. Default
    /// matches the order of magnitude of Description's field-score (10) and sits below
    /// curated keyword hits (12–40); tune via bench sweeps. A weight of 0 disables
    /// scoring entirely without removing the contributor from the pipeline.</param>
    public PathSegmentScoreContributor(double weight = DefaultWeight)
    {
        _weight = weight;
    }

    public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (_weight <= 0 || string.IsNullOrWhiteSpace(query) || candidates.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, double>?>(null);

        var queryTokens = new HashSet<string>(TextAnalysis.Tokenize(query), StringComparer.OrdinalIgnoreCase);
        if (queryTokens.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, double>?>(null);

        var scores = new Dictionary<string, double>(candidates.Count);
        foreach (var candidate in candidates)
        {
            int intersect = CountPathTokenIntersection(candidate, queryTokens);
            if (intersect > 0)
                scores[$"{candidate.Scope}|{candidate.Entry.Name}"] = _weight * intersect;
        }

        return Task.FromResult<IReadOnlyDictionary<string, double>?>(scores.Count > 0 ? scores : null);
    }

    private static int CountPathTokenIntersection(ScopedArtifact candidate, HashSet<string> queryTokens)
    {
        // Strip the "local-topic:" infrastructure prefix so its tokens ("local", "topic")
        // don't pollute every candidate's path bag. After stripping, scope is the
        // raw topic path (e.g. "findings/auth-refactor"). Name is the leaf subject.
        string scope = candidate.Scope;
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            scope = scope["local-topic:".Length..];

        int intersect = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in TextAnalysis.Tokenize(scope))
        {
            if (seen.Add(token) && queryTokens.Contains(token)) intersect++;
        }
        foreach (var token in TextAnalysis.Tokenize(candidate.Entry.Name))
        {
            if (seen.Add(token) && queryTokens.Contains(token)) intersect++;
        }
        return intersect;
    }
}
