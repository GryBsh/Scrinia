using System.Text.Json.Serialization;

namespace Scrinia.LongMemEvalV2.Worker.Protocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(LearnRequest))]
[JsonSerializable(typeof(RetrieveRequest))]
[JsonSerializable(typeof(ValidateSnapshotRequest))]
[JsonSerializable(typeof(LearningReceipt))]
[JsonSerializable(typeof(WorkerResult))]
[JsonSerializable(typeof(ControlLine))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;

public sealed record ControlLine
{
    public required string Operation { get; init; }
}
