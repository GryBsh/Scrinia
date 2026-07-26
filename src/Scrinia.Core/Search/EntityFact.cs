using Scrinia.Core;

namespace Scrinia.Core.Search;

/// <summary>A single entity: an attribute (key, value) pair. Key and Value are lower-cased and
/// kept whole (values are not tokenized) — identical to <see cref="EntityGraph"/> node identity,
/// so the fact store and the graph never disagree on entity identity.</summary>
public readonly record struct EntityRef(string Key, string Value)
{
    public static EntityRef Of(string key, string value) =>
        new(key.Trim().ToLowerInvariant(), value.Trim().ToLowerInvariant());
}

/// <summary>Compact per-memory unit: the memory's raw identity, effective date, and distinct entity
/// set. Stores the raw <c>(Scope, Subject)</c> and derives both presentations: <see cref="MemoryId"/>
/// (the recall-able qualified name, user-facing) and <see cref="ScoringKey"/> (the scorer's
/// "{scope}|{name}" join key). One identity, two views — no translation layer.</summary>
public sealed record MemoryEntities(
    string Scope,
    string Subject,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> Entities)
{
    /// <summary>Recall-able qualified name (unchanged value vs the old stored MemoryId).</summary>
    public string MemoryId => MemoryNaming.FormatRoundTrip(Scope, Subject);

    /// <summary>The scorer's join key, shared with the entity-graph / path-segment / embeddings
    /// contributors and <c>WeightedAttributeScorer.GetSupplemental</c>.</summary>
    public string ScoringKey => $"{Scope}|{Subject}";
}

/// <summary>A fact returned by <see cref="EntityFactStore"/> queries: one entity as seen in one
/// memory, with that memory's date and the other entities co-occurring in it.</summary>
public sealed record EntityFact(
    EntityRef Entity,
    string MemoryId,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> CoOccurring);
