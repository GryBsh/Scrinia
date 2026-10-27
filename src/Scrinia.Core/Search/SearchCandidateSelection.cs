using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Selects the lexical top-K from query-eligible candidates using the shared ranking rules.</summary>
internal static class SearchCandidateSelection
{
    internal static IReadOnlyList<ScopedArtifact> Select(
        IEnumerable<ScopedArtifact> source, SearchQuery query, int limit,
        Func<string, string, string> formatQualifiedLabel)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var queryTokens = TextAnalysis.Tokenize(query.Query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var worstFirst = Comparer<SearchCandidateOptions.RankedCandidate>.Create(
            (left, right) => SearchCandidateOptions.LexicalOrder.Compare(right, left));
        var selected = new PriorityQueue<SearchCandidateOptions.RankedCandidate, SearchCandidateOptions.RankedCandidate>(limit, worstFirst);
        long ordinal = 0;
        foreach (var candidate in source)
        {
            if (!SearchResultFilter.SelectionCandidateMatches(candidate, query, formatQualifiedLabel))
                continue;

            var ranked = new SearchCandidateOptions.RankedCandidate(candidate,
                SearchCandidateOptions.LexicalScore(candidate, queryTokens), ordinal++);
            if (selected.Count < limit)
                selected.Enqueue(ranked, ranked);
            else
                selected.EnqueueDequeue(ranked, ranked);
        }

        return selected.UnorderedItems.Select(item => item.Element)
            .Order(SearchCandidateOptions.LexicalOrder).Select(item => item.Candidate).ToArray();
    }
}
