using System.Text.Json.Serialization;

namespace Scrinia.Agent.Benchmarks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(CaseOutcome))]
[JsonSerializable(typeof(AnswerRecord))]
[JsonSerializable(typeof(UsageTotals))]
[JsonSerializable(typeof(OutcomeEnvelope))]
[JsonSerializable(typeof(ModelContextProtocol.Protocol.Tool))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RecordJsonContext : JsonSerializerContext;
