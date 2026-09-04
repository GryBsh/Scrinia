using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HomeConfig))]
[JsonSerializable(typeof(SharedBasesMap))]
[JsonSerializable(typeof(SharedBaseEntry))]
[JsonSerializable(typeof(ConfigurationJsonResult))]
[JsonSerializable(typeof(SharedBaseJsonItem))]
public partial class HomeConfigJsonContext : JsonSerializerContext;
