using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HomeConfig))]
[JsonSerializable(typeof(SharedBasesMap))]
[JsonSerializable(typeof(SharedBaseEntry))]
public partial class HomeConfigJsonContext : JsonSerializerContext;
