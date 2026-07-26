namespace Scrinia.Core.Search;

/// <summary>Pure projection of an <see cref="EntityFactStore"/> into <see cref="FactsResult"/>s for
/// the explicit `facts` tool. Reuses only the store's public query API; adds no store logic.
/// Owns entity-argument resolution (key:value exact, or bare value across keys). Deterministic.</summary>
public static class FactsQuery
{
    /// <summary>Resolves one entity argument. "key:value" → exact; bare "value" → all entities with
    /// that value (1 → Resolved, 0 → NotFound, &gt;1 → Ambiguous). Keys/values lowercased.</summary>
    public static EntityResolution Resolve(EntityFactStore store, string arg)
    {
        string trimmed = (arg ?? string.Empty).Trim();
        int colon = trimmed.IndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1)
        {
            var e = EntityRef.Of(trimmed[..colon], trimmed[(colon + 1)..]);
            return store.MentionCount(e) > 0 ? new EntityResolution.Resolved(e) : new EntityResolution.NotFound(trimmed);
        }

        string value = trimmed.ToLowerInvariant();
        var matches = store.Entities()
            .Where(e => e.Value == value)
            .Select(e => new EntityCount(e, store.MentionCount(e)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Entity.Key, StringComparer.Ordinal)
            .ToArray();

        return matches.Length switch
        {
            0 => new EntityResolution.NotFound(trimmed),
            1 => new EntityResolution.Resolved(matches[0].Entity),
            _ => new EntityResolution.Ambiguous(trimmed, matches),
        };
    }

    /// <summary>Lists the entity vocabulary (optionally one key), by mention count desc then
    /// key/value ordinal, capped at <paramref name="limit"/>.</summary>
    public static FactsResult EntitiesList(EntityFactStore store, string? key, int limit)
    {
        string? k = string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();
        var all = store.Entities(k)
            .Select(e => new EntityCount(e, store.MentionCount(e)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Entity.Key, StringComparer.Ordinal)
            .ThenBy(c => c.Entity.Value, StringComparer.Ordinal)
            .ToArray();

        int cap = limit < 0 ? 0 : limit;
        var items = all.Take(cap).ToArray();
        return new EntitiesListResult(items, all.Length, all.Length > items.Length);
    }

    public const int ProfileNeighborLimit = 10;

    /// <summary>Deep-dive on one entity: count, span, top co-occurrences, and the chronological
    /// timeline (capped at <paramref name="timelineLimit"/>). Ambiguous/not-found bare args surface
    /// as Disambiguation/NotFound results.</summary>
    public static FactsResult Profile(EntityFactStore store, string entityArg, int timelineLimit)
    {
        var resolution = Resolve(store, entityArg);
        if (resolution is not EntityResolution.Resolved resolved)
            return FromFailedResolution(resolution);

        var e = resolved.Entity;
        var span = store.Span(e);
        var neighbors = store.Neighbors(e)
            .Take(ProfileNeighborLimit)
            .Select(n => new EntityCount(n.Entity, n.Count))
            .ToArray();

        var full = store.Timeline(e);   // already Date asc then MemoryId ordinal
        int cap = timelineLimit < 0 ? 0 : timelineLimit;
        var timeline = full.Take(cap).Select(f => new TimelineEntry(f.MemoryId, f.Date)).ToArray();

        return new ProfileResult(e, store.MentionCount(e), span?.First, span?.Last,
            neighbors, timeline, full.Count > timeline.Length);
    }

    /// <summary>Memories mentioning ALL given entities. Any ambiguous/not-found member short-circuits
    /// to that member's Disambiguation/NotFound result (op not performed).</summary>
    public static FactsResult Intersect(EntityFactStore store, IReadOnlyList<string> entityArgs)
    {
        if (entityArgs.Count == 0)
            return new IntersectionResult([], [], 0);

        var refs = new List<EntityRef>(entityArgs.Count);
        foreach (var arg in entityArgs)
        {
            var resolution = Resolve(store, arg);
            if (resolution is not EntityResolution.Resolved r)
                return FromFailedResolution(resolution);
            refs.Add(r.Entity);
        }

        var ids = store.MemoriesFor(refs.ToArray());
        // Dates: every intersecting memory appears in each member's facts, so the first member's
        // facts carry the dates. No new store method needed.
        var idSet = new HashSet<string>(ids, StringComparer.Ordinal);
        var dateById = store.Facts(refs[0])
            .Where(f => idSet.Contains(f.MemoryId))
            .GroupBy(f => f.MemoryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Date, StringComparer.Ordinal);

        var memories = ids
            .Select(id => new TimelineEntry(id, dateById.TryGetValue(id, out var d) ? d : default))
            .OrderBy(m => m.Date)
            .ThenBy(m => m.MemoryId, StringComparer.Ordinal)
            .ToArray();

        return new IntersectionResult(refs, memories, memories.Length);
    }

    /// <summary>Maps an already-computed non-Resolved resolution to the matching FactsResult.</summary>
    private static FactsResult FromFailedResolution(EntityResolution resolution) =>
        resolution switch
        {
            EntityResolution.Ambiguous a => new DisambiguationResult(a.Arg, a.Candidates),
            EntityResolution.NotFound n => new NotFoundResult(n.Arg),
            _ => throw new InvalidOperationException("FromFailedResolution called with a Resolved resolution."),
        };
}
