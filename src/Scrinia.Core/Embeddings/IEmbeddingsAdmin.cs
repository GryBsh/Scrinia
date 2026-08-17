namespace Scrinia.Core.Embeddings;

/// <summary>
/// What the embeddings REST endpoints need, independent of which implementation provides it.
/// Implemented by the server's built-in Model2Vec service and by the Vulkan plugin, so one
/// endpoint file serves both and the plugin no longer maps a second copy of the same routes.
///
/// <para>Finding H6: the previous arrangement had each implementation map its own routes into
/// an inline /api/v1/plugins group that carried no {store} route value, so no request could
/// ever resolve a store and every reindex returned 400.</para>
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

    /// <summary>Free-text hardware label surfaced in the web UI, e.g. "CPU" or "Vulkan GPU".</summary>
    string Hardware { get; }

    /// <summary>Re-embeds every persistent memory in the store. Returns the number reindexed.</summary>
    Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct);
}
