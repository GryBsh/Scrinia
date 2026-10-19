using System.Text.Json.Serialization;

namespace Scrinia.LongMemEvalV2.Worker.Protocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Scrinia.LongMemEvalV2.Worker.Evidence.EvidenceSelection))]
[JsonSerializable(typeof(LearnRequest))]
[JsonSerializable(typeof(RetrieveRequest))]
[JsonSerializable(typeof(ValidateSnapshotRequest))]
[JsonSerializable(typeof(LearningReceipt))]
[JsonSerializable(typeof(WorkerResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string,string[]>))]
[JsonSerializable(typeof(ControlLine))]
[JsonSerializable(typeof(Scrinia.LongMemEvalV2.Worker.CatalogCaptureRequest))]
[JsonSerializable(typeof(Scrinia.LongMemEvalV2.Worker.Evidence.SourceCatalog))]
[JsonSerializable(typeof(Scrinia.LongMemEvalV2.Worker.Evidence.SourceMemory))]
[JsonSerializable(typeof(ModelContextProtocol.Protocol.Tool[]))]
[JsonSerializable(typeof(TrajectoryManifest))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;

public sealed record ControlLine
{
    public required string Operation { get; init; }
}
