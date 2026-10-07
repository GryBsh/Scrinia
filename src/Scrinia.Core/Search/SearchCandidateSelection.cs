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

        var selected = new List<ScopedArtifact>(limit);
        foreach (var candidate in source)
        {
            if (!SearchResultFilter.SelectionCandidateMatches(candidate, query, formatQualifiedLabel))
                continue;

            selected.Add(candidate);
            if (selected.Count > limit)
                selected = SearchCandidateOptions.RankLexically(selected, query.Query).Take(limit).ToList();
        }

        return SearchCandidateOptions.RankLexically(selected, query.Query);
    }
}
