using System.Text.Json.Serialization;

namespace Scrinia.Mcp.SharedContracts;

public enum SharedAvailability
{
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("conflicted")] Conflicted
}
public enum SharedHitKind
{
    [JsonStringEnumMemberName("entry")] Entry,
    [JsonStringEnumMemberName("chunk")] Chunk
}
public enum SharedListKind { [JsonStringEnumMemberName("persistent")] Persistent }
public enum SharedDrift
{
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    [JsonStringEnumMemberName("changed")] Changed,
    [JsonStringEnumMemberName("missing")] Missing
}
public enum FactsOperation
{
    [JsonStringEnumMemberName("entities")] Entities,
    [JsonStringEnumMemberName("profile")] Profile,
    [JsonStringEnumMemberName("intersect")] Intersect
}
public enum MutationKind
{
    [JsonStringEnumMemberName("applied")] Applied,
    [JsonStringEnumMemberName("none")] None
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SharedBaseSummary(string Base, string? Root, SharedAvailability Availability, string? Generation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SearchHit(string Base, string Path, SharedHitKind Kind, int? Chunk, double Score, string Preview, IReadOnlyDictionary<string, string[]> Attributes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileDriftRef(string File, string? ExpectedSha256, string? ActualSha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListItem(string Base, string Path, SharedListKind Kind, string? Created, string? Updated, string? Description, IReadOnlyDictionary<string, string[]> Attributes, string[] Keywords, SharedDrift? Drift = null, FileDriftRef[]? Files = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecallResult(string Base, string Path, string[] Chunks, IReadOnlyDictionary<string, string[]> Attributes, string[] Keywords, string? Created, string? Updated);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EntityCount(string Entity, int Count);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FactsEntitiesResult(string Base, FactsOperation Op, EntityCount[] Rows, bool Truncated) : FactsResult(Base, Op);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FactsProfileResult(string Base, FactsOperation Op, string Entity, int Count, string? FirstSeen, string? LastSeen, EntityCount[] CoOccurrences, bool CoOccurrencesTruncated, string[] Timeline, bool TimelineTruncated) : FactsResult(Base, Op);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FactsIntersectResult(string Base, FactsOperation Op, string[] Paths, bool Truncated) : FactsResult(Base, Op);
public abstract record FactsResult(string Base, FactsOperation Op);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MutationResult(string Base, string? Path, string? Destination, MutationKind Mutation, string Generation, string[] Manifest);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefreshResult(string Base, string? PreviousGeneration, string NewGeneration, SharedAvailability Availability, SharedWarning[] Warnings);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SharedWarning(string Base, string Code, string Diagnostic);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SharedEnvelope))]
[JsonSerializable(typeof(SharedBaseSummary))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(FileDriftRef))]
[JsonSerializable(typeof(ListItem))]
[JsonSerializable(typeof(RecallResult))]
[JsonSerializable(typeof(EntityCount))]
[JsonSerializable(typeof(FactsEntitiesResult))]
[JsonSerializable(typeof(FactsProfileResult))]
[JsonSerializable(typeof(FactsIntersectResult))]
[JsonSerializable(typeof(MutationResult))]
[JsonSerializable(typeof(RefreshResult))]
[JsonSerializable(typeof(SharedWarning))]
public partial class SharedDtoJsonContext : JsonSerializerContext;
