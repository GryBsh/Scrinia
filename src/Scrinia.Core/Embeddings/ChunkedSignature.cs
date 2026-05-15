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
}
