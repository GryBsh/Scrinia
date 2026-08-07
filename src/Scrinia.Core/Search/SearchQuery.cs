using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Everything a store needs to answer one search. Replaced five <c>SearchAll</c> overloads whose
/// four default implementations post-filtered after the top-K cut — returning fewer than
/// <c>Limit</c> results whenever a filter was selective. A single member with no default body
/// means a store that cannot filter fails to compile rather than quietly under-filling.
/// </summary>
public sealed record SearchQuery(string Query)
{
    /// <summary>Optional comma-separated scope filter. Null means every readable scope.</summary>
    public string? Scopes { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Optional comma-separated topic names to exclude, e.g. "plan,task".</summary>
    public string? ExcludeTopics { get; init; }

    /// <summary>Case-insensitive prefix on the qualified label, e.g. "/findings/".</summary>
    public string? PathPrefix { get; init; }

    /// <summary>AND across keys, OR within each key's values, case-insensitive.</summary>
    public IReadOnlyDictionary<string, string[]>? AttributeFilter { get; init; }

    /// <summary>Plugin-supplied scores (e.g. embeddings), keyed "{scope}|{name}" for entries and
    /// "{scope}|{name}|{chunkIndex}" for chunks. Scored as a fusion source.</summary>
    public IReadOnlyDictionary<string, double>? SupplementalScores { get; init; }

    /// <summary>Entity-graph scores, scored as their own fusion source — never summed into
    /// <see cref="SupplementalScores"/>.</summary>
    public IReadOnlyDictionary<string, double>? EntityGraphScores { get; init; }

    /// <summary>
    /// Pre-materialized candidates. When set, the store skips its own <c>ListScoped</c> — the
    /// caller already paid for it.
    /// <para><b>Contract:</b> the caller must have materialized these with the same
    /// <see cref="Scopes"/> and <see cref="ExcludeTopics"/> carried on this query. Handing over a
    /// differently-scoped list silently changes the result set. <see cref="SearchPipeline"/> is
    /// the only production caller that sets this, and it builds both from the same values.</para>
    /// <para><b>Staleness:</b> the list is a snapshot. A caller that materializes candidates,
    /// awaits something slow (an embedding provider), then searches will not see a memory written
    /// during that await — where re-listing inside the store would have. That is an accepted
    /// trade: search results were never transactional with concurrent writes, and the window is
    /// the same one the contributor already scored against.</para>
    /// </summary>
    public IReadOnlyList<ScopedArtifact>? Candidates { get; init; }
}
