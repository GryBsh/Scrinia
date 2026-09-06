using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Compatibility shim for the retired server-side duplicate. The whole ranking
/// pipeline lives in <see cref="SearchPipeline"/>; this type exists only so
/// callers that previously reached <c>PluginPipeline.SearchAsync</c> delegate
/// to the single materialization path without duplicating scorer construction
/// or filter handling. No logic lives here — every overload forwards to
/// <see cref="SearchPipeline.ExecuteAsync"/>.
/// </summary>
public sealed class PluginPipeline
{
    private readonly ISearchScoreContributor? _contributor;
    private readonly EntityGraphScoreContributor? _graph;

    public PluginPipeline(ISearchScoreContributor? contributor = null, EntityGraphScoreContributor? graph = null)
    {
        _contributor = contributor;
        _graph = graph;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, IMemoryStore store, CancellationToken ct = default)
        => SearchAsync(query, store, _contributor, _graph, ct);

    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchQuery query,
        IMemoryStore store,
        ISearchScoreContributor? contributor,
        EntityGraphScoreContributor? graph,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(store);
        var outcome = await SearchPipeline.ExecuteAsync(store, query, contributor, graph, ct: ct).ConfigureAwait(false);
        return outcome.Matches;
    }

    public Task<SearchOutcome> SearchWithFactsAsync(SearchQuery query, IMemoryStore store, CancellationToken ct = default)
        => SearchPipeline.ExecuteAsync(store, query, _contributor, _graph, surfaceEntityFacts: false, ct: ct);
}
