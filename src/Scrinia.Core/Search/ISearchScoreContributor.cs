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
/// In the server, <see cref="Current"/> is set per-request (AsyncLocal).
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
}
