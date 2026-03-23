using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using the OpenAI embeddings API.</summary>
public sealed class OpenAiEmbeddingProvider : ResilientEmbeddingProvider
{
    private readonly string _model;

    public override int Dimensions => ObservedDimensions > 0 ? ObservedDimensions : 1536;
    protected override string ProviderName => "OpenAI";

    public OpenAiEmbeddingProvider(string? apiKey, string model, string baseUrl, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
        : base(CreateHttpClient(apiKey, baseUrl), logger, circuitBreaker, retryOptions)
    {
        _model = model;
    }

    private static HttpClient CreateHttpClient(string? apiKey, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key is required for the OpenAI embedding provider.", nameof(apiKey));

        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
    {
        var request = new OpenAiEmbedRequest(_model, text);
        return await Http.PostAsJsonAsync("embeddings", request, OpenAiJsonContext.Default.OpenAiEmbedRequest, ct);
    }

    protected override async Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(OpenAiJsonContext.Default.OpenAiEmbedResponse, ct);
        if (result?.Data is { Length: > 0 })
            return result.Data[0].Embedding;
        return null;
    }
}

internal sealed record OpenAiEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);

internal sealed class OpenAiEmbedResponse
{
    [JsonPropertyName("data")]
    public OpenAiEmbeddingData[]? Data { get; set; }
}

internal sealed class OpenAiEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];
}

[JsonSerializable(typeof(OpenAiEmbedRequest))]
[JsonSerializable(typeof(OpenAiEmbedResponse))]
internal partial class OpenAiJsonContext : JsonSerializerContext;
