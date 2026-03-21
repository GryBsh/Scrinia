namespace Scrinia.Core.Embeddings;

/// <summary>Abstraction over embedding generation backends.</summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>Whether the provider is initialized and ready to embed.</summary>
    bool IsAvailable { get; }

    /// <summary>Dimensionality of the embedding vectors produced.</summary>
    int Dimensions { get; }

    /// <summary>Generate an embedding for a single text input.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Generate embeddings for multiple texts in a batch.</summary>
    async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
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
}
