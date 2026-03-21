using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using Ollama's HTTP API.</summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;
    private int _dimensions;

    public bool IsAvailable => _dimensions > 0;
    public int Dimensions => _dimensions;

    public OllamaEmbeddingProvider(string baseUrl, string model, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _logger = logger;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            _circuitBreaker.EnsureClosed();

            var request = new OllamaEmbedRequest(_model, text);
            var response = await RetryPolicy.ExecuteAsync(
                async () => await _http.PostAsJsonAsync("api/embed", request, OllamaJsonContext.Default.OllamaEmbedRequest, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                _logger,
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaEmbedResponse, ct);
            if (result?.Embeddings is { Length: > 0 })
            {
                var vec = result.Embeddings[0];
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
            _logger.LogWarning(ex, "Ollama embedding failed");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record OllamaEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);

internal sealed class OllamaEmbedResponse
{
    [JsonPropertyName("embeddings")]
    public float[][]? Embeddings { get; set; }
}

[JsonSerializable(typeof(OllamaEmbedRequest))]
[JsonSerializable(typeof(OllamaEmbedResponse))]
internal partial class OllamaJsonContext : JsonSerializerContext;
