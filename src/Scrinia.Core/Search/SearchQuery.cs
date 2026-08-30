using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Everything a store needs to answer one search. Replaced five <c>SearchAll</c> overloads whose
/// four default implementations post-filtered after the top-K cut — returning fewer than
/// <c>Limit</c> results whenever a filter was selective. A single member with no default body
/// means a store that cannot filter fails to compile rather than quietly under-filling.
/// </summary>
public sealed record SearchQuery(string Query) : MemorySearchRequest(Query)
{
    private readonly IReadOnlyList<string>? _scopes;
    private readonly IReadOnlyList<string>? _excludeTopics;

    /// <summary>Optional scope filter. Null or empty means every readable scope (Task 9, D-4: an
    /// empty list behaves identically to null). Normalized through
    /// <see cref="MemoryNaming.NormalizeFilterList"/> on construction (Task 9 fix round 1, F-1/F-2/
    /// F-3) — a null element or a blank entry can never reach a consumer, and no construction path
    /// can bypass the rule since it lives in the <c>init</c> accessor itself.</summary>
    public IReadOnlyList<string>? Scopes
    {
        get => _scopes;
        init => _scopes = MemoryNaming.NormalizeFilterList(value);
    }

    /// <summary>Maximum results to return.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Optional topic names to exclude, e.g. ["plan", "task"]. Null or empty excludes
    /// nothing (Task 9, D-4). Normalized the same way as <see cref="Scopes"/> and for the same
    /// reason (Task 9 fix round 1, F-1/F-2/F-3).</summary>
    public IReadOnlyList<string>? ExcludeTopics
    {
        get => _excludeTopics;
        init => _excludeTopics = MemoryNaming.NormalizeFilterList(value);
    }

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
    /// <para><b>Contract:</b> a hint about cost, not about the answer — in one direction. The
    /// store re-applies this query's own <see cref="Scopes"/> and <see cref="ExcludeTopics"/> to
    /// whatever is supplied, so a wider or differently-scoped list cannot add results the query's
    /// own filters would exclude; the surplus is filtered out, not returned. Filtering can only
    /// remove candidates, though — it cannot source ones that were never supplied — so a list
    /// <i>narrower</i> than this query's scopes still under-returns, the same as calling
    /// <c>ListScoped</c> with that narrower filter would. <see cref="SearchPipeline"/> is the only
    /// production caller that sets this, and it builds both from the same values, so this
    /// asymmetry never surfaces there.</para>
    /// <para><b>Staleness:</b> the list is a snapshot. A caller that materializes candidates,
    /// awaits something slow (an embedding provider), then searches will not see a memory written
    /// during that await — where re-listing inside the store would have. That is an accepted
    /// trade: search results were never transactional with concurrent writes, and the window is
    /// the same one the contributor already scored against.</para>
    /// </summary>
    public IReadOnlyList<ScopedArtifact>? Candidates { get; init; }
}
