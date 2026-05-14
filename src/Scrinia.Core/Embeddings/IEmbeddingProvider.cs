namespace Scrinia.Core.Embeddings;

/// <summary>Abstraction over embedding generation backends.</summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>Whether the provider is initialized and ready to embed.</summary>
    bool IsAvailable { get; }

    /// <summary>Dimensionality of the embedding vectors produced.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Stable identifier for this provider+model combination, used by <c>VectorStore</c> to
    /// detect that a workspace's vectors were built with a different model than the one
    /// currently active. Format is <c>{kind}:{model}</c> (e.g. <c>ollama:nomic-embed-text</c>,
    /// <c>model2vec:m2v-MiniLM-L6-v2</c>). Must be stable from construction onward — changing
    /// it mid-lifetime would false-trigger reindex.
    /// </summary>
    string Signature { get; }

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
