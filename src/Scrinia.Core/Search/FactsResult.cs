namespace Scrinia.Core.Search;

/// <summary>An entity paired with a count (mention count, or shared-memory count for co-occurrence).</summary>
public readonly record struct EntityCount(EntityRef Entity, int Count);

/// <summary>One memory on an entity's timeline / in an intersection: its id and effective date.</summary>
public readonly record struct TimelineEntry(string MemoryId, DateTimeOffset Date);

/// <summary>Result of resolving one entity argument against the store's vocabulary.</summary>
public abstract record EntityResolution
{
    public sealed record Resolved(EntityRef Entity) : EntityResolution;
    public sealed record Ambiguous(string Arg, IReadOnlyList<EntityCount> Candidates) : EntityResolution;
    public sealed record NotFound(string Arg) : EntityResolution;
}

/// <summary>Discriminated result of a facts query, rendered by FactsResponseFormatter.</summary>
public abstract record FactsResult;

public sealed record EntitiesListResult(IReadOnlyList<EntityCount> Items, int Total, bool Truncated) : FactsResult;

public sealed record ProfileResult(
    EntityRef Entity, int MentionCount, DateTimeOffset? First, DateTimeOffset? Last,
    IReadOnlyList<EntityCount> Neighbors, IReadOnlyList<TimelineEntry> Timeline, bool TimelineTruncated) : FactsResult;

public sealed record IntersectionResult(
    IReadOnlyList<EntityRef> Members, IReadOnlyList<TimelineEntry> Memories, int Count) : FactsResult;

/// <summary>A bare value matched more than one entity — the op is not performed.</summary>
public sealed record DisambiguationResult(string Arg, IReadOnlyList<EntityCount> Candidates) : FactsResult;

/// <summary>An entity argument matched nothing.</summary>
public sealed record NotFoundResult(string Arg) : FactsResult;
