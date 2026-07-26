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
}
