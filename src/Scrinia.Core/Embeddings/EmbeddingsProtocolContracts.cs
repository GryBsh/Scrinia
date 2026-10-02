using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Embeddings;

public sealed record EmbeddingsStatusResponse(
    int ProtocolVersion,
    string[] Capabilities,
    bool ProviderAvailable,
    int Dimensions,
    string VectorStoreSignature);

public sealed record EmbeddingsIndexRequest(string Scope, string Name, string[] Content);

public sealed record EmbeddingsSearchRequest(
    string Query,
    IReadOnlyDictionary<string, string[]> CandidatesByScope);

public sealed record EmbeddingsRemoveRequest(string Scope, string Name);

public sealed record EmbeddingsOperationResponse(bool Ok, string? Error);

public sealed record EmbeddingsSearchResponse(
    [property: JsonPropertyName("scores")]
    IReadOnlyDictionary<string, double> Scores,
    [property: JsonPropertyName("error")]
    string? Error);

public sealed class EmbeddingsProtocolUnsupportedException(string message)
    : InvalidOperationException(message);

public sealed class EmbeddingsProtocolInvalidException(string message)
    : InvalidOperationException(message);

public static class EmbeddingsProtocol
{
    public const int Version = 1;
    public static readonly string[] RequiredCapabilities = ["status", "index", "search", "remove"];

    public static bool IsSupportedCapability(string capability) =>
        RequiredCapabilities.Contains(capability, StringComparer.Ordinal);

    public static Exception? ValidateStatus(EmbeddingsStatusResponse? status)
    {
        if (status is null)
            return new EmbeddingsProtocolInvalidException("Embeddings status response is missing.");

        if (status.ProtocolVersion != Version)
            return new EmbeddingsProtocolUnsupportedException(
                $"Embeddings protocol version {status.ProtocolVersion} is unsupported; expected {Version}.");

        if (status.Capabilities is null)
            return new EmbeddingsProtocolInvalidException("Embeddings status must include capabilities.");

        if (status.Capabilities.Length != status.Capabilities.Distinct(StringComparer.Ordinal).Count())
            return new EmbeddingsProtocolInvalidException("Embeddings status capabilities must be unique.");

        if (status.Capabilities.Length != RequiredCapabilities.Length ||
            status.Capabilities.Any(capability =>
                capability is null || !RequiredCapabilities.Contains(capability, StringComparer.Ordinal)) ||
            RequiredCapabilities.Any(required => !status.Capabilities.Contains(required, StringComparer.Ordinal)))
            return new EmbeddingsProtocolInvalidException(
                "Embeddings status must advertise exactly the supported capabilities.");

        if (!status.ProviderAvailable)
            return new EmbeddingsProtocolInvalidException("Embeddings provider is unavailable.");

        if (status.Dimensions <= 0 || string.IsNullOrWhiteSpace(status.VectorStoreSignature))
            return new EmbeddingsProtocolInvalidException(
                "Embeddings status must include positive dimensions and a vector-store signature.");

        return null;
    }

    public static void EnsureValidStatus(EmbeddingsStatusResponse? status)
    {
        Exception? failure = ValidateStatus(status);
        if (failure is not null)
            throw failure;
    }
}

public static class EmbeddingsProtocolJson
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value,
            EmbeddingsProtocolJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"No JSON metadata exists for {typeof(T).Name}."));

    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value,
            EmbeddingsProtocolJsonContext.Default.GetTypeInfo(value.GetType())
            ?? throw new InvalidOperationException($"No JSON metadata exists for {value.GetType().Name}."));

    public static object? Deserialize(Type type, string json) =>
        JsonSerializer.Deserialize(json,
            EmbeddingsProtocolJsonContext.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException($"No JSON metadata exists for {type.Name}."));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmbeddingsStatusResponse))]
[JsonSerializable(typeof(EmbeddingsIndexRequest))]
[JsonSerializable(typeof(EmbeddingsSearchRequest))]
[JsonSerializable(typeof(EmbeddingsRemoveRequest))]
[JsonSerializable(typeof(EmbeddingsOperationResponse))]
[JsonSerializable(typeof(EmbeddingsSearchResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public partial class EmbeddingsProtocolJsonContext : JsonSerializerContext;
