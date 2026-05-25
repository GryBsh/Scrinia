namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Abstracts the retrieval call so we can swap implementations (BM25-only,
/// BM25+embeddings, +recency, +MMR) without touching the runner or scorer.
/// </summary>
public interface IRetriever : IAsyncDisposable
{
    /// <summary>
    /// Ingests one haystack document into the underlying store. Called once per
    /// haystack doc before any queries are issued for the current question.
    /// </summary>
    Task IngestAsync(HaystackDoc doc, CancellationToken ct = default);

    /// <summary>
    /// Ingest overload that accepts agent-harness-supplied retrieval anchors. The
    /// default implementation forwards to <see cref="IngestAsync(HaystackDoc, CancellationToken)"/>
    /// without using the anchors — implementations that participate in the agent
    /// harness (currently <c>ScriniaRetriever</c>) override this to wire anchors
    /// into <c>ArtifactEntry.RetrievalAnchors</c>. Null/empty anchors are
    /// equivalent to the non-anchor overload.
    /// </summary>
    Task IngestAsync(HaystackDoc doc, IReadOnlyList<string>? anchors, CancellationToken ct = default) =>
        IngestAsync(doc, ct);

    /// <summary>
    /// Returns up to <paramref name="topK"/> ranked hits for <paramref name="query"/>.
    /// </summary>
    Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default);

    /// <summary>
    /// Resets the underlying store for the next question. Called between questions
    /// in per-question-haystack benchmarks (LongMemEval, ConvoMem, MemBench).
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}

/// <summary>
/// One hit returned by an <see cref="IRetriever"/>. <see cref="Id"/> is the haystack
/// doc's ID (matched against ground truth for ID-match benchmarks). <see cref="Text"/>
/// carries the original doc text for substring-match benchmarks (ConvoMem).
/// </summary>
public sealed record RetrievalHit(
    string Id,
    string Text,
    double Score);
