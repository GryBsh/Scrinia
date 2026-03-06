using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>Embedding provider using the Google Gemini embedContent API.</summary>
public sealed class GoogleGeminiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _requestUrl;
    private readonly int _configuredDimensions;
    private readonly ILogger _logger;
    private int _dimensions;

    public bool IsAvailable => true;
    public int Dimensions => _dimensions > 0 ? _dimensions : (_configuredDimensions > 0 ? _configuredDimensions : 3072);

    public GoogleGeminiEmbeddingProvider(string? apiKey, string model, string baseUrl, int dimensions, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Google API key is required for the Google Gemini embedding provider.", nameof(apiKey));

        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _model = model;
        _configuredDimensions = dimensions;
        _logger = logger;

        var url = baseUrl.TrimEnd('/');
        _requestUrl = $"{url}/v1beta/models/{model}:embedContent?key={apiKey}";
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var request = new GeminiEmbedRequest(
                new GeminiContent([new GeminiPart(text)]),
                "RETRIEVAL_DOCUMENT",
                _configuredDimensions > 0 ? _configuredDimensions : null);
            var response = await _http.PostAsJsonAsync(_requestUrl, request, GeminiJsonContext.Default.GeminiEmbedRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync(GeminiJsonContext.Default.GeminiEmbedResponse, ct);
            if (result?.Embedding?.Values is { Length: > 0 })
            {
                var vec = result.Embedding.Values;
                if (_dimensions == 0)
                    _dimensions = vec.Length;
                L2Normalize(vec);
                return vec;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Gemini embedding failed");
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
