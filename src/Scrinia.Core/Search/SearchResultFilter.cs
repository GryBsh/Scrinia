using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Path-prefix and attribute predicates for search results and candidates. Relocated from the
/// MCP layer into Core so the store can apply them BEFORE the top-K cut (symmetric with how
/// excludeTopics pre-filters candidates), rather than post-filtering an already-limited result
/// set. Path-prefix is a case-insensitive StartsWith on the qualified label (ephemeral entries
/// labelled "~{name}", topics via MemoryNaming.FormatScopeLabel). Attribute filter is AND across
/// keys, OR within each key's values, case-insensitive; it excludes topics (which carry no
/// attributes).
/// </summary>
public static class SearchResultFilter
{
    /// <summary>Keep-predicate for an entry candidate (covers its entry and chunk results —
    /// both inherit the parent's path and attributes).</summary>
    public static bool CandidateMatches(
        IMemoryStore store, ScopedArtifact candidate,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        string label = candidate.Scope == "ephemeral"
            ? $"~{candidate.Entry.Name}"
            : store.FormatQualifiedName(candidate.Scope, candidate.Entry.Name);
        return MatchesPathPrefix(label, pathPrefix)
            && MatchesAttributes(candidate.Entry.Attributes, attributeFilter);
    }

    /// <summary>Keep-predicate for a topic. Topics carry no attributes, so a topic is kept only
    /// when no attribute filter is active AND its scope label matches the path prefix. Callers
    /// that have an active attribute filter should drop all topics without calling this.</summary>
    public static bool TopicMatches(TopicInfo topic, string? pathPrefix)
        => MatchesPathPrefix(MemoryNaming.FormatScopeLabel(topic.Scope), pathPrefix);

    /// <summary>Keep-predicate over a materialized <see cref="SearchResult"/>. Used by the
    /// default <see cref="IMemoryStore"/> post-filter fallback for stores without pre-limit
    /// candidate filtering.</summary>
    public static bool Matches(
        IMemoryStore store, SearchResult result,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        string label = result switch
        {
            EntryResult er => er.Item.Scope == "ephemeral"
                ? $"~{er.Item.Entry.Name}"
                : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            ChunkEntryResult cr => cr.ParentItem.Scope == "ephemeral"
                ? $"~{cr.ParentItem.Entry.Name}"
                : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            TopicResult tr => MemoryNaming.FormatScopeLabel(tr.Scope),
            _ => string.Empty,
        };
        if (!MatchesPathPrefix(label, pathPrefix)) return false;

        Dictionary<string, string[]>? attributes = result switch
        {
            EntryResult er => er.Item.Entry.Attributes,
            ChunkEntryResult cr => cr.ParentItem.Entry.Attributes,
            _ => null, // TopicResult: no attributes -> excluded when a filter is active
        };
        return MatchesAttributes(attributes, attributeFilter);
    }

    private static bool MatchesPathPrefix(string label, string? pathPrefix)
        => string.IsNullOrWhiteSpace(pathPrefix)
        || label.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAttributes(
        Dictionary<string, string[]>? attributes, IReadOnlyDictionary<string, string[]>? filter)
    {
        if (filter is null || filter.Count == 0) return true;
        if (attributes is null || attributes.Count == 0) return false;
        foreach (var want in filter)
        {
            if (!attributes.TryGetValue(want.Key.Trim().ToLowerInvariant(), out var have)
                || have is null || have.Length == 0)
                return false;
            if (want.Value is { Length: > 0 })
            {
                bool any = false;
                foreach (string wv in want.Value)
                {
                    foreach (string hv in have)
                        if (string.Equals(hv, wv, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                    if (any) break;
                }
                if (!any) return false;
            }
        }
        return true;
    }
}
