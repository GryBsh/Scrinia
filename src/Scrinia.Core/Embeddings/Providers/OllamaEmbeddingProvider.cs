using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using Ollama's HTTP API.</summary>
public sealed class OllamaEmbeddingProvider : ResilientEmbeddingProvider
{
    private readonly string _model;

    public override bool IsAvailable => ObservedDimensions > 0;
    public override int Dimensions => ObservedDimensions;
    protected override string ProviderName => "Ollama";

    public OllamaEmbeddingProvider(string baseUrl, string model, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
        : base(CreateHttpClient(baseUrl), logger, circuitBreaker, retryOptions)
    {
        _model = model;
    }

    private static HttpClient CreateHttpClient(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
    {
        var request = new OllamaEmbedRequest(_model, text);
        return await Http.PostAsJsonAsync("api/embed", request, OllamaJsonContext.Default.OllamaEmbedRequest, ct);
    }

    protected override async Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaEmbedResponse, ct);
        if (result?.Embeddings is { Length: > 0 })
            return result.Embeddings[0];
        return null;
    }
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
