using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using the Google Gemini embedContent API.</summary>
public sealed class GoogleGeminiEmbeddingProvider : ResilientEmbeddingProvider
{
    private readonly string _requestUrl;
    private readonly int _configuredDimensions;
    private readonly string _model;

    public override int Dimensions => ObservedDimensions > 0 ? ObservedDimensions : (_configuredDimensions > 0 ? _configuredDimensions : 3072);
    public override string Signature => $"google:{_model}";
    protected override string ProviderName => "Google Gemini";

    public GoogleGeminiEmbeddingProvider(string? apiKey, string model, string baseUrl, int dimensions, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
        : base(CreateHttpClient(apiKey), logger, circuitBreaker, retryOptions)
    {
        _configuredDimensions = dimensions;
        _model = model;

        var url = baseUrl.TrimEnd('/');
        _requestUrl = $"{url}/v1beta/models/{model}:embedContent";
    }

    private static HttpClient CreateHttpClient(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Google API key is required for the Google Gemini embedding provider.", nameof(apiKey));

        var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
        return http;
    }

    protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
    {
        var request = new GeminiEmbedRequest(
            new GeminiContent([new GeminiPart(text)]),
            "RETRIEVAL_DOCUMENT",
            _configuredDimensions > 0 ? _configuredDimensions : null);
        return await Http.PostAsJsonAsync(_requestUrl, request, GeminiJsonContext.Default.GeminiEmbedRequest, ct);
    }

    protected override async Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(GeminiJsonContext.Default.GeminiEmbedResponse, ct);
        if (result?.Embedding?.Values is { Length: > 0 })
            return result.Embedding.Values;
        return null;
    }
}

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string Text);

internal sealed record GeminiContent(
    [property: JsonPropertyName("parts")] GeminiPart[] Parts);

internal sealed class GeminiEmbedRequest
{
    public GeminiEmbedRequest(GeminiContent content, string taskType, int? outputDimensionality)
    {
        Content = content;
        TaskType = taskType;
        OutputDimensionality = outputDimensionality;
    }

    [JsonPropertyName("content")]
    public GeminiContent Content { get; }

    [JsonPropertyName("taskType")]
    public string TaskType { get; }

    [JsonPropertyName("outputDimensionality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputDimensionality { get; }
}

internal sealed class GeminiEmbedResponse
{
    [JsonPropertyName("embedding")]
    public GeminiEmbeddingValues? Embedding { get; set; }
}

internal sealed class GeminiEmbeddingValues
{
    [JsonPropertyName("values")]
    public float[] Values { get; set; } = [];
}

[JsonSerializable(typeof(GeminiEmbedRequest))]
[JsonSerializable(typeof(GeminiEmbedResponse))]
internal partial class GeminiJsonContext : JsonSerializerContext;
