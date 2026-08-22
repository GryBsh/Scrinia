using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using the Voyage AI embeddings API.</summary>
public sealed class VoyageAiEmbeddingProvider : ResilientEmbeddingProvider
{
    private readonly string _model;

    public override int Dimensions => ObservedDimensions > 0 ? ObservedDimensions : 1024;
    public override string Signature => $"voyageai:{_model}";
    protected override string ProviderName => "Voyage AI";
    protected override bool SupportsBatch => true;

    public VoyageAiEmbeddingProvider(string? apiKey, string model, string baseUrl, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
        : base(CreateHttpClient(apiKey, baseUrl), logger, circuitBreaker, retryOptions)
    {
        _model = model;
    }

    private static HttpClient CreateHttpClient(string? apiKey, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Voyage AI API key is required for the Voyage AI embedding provider.", nameof(apiKey));

        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
    {
        var request = new VoyageAiEmbedRequest(_model, text);
        return await Http.PostAsJsonAsync("embeddings", request, VoyageAiJsonContext.Default.VoyageAiEmbedRequest, ct);
    }

    protected override async Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(VoyageAiJsonContext.Default.VoyageAiEmbedResponse, ct);
        if (result?.Data is { Length: > 0 })
            return result.Data[0].Embedding;
        return null;
    }

    protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(
        IReadOnlyList<string> texts, CancellationToken ct) =>
        Http.PostAsJsonAsync("embeddings", new VoyageAiBatchRequest(_model, texts),
            VoyageAiJsonContext.Default.VoyageAiBatchRequest, ct);

    protected override async Task<float[][]?> ParseEmbeddingBatchResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync(VoyageAiJsonContext.Default.VoyageAiEmbedResponse, ct);
        return OrderIndexedVectors(result?.Data, result?.Data?.Length ?? 0,
            item => item.Index, item => item.Embedding);
    }
}

internal sealed record VoyageAiEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("input_type")] string InputType = "document");

internal sealed record VoyageAiBatchRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
    [property: JsonPropertyName("input_type")] string InputType = "document");

internal sealed class VoyageAiEmbedResponse
{
    [JsonPropertyName("data")]
    public VoyageAiEmbeddingData[]? Data { get; set; }
}

internal sealed class VoyageAiEmbeddingData
{
    [JsonPropertyName("index")]
    public int Index { get; set; }
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];
}

[JsonSerializable(typeof(VoyageAiEmbedRequest))]
[JsonSerializable(typeof(VoyageAiBatchRequest))]
[JsonSerializable(typeof(VoyageAiEmbedResponse))]
internal partial class VoyageAiJsonContext : JsonSerializerContext;
