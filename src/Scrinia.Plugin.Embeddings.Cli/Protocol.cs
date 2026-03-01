using System.Text.Json.Serialization;

namespace Scrinia.Plugin.Embeddings.Cli;

internal sealed class PluginRequest
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

internal sealed class PluginResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("vector")]
    public float[]? Vector { get; init; }

    [JsonPropertyName("vectors")]
    public float[][]? Vectors { get; init; }

    [JsonPropertyName("scores")]
    public Dictionary<string, double>? Scores { get; init; }

    [JsonPropertyName("status")]
    public PluginStatus? Status { get; init; }
}

internal sealed class PluginStatus
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
[JsonSerializable(typeof(PluginRequest))]
[JsonSerializable(typeof(PluginResponse))]
[JsonSerializable(typeof(PluginStatus))]
internal partial class PluginJsonContext : JsonSerializerContext;
