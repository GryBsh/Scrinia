using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>
/// Shared response parsing for the providers that deserialize the canonical OpenAI-compatible
/// embedding shape: a response wrapper with a <c>data</c> array whose first element carries an
/// <c>embedding</c> vector. The wrapper and JSON-context types differ per provider, so the helper
/// is generic over the deserialized wrapper <typeparamref name="TResponse"/> and is handed the
/// first element's embedding through <paramref name="extractEmbedding"/>.
/// </summary>
internal static class EmbeddingResponseParser
{
    public static async Task<float[]?> ParseOpenAICompatibleAsync<TResponse>(
        HttpContent content,
        JsonTypeInfo<TResponse> jsonContext,
        Func<TResponse, float[]?> extractEmbedding,
        CancellationToken ct)
    {
        var result = await content.ReadFromJsonAsync(jsonContext, ct);
        if (result is null)
            return null;
        return extractEmbedding(result) ?? null;
    }
}
