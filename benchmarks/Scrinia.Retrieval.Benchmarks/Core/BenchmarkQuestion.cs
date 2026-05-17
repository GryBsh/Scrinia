namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// One question from a retrieval benchmark. Carries the query, the per-question
/// haystack to ingest, and the ground truth used to compute R@K.
///
/// Two ground-truth modes are supported. ID-match benchmarks (LongMemEval, LoCoMo,
/// MemBench) populate <see cref="GroundTruthIds"/>; text-match benchmarks (ConvoMem)
/// populate <see cref="GroundTruthTexts"/> instead. The scorer dispatches on which is
/// non-empty.
/// </summary>
public sealed record BenchmarkQuestion(
    string Id,
    string QueryText,
    IReadOnlyList<HaystackDoc> Haystack,
    IReadOnlyList<string> GroundTruthIds,
    IReadOnlyList<string> GroundTruthTexts,
    string Category);

/// <summary>
/// One document in a benchmark question's haystack. <see cref="Id"/> must be unique
/// within the question and is what the scorer matches against for ID-match benchmarks.
/// </summary>
public sealed record HaystackDoc(
    string Id,
    string Text,
    DateTimeOffset? Timestamp);
