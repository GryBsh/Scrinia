using System.Text.Json.Serialization;

namespace Scrinia.Agent.Benchmarks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModelContextProtocol.Protocol.Tool))]
[JsonSerializable(typeof(UsageTotals))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RuntimeJsonContext : JsonSerializerContext;
