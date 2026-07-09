namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// A retriever that can surface "what the store returns for a query" as raw content
/// strings for the LLM judge, independent of the R@k doc-id mapping. The agentic-ingest
/// arm implements this so the judge sees the matching memories' content even when their
/// citations don't resolve to a gold doc id (R@k stays a provenance-gated lower bound).
/// Retrievers that don't implement it fall back to their RetrieveAsync hit texts.
/// </summary>
public interface ISurfacedContentProvider
{
    Task<IReadOnlyList<string>> GetSurfacedContentsAsync(
        string query, int k, CancellationToken ct = default);
}
