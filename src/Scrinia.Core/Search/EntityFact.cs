namespace Scrinia.Core.Search;

/// <summary>A single entity: an attribute (key, value) pair. Key and Value are lower-cased and
/// kept whole (values are not tokenized) — identical to <see cref="EntityGraph"/> node identity,
/// so the fact store and the graph never disagree on entity identity.</summary>
public readonly record struct EntityRef(string Key, string Value)
{
    public static EntityRef Of(string key, string value) =>
        new(key.Trim().ToLowerInvariant(), value.Trim().ToLowerInvariant());
}

/// <summary>Compact per-memory unit: the memory's effective date and its distinct entity set.
/// This is what is derived from an entry and what the sidecar persists (O(entities) per memory).</summary>
public sealed record MemoryEntities(
    string MemoryId,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> Entities);

/// <summary>A fact returned by <see cref="EntityFactStore"/> queries: one entity as seen in one
/// memory, with that memory's date and the other entities co-occurring in it.</summary>
public sealed record EntityFact(
    EntityRef Entity,
    string MemoryId,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> CoOccurring);
