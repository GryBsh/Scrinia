using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Derives a memory's compact entity set from its entry, using the identical
/// <see cref="EntityGraph.ParseEntityKeywords"/> ∪ <see cref="EntityGraph.ParseEntityAttributes"/>
/// union the graph consumes. Fact date is the entry's effective last-authored date
/// (<c>UpdatedAt ?? CreatedAt</c>). Null when the memory carries no entities.</summary>
public static class EntityFactDerivation
{
    public static MemoryEntities? Derive(ScopedArtifact item, string memoryId)
    {
        var e = item.Entry;
        var entities = EntityGraph.ParseEntityKeywords(e.Keywords)
            .Concat(EntityGraph.ParseEntityAttributes(e.Attributes))
            .Select(t => EntityRef.Of(t.Type, t.Value))
            .Distinct()
            .ToArray();

        if (entities.Length == 0) return null;
        return new MemoryEntities(memoryId, e.UpdatedAt ?? e.CreatedAt, entities);
    }
}
