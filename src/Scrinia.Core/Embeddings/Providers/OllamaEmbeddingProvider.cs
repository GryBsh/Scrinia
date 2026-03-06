using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using Ollama's HTTP API.</summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;
    private int _dimensions;

    public bool IsAvailable => _dimensions > 0;
    public int Dimensions => _dimensions;

    public OllamaEmbeddingProvider(string baseUrl, string model, ILogger logger)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _logger = logger;
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var request = new OllamaEmbedRequest(_model, text);
            var response = await _http.PostAsJsonAsync("api/embed", request, OllamaJsonContext.Default.OllamaEmbedRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaEmbedResponse, ct);
            if (result?.Embeddings is { Length: > 0 })
            {
                var vec = result.Embeddings[0];
                if (_dimensions == 0)
                    _dimensions = vec.Length;
                L2Normalize(vec);
                return vec;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama embedding failed");
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
