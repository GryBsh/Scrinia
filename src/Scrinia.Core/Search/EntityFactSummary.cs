namespace Scrinia.Core.Search;

/// <summary>One co-occurring entity and how many of the summarized entity's memories it shares.</summary>
public readonly record struct CoOccurrence(EntityRef Entity, int Count);

/// <summary>A compact aggregate view of one entity: how many memories mention it, the span of their
/// dates, and its strongest co-occurring entities. Built by <see cref="EntityFactSummarizer"/> for
/// retrieval auto-surfacing. <see cref="First"/>/<see cref="Last"/> are null only when the entity
/// has no facts (defensive — callers pass entities known to the store).</summary>
public sealed record EntityFactSummary(
    EntityRef Entity,
    int MentionCount,
    DateTimeOffset? First,
    DateTimeOffset? Last,
    IReadOnlyList<CoOccurrence> TopCoOccurring);

/// <summary>Pure projection of an <see cref="EntityFactStore"/> into compact per-entity summaries
/// for the entities a query named. Deterministic: entities ranked by mention count desc, then
/// Key/Value ordinal (matching the store's own tie-breaks).</summary>
public static class EntityFactSummarizer
{
    public static IReadOnlyList<EntityFactSummary> Summarize(
        EntityFactStore store,
        IEnumerable<EntityRef> namedEntities,
        int maxEntities = 3,
        int coOccurrenceLimit = 3)
    {
        var summaries = new List<EntityFactSummary>();
        foreach (var entity in namedEntities.Distinct())
        {
            int count = store.MentionCount(entity);
            if (count == 0) continue; // named a key the candidate set doesn't actually carry
            var span = store.Span(entity);
            var coOcc = store.Neighbors(entity)   // already ordered by count desc
                .Take(coOccurrenceLimit)
                .Select(n => new CoOccurrence(n.Entity, n.Count))
                .ToArray();
            summaries.Add(new EntityFactSummary(entity, count, span?.First, span?.Last, coOcc));
        }

        return summaries
            .OrderByDescending(s => s.MentionCount)
            .ThenBy(s => s.Entity.Key, StringComparer.Ordinal)
            .ThenBy(s => s.Entity.Value, StringComparer.Ordinal)
            .Take(maxEntities)
            .ToArray();
    }
}
