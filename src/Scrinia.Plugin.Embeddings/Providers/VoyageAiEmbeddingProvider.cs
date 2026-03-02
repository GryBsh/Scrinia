using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings.Providers;

/// <summary>Embedding provider using the Voyage AI embeddings API.</summary>
public sealed class VoyageAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private int _dimensions;

    public bool IsAvailable => true;
    public int Dimensions => _dimensions > 0 ? _dimensions : 1024;

    public VoyageAiEmbeddingProvider(string? apiKey, string model, string baseUrl, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Voyage AI API key is required for the Voyage AI embedding provider.", nameof(apiKey));

        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _logger = logger;
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var request = new VoyageAiEmbedRequest(_model, text);
            var response = await _http.PostAsJsonAsync("embeddings", request, VoyageAiJsonContext.Default.VoyageAiEmbedRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(VoyageAiJsonContext.Default.VoyageAiEmbedResponse, ct);
            if (result?.Data is { Length: > 0 })
            {
                var vec = result.Data[0].Embedding;
                if (_dimensions == 0)
                    _dimensions = vec.Length;
                L2Normalize(vec);
                return vec;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voyage AI embedding failed");
            return null;
        }
    }

    public async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var vec = await EmbedAsync(texts[i], ct);
            if (vec is null) return null;
            results[i] = vec;
        }
        return results;
    }

    private static void L2Normalize(float[] v)
    {
        float norm = 0;
        foreach (float f in v) norm += f * f;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record VoyageAiEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("input_type")] string InputType = "document");

internal sealed class VoyageAiEmbedResponse
{
    [JsonPropertyName("data")]
    public VoyageAiEmbeddingData[]? Data { get; set; }
}

internal sealed class VoyageAiEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];
}

[JsonSerializable(typeof(VoyageAiEmbedRequest))]
[JsonSerializable(typeof(VoyageAiEmbedResponse))]
internal partial class VoyageAiJsonContext : JsonSerializerContext;
