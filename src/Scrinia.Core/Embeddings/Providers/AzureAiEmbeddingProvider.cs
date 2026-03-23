using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using Azure AI Foundry (Azure OpenAI) embeddings API.</summary>
public sealed class AzureAiEmbeddingProvider : ResilientEmbeddingProvider
{
    private readonly string _model;
    private readonly string _requestUrl;
    private readonly bool _useV1;

    public override int Dimensions => ObservedDimensions > 0 ? ObservedDimensions : 1536;
    protected override string ProviderName => "Azure";

    public AzureAiEmbeddingProvider(
        string? endpoint, string? apiKey, string deployment, string model,
        string apiVersion, bool useV1, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
        : base(CreateHttpClient(endpoint, apiKey), logger, circuitBreaker, retryOptions)
    {
        _model = model;
        _useV1 = useV1;

        var baseUrl = endpoint!.TrimEnd('/');
        _requestUrl = useV1
            ? $"{baseUrl}/openai/v1/embeddings"
            : $"{baseUrl}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}";
    }

    private static HttpClient CreateHttpClient(string? endpoint, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Azure endpoint is required for the Azure embedding provider.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Azure API key is required for the Azure embedding provider.", nameof(apiKey));

        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("api-key", apiKey);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
    {
        var request = new AzureEmbedRequest(text, _useV1 ? _model : null);
        return await Http.PostAsJsonAsync(_requestUrl, request, AzureJsonContext.Default.AzureEmbedRequest, ct);
    }

    protected override async Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(AzureJsonContext.Default.AzureEmbedResponse, ct);
        if (result?.Data is { Length: > 0 })
            return result.Data[0].Embedding;
        return null;
    }
}

internal sealed class AzureEmbedRequest
{
    public AzureEmbedRequest(string input, string? model)
    {
        Input = input;
        Model = model;
    }

    [JsonPropertyName("input")]
    public string Input { get; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; }
}

internal sealed class AzureEmbedResponse
{
    [JsonPropertyName("data")]
    public AzureEmbeddingData[]? Data { get; set; }
}

internal sealed class AzureEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];
}

[JsonSerializable(typeof(AzureEmbedRequest))]
[JsonSerializable(typeof(AzureEmbedResponse))]
internal partial class AzureJsonContext : JsonSerializerContext;
