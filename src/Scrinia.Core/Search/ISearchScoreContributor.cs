using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Plugins implement this to contribute supplemental search scores (e.g. semantic similarity).
/// Scores are keyed by "{scope}|{name}" for entries or "{scope}|{name}|{chunkIndex}" for chunks.
/// </summary>
public interface ISearchScoreContributor
{
    Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct);
}

/// <summary>
/// AsyncLocal context for making <see cref="ISearchScoreContributor"/> available
/// to both MCP tools and REST endpoints without passing it through every method.
/// <para>
/// <see cref="Current"/> is an AsyncLocal, so a host that serves concurrent callers sets it per
/// call rather than once per process.
/// In the CLI, AsyncLocal doesn't propagate through the generic host to MCP tool handlers,
/// so <see cref="Default"/> provides a process-wide fallback.
/// </para>
/// </summary>
public static class SearchContributorContext
{
    private static readonly AsyncLocal<ISearchScoreContributor?> _current = new();
    private static ISearchScoreContributor? _default;

    /// <summary>Gets/sets the search contributor for the current async context, falling back to <see cref="Default"/>.</summary>
    public static ISearchScoreContributor? Current { get => _current.Value ?? _default; set => _current.Value = value; }

    /// <summary>Process-wide default used when no AsyncLocal value is set (CLI single-session mode).</summary>
    public static ISearchScoreContributor? Default { get => _default; set => _default = value; }

    internal static ISearchScoreContributor? RawCurrent
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>
/// Parallel to <see cref="SearchContributorContext"/>, but for the entity-graph signal,
/// which is scored as its <i>own</i> source rather than summed into the supplemental
/// channel (see <see cref="EntityGraphScoreContributor"/>). Kept separate so the entity
/// scores reach <c>WeightedAttributeScorer</c> as a distinct fusion source instead of being
/// merged with embeddings/path-segment.
/// <para>Same Current/Default contract and rationale as
/// <see cref="SearchContributorContext"/>: a multi-caller host sets <see cref="Current"/> per
/// request; the CLI sets <see cref="Default"/> because AsyncLocal doesn't propagate
/// through the generic host to MCP tool handlers.</para>
/// </summary>
public static class EntityGraphScoreContext
{
    private static readonly AsyncLocal<EntityGraphScoreContributor?> _current = new();
    private static EntityGraphScoreContributor? _default;

    /// <summary>Gets/sets the entity-graph contributor for the current async context, falling back to <see cref="Default"/>.</summary>
    public static EntityGraphScoreContributor? Current { get => _current.Value ?? _default; set => _current.Value = value; }

    /// <summary>Process-wide default used when no AsyncLocal value is set (CLI single-session mode).</summary>
    public static EntityGraphScoreContributor? Default { get => _default; set => _default = value; }
}
