using System.Text.Json.Serialization;

namespace Scrinia.Agent.Benchmarks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true)]
[JsonSerializable(typeof(RunConfiguration))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ConfigurationJsonContext : JsonSerializerContext;
