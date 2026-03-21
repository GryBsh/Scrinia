using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using Azure AI Foundry (Azure OpenAI) embeddings API.</summary>
public sealed class AzureAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _requestUrl;
    private readonly bool _useV1;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;
    private int _dimensions;

    public bool IsAvailable => true;
    public int Dimensions => _dimensions > 0 ? _dimensions : 1536;

    public AzureAiEmbeddingProvider(
        string? endpoint, string? apiKey, string deployment, string model,
        string apiVersion, bool useV1, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Azure endpoint is required for the Azure embedding provider.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Azure API key is required for the Azure embedding provider.", nameof(apiKey));

        var baseUrl = endpoint.TrimEnd('/');
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("api-key", apiKey);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _useV1 = useV1;
        _logger = logger;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();

        _requestUrl = useV1
            ? $"{baseUrl}/openai/v1/embeddings"
            : $"{baseUrl}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}";
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            _circuitBreaker.EnsureClosed();

            var request = new AzureEmbedRequest(text, _useV1 ? _model : null);
            var response = await RetryPolicy.ExecuteAsync(
                async () => await _http.PostAsJsonAsync(_requestUrl, request, AzureJsonContext.Default.AzureEmbedRequest, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                _logger,
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(AzureJsonContext.Default.AzureEmbedResponse, ct);
            if (result?.Data is { Length: > 0 })
            {
                var vec = result.Data[0].Embedding;
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
            _logger.LogWarning(ex, "Azure embedding failed");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
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
