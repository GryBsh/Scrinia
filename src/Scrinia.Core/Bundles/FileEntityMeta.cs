using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Bundles;

/// <summary>Metadata sidecar for a skill file on disk.</summary>
public record SkillFileMeta(
    string? BasedOn,
    string? Role,
    string[]? Capabilities,
    string? Scaffold,
    string? CreatedAt,
    string? UpdatedAt,
    [property: JsonPropertyName("v")] int? SchemaVersion = null)
{
    /// <summary>Fields unknown to this build, retained across accepted rewrites.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Metadata sidecar for an agent config file on disk.</summary>
public record AgentFileMeta(
    string? CreatedAt,
    string? UpdatedAt,
    [property: JsonPropertyName("v")] int? SchemaVersion = null)
{
    /// <summary>Fields unknown to this build, retained across accepted rewrites.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SkillFileMeta))]
[JsonSerializable(typeof(AgentFileMeta))]
public partial class FileEntityJsonContext : JsonSerializerContext;
