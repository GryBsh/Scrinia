using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings.Providers;

/// <summary>Embedding provider using the OpenAI embeddings API.</summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private int _dimensions;

    public bool IsAvailable => true; // Available as long as API key is set
    public int Dimensions => _dimensions > 0 ? _dimensions : 1536;

    public OpenAiEmbeddingProvider(string? apiKey, string model, string baseUrl, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key is required for the OpenAI embedding provider.", nameof(apiKey));

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
            var request = new OpenAiEmbedRequest(_model, text);
            var response = await _http.PostAsJsonAsync("embeddings", request, OpenAiJsonContext.Default.OpenAiEmbedRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(OpenAiJsonContext.Default.OpenAiEmbedResponse, ct);
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
            _logger.LogWarning(ex, "OpenAI embedding failed");
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
