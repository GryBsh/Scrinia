namespace Scrinia.Core.Embeddings;

/// <summary>
/// Administrative capabilities exposed by an embeddings provider, independent of which
/// implementation provides them. The interface is consumed by the active embeddings integration
/// and keeps provider status and reindexing consistent across implementations.
/// </summary>
public interface IEmbeddingsAdmin
{
    /// <summary>Display name of the active provider, e.g. "Model2VecEmbeddingProvider".</summary>
    string ProviderName { get; }

    /// <summary>Whether the provider can currently embed.</summary>
    bool IsAvailable { get; }

    /// <summary>Vector dimensionality, or 0 when unavailable.</summary>
    int Dimensions { get; }

    /// <summary>Weight applied to cosine similarity in hybrid scoring.</summary>
    double SemanticWeight { get; }

    /// <summary>Total vectors held across all scopes.</summary>
    int TotalVectorCount { get; }

    /// <summary>Free-text hardware label for the active provider, e.g. "CPU" or "Vulkan GPU".</summary>
    string Hardware { get; }

    /// <summary>Re-embeds every persistent memory in the store. Returns the number reindexed.</summary>
    Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct);
}
