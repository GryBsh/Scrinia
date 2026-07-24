namespace Scrinia.Core.Search;

/// <summary>
/// A materialized, aggregatable view over memories' entities. Built from compact
/// <see cref="MemoryEntities"/> records (derived from entries, or loaded from a persisted sidecar).
/// Pure and I/O-free. Every attribute (key, value) pair is an entity, uniformly; predicate = key.
/// </summary>
public sealed class EntityFactStore
{
    // entity -> one row per mentioning memory (memoryId, date, that memory's full entity set)
    private readonly Dictionary<EntityRef, List<MemoryEntities>> _byEntity = new();

    public EntityFactStore(IEnumerable<MemoryEntities> memories)
    {
        foreach (var mem in memories)
            foreach (var entity in mem.Entities.Distinct())
                (_byEntity.TryGetValue(entity, out var list) ? list : _byEntity[entity] = [])
                    .Add(mem);
    }

    public IReadOnlyList<EntityFact> Facts(EntityRef entity)
    {
        if (!_byEntity.TryGetValue(entity, out var mems)) return [];
        return mems
            .Select(m => new EntityFact(
                entity, m.MemoryId, m.Date,
                m.Entities.Where(e => e != entity).ToArray()))
            .ToArray();
    }

    public int MentionCount(EntityRef entity) =>
        _byEntity.TryGetValue(entity, out var mems) ? mems.Count : 0;

    public IReadOnlyList<string> MemoriesFor(params EntityRef[] entities)
    {
        if (entities.Length == 0) return [];
        IEnumerable<string>? acc = null;
        foreach (var e in entities)
        {
            var ids = _byEntity.TryGetValue(e, out var mems)
                ? mems.Select(m => m.MemoryId)
                : [];
            acc = acc is null ? ids : acc.Intersect(ids);
        }
        return acc!.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<EntityRef> Entities(string? key = null) =>
        _byEntity.Keys
            .Where(e => key is null || e.Key == key)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ThenBy(e => e.Value, StringComparer.Ordinal)
            .ToArray();
}
