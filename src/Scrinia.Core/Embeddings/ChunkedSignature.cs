namespace Scrinia.Core.Embeddings;

/// <summary>
/// Composes the chunking parameters into the embedding signature stored in SVF3 vector files.
/// When the user changes <c>Scrinia:Embeddings:ChunkSize</c> or <c>ChunkOverlap</c>, the
/// composed signature changes too — so <see cref="VectorStore"/>'s signature-mismatch path
/// quarantines the old vectors and triggers a reindex, the same flow that handles a model
/// switch. Keeps chunk-config drift from silently producing inconsistent recall.
/// </summary>
public static class ChunkedSignature
{
    /// <summary>
    /// Compose a chunked signature: <c>{providerSignature}|c{chunkSize}o{overlap}</c>.
    /// Example: <c>ollama:nomic-embed-text|c1200o200</c>.
    /// </summary>
    public static string Compose(string providerSignature, int chunkSize, int overlap) =>
        $"{providerSignature}|c{chunkSize}o{overlap}";

    /// <summary>
    /// The signature a live-serving store should expect for vectors produced by
    /// <paramref name="provider"/>, or <c>null</c> when the provider is unavailable. A store built
    /// with a null signature disables quarantine-on-mismatch, so a transient provider-load failure
    /// never quarantines good vectors. Uses the same <see cref="Compose"/> as the reindexer, so a
    /// reindex with the same provider + options reproduces a byte-identical signature.
    /// </summary>
    public static string? ForProvider(IEmbeddingProvider provider, EmbeddingOptions options)
        => provider.IsAvailable
            ? Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap)
            : null;
}
