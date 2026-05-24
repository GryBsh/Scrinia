using Scrinia.Core.Search;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Second-stage reranker applied after first-stage retrieval. Takes the top-K
/// candidates from <c>FileMemoryStore.SearchAll</c>, optionally re-scores them
/// with a richer signal (token-level similarity, cross-encoder, LLM judge),
/// and returns the reordered list. Score on each <see cref="SearchResult"/>
/// is replaced by the rerank score — callers should not blend with the
/// original first-stage score unless the reranker contract explicitly says so.
///
/// <para>Rerankers are intentionally cheap-per-call because they run online at
/// query time. The cost discipline: O(K) per query where K is the top-K size
/// passed in (typically 50–100), not O(corpus).</para>
/// </summary>
public interface ISearchReranker
{
    /// <summary>
    /// Reorders <paramref name="candidates"/> according to the reranker's score.
    /// Returns the SAME <see cref="SearchResult"/> instances (or copies with updated
    /// <see cref="SearchResult.Score"/>) — never replaces the underlying entry refs,
    /// so downstream dedup/chunk-collapsing logic continues to work unchanged.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken ct);
}
