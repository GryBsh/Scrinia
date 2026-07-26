using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Derives a memory's compact entity set from its entry, using the identical
/// <see cref="EntityGraph.ParseEntityKeywords"/> ∪ <see cref="EntityGraph.ParseEntityAttributes"/>
/// union the graph consumes. Fact date is the entry's effective last-authored date
/// (<c>UpdatedAt ?? CreatedAt</c>). Null when the memory carries no entities.</summary>
public static class EntityFactDerivation
{
    public static MemoryEntities? Derive(ScopedArtifact item)
    {
        var e = item.Entry;
        var entities = EntityGraph.ParseEntityKeywords(e.Keywords)
            .Concat(EntityGraph.ParseEntityAttributes(e.Attributes))
            .Select(t => EntityRef.Of(t.Type, t.Value))
            .Distinct()
            .ToArray();

        if (entities.Length == 0) return null;
        return new MemoryEntities(item.Scope, item.Entry.Name, e.UpdatedAt ?? e.CreatedAt, entities);
    }

    /// <summary>Derive→filter→build the one shared way. Folds the duplicate derive loops in
    /// <c>IMemoryStore.GetEntityFactStore</c>, <c>MemoryTools.Core.BuildEntityFacts</c>, and
    /// <c>MemoryTools.Facts.SelectFactStore</c>; also the entity-graph filtered path (Task 6).</summary>
    public static EntityFactStore BuildStore(IEnumerable<ScopedArtifact> items) =>
        new(items.Select(Derive).Where(m => m is not null).Select(m => m!));
}
