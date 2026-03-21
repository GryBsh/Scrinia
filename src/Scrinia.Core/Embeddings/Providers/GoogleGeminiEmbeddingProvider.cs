using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using the Google Gemini embedContent API.</summary>
public sealed class GoogleGeminiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _requestUrl;
    private readonly int _configuredDimensions;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;
    private int _dimensions;

    public bool IsAvailable => true;
    public int Dimensions => _dimensions > 0 ? _dimensions : (_configuredDimensions > 0 ? _configuredDimensions : 3072);

    public GoogleGeminiEmbeddingProvider(string? apiKey, string model, string baseUrl, int dimensions, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Google API key is required for the Google Gemini embedding provider.", nameof(apiKey));

        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _configuredDimensions = dimensions;
        _logger = logger;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();

        var url = baseUrl.TrimEnd('/');
        _requestUrl = $"{url}/v1beta/models/{model}:embedContent?key={apiKey}";
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            _circuitBreaker.EnsureClosed();

            var request = new GeminiEmbedRequest(
                new GeminiContent([new GeminiPart(text)]),
                "RETRIEVAL_DOCUMENT",
                _configuredDimensions > 0 ? _configuredDimensions : null);
            var response = await RetryPolicy.ExecuteAsync(
                async () => await _http.PostAsJsonAsync(_requestUrl, request, GeminiJsonContext.Default.GeminiEmbedRequest, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                _logger,
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(GeminiJsonContext.Default.GeminiEmbedResponse, ct);
            if (result?.Embedding?.Values is { Length: > 0 })
            {
                var vec = result.Embedding.Values;
                if (_dimensions == 0)
                    _dimensions = vec.Length;
                VectorMath.L2Normalize(vec);
                _circuitBreaker.RecordSuccess();
                return vec;
            }
            return null;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "Google Gemini embedding failed");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
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
