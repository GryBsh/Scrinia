using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Search orchestration owner. Extracted from <see cref="FileMemoryStore"/> so the store forwards
/// <c>SearchAll</c> rather than hosting the candidate-listing → path/attribute-filtering → topic
/// building → scoring pipeline. The scorer (<see cref="WeightedAttributeScorer"/>) and its static
/// helpers (<see cref="SearchResultFilter"/>, <see cref="RankerOptions"/>, <see cref="SearchQuery"/>)
/// stay put; this type only composes them.
/// </summary>
internal sealed class SearchService
{
    private readonly ListingService _listing;
    private readonly Func<string, string, string> _formatQualifiedLabel;
    private readonly Func<RankerOptions> _rankerOptions;

    internal SearchService(
        ListingService listing,
        Func<string, string, string> formatQualifiedLabel,
        Func<RankerOptions> rankerOptions)
    {
        _listing = listing;
        _formatQualifiedLabel = formatQualifiedLabel;
        _rankerOptions = rankerOptions;
    }

    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return [];

        string? pathPrefix = query.Address?.ToString() ?? query.PathPrefix;
        bool hasPath = !string.IsNullOrWhiteSpace(pathPrefix);
        bool hasAttr = query.AttributeFilter is { Count: > 0 };

        // Candidates set means the caller (SearchPipeline) already materialized this slice for
        // the contributors — skip the second ListScoped. It is a hint about COST, never about
        // the ANSWER: re-apply this query's own scope and excludeTopics filter so a caller who
        // supplies a wider or differently-scoped list gets exactly what it would have got with
        // no Candidates at all. (A list narrower than the query's scopes is not corrected here —
        // it still under-returns.) One HashSet build plus a Contains per candidate — cheap, and
        // there is no re-listing to compare it against in this branch.
        IReadOnlyList<ScopedArtifact> allCandidates =
            query.Candidates is { } supplied
                ? _listing.FilterToQueryScopes(supplied, query.Scopes, query.ExcludeTopics)
                : _listing.ListScoped(query.Scopes, query.ExcludeTopics);

        IReadOnlyList<ScopedArtifact> entryCandidates = hasPath || hasAttr
            ? allCandidates
                .Where(c => SearchResultFilter.CandidateMatches(c, pathPrefix, query.AttributeFilter, _formatQualifiedLabel))
                .ToList()
            : allCandidates;

        // Topics carry no attributes: an active attribute filter drops them all. Otherwise filter
        // topics by their own scope label, derived from the UNFILTERED candidate set (a topic can
        // match even when its entries are filtered out by a longer path prefix, and vice versa).
        List<TopicInfo> topics;
        if (hasAttr)
            topics = [];
        else if (hasPath)
            topics = ListingService.BuildTopicInfosFromCandidates(allCandidates)
                .Where(t => SearchResultFilter.TopicMatches(t, pathPrefix))
                .ToList();
        else
            topics = ListingService.BuildTopicInfosFromCandidates(allCandidates);

        var searcher = new WeightedAttributeScorer(_rankerOptions());
        return searcher.SearchAll(query.Query, entryCandidates, topics, query.Limit,
            query.SupplementalScores, query.EntityGraphScores);
    }
}
