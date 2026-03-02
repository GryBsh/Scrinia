using System.Text.Json.Serialization;

namespace Scrinia.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, double>))]
internal partial class PluginClientJsonContext : JsonSerializerContext;
