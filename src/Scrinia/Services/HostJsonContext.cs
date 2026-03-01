using System.Text.Json.Serialization;

namespace Scrinia.Services;

// ── Host-side DTOs (mirror of Plugin.Embeddings.Cli/Protocol.cs) ─────────
// Keep in sync with the plugin's PluginRequest/PluginResponse types.
// These are separate types so the CLI has no project reference to the plugin exe.

internal sealed class HostRequest
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("texts")]
    public string[]? Texts { get; init; }

    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("chunkIndex")]
    public int? ChunkIndex { get; init; }

    [JsonPropertyName("scopes")]
    public string[]? Scopes { get; init; }
}

internal sealed class HostResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("scores")]
    public Dictionary<string, double>? Scores { get; init; }

    [JsonPropertyName("status")]
    public HostStatus? Status { get; init; }
}

internal sealed class HostStatus
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("hardware")]
    public string? Hardware { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; init; }

    [JsonPropertyName("vectorCount")]
    public int VectorCount { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(HostStatus))]
internal partial class HostJsonContext : JsonSerializerContext;
