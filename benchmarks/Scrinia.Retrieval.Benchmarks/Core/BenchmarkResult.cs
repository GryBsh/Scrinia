namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Top-level result for a benchmark run. JSON shape intentionally mirrors MemPalace's
/// per-question result files so a separate compare tool can diff per-question hits
/// across systems without a schema translator.
/// </summary>
public sealed record BenchmarkRunResult(
    RunMetadata RunMetadata,
    AggregateMetrics Aggregate,
    IReadOnlyDictionary<string, AggregateMetrics> PerCategory,
    IReadOnlyList<QuestionResult> Questions);

public sealed record RunMetadata(
    string Benchmark,
    string Mode,
    int TopK,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int QuestionCount,
    string ScriniaVersion);

/// <summary>
/// <see cref="SurfacedRate"/> and <see cref="CorrectRate"/> are both means over judged
/// questions only (see <see cref="BenchmarkScorer.Aggregate"/>) — <see cref="SurfacedRate"/>
/// is the fraction where the answerer produced an answer instead of the not-answerable
/// sentinel; <see cref="CorrectRate"/> is the fraction the grader judged correct.
/// </summary>
public sealed record AggregateMetrics(
    int QuestionCount,
    double RecallAt5,
    double RecallAt10,
    double RecallAt50,
    double Ndcg10,
    double SurfacedRate = 0.0,
    double CorrectRate = 0.0);

/// <summary>
/// Per-question score. <c>RecallAt{5,10,50}</c> is a double in <c>[0, 1]</c> rather
/// than a boolean because some benchmarks (ConvoMem, LoCoMo) report fractional recall
/// — matched_evidence / total_evidence — while others (MemBench, LongMemEval) report
/// binary any-hit recall (1.0 if any gold appears in top-K, 0.0 otherwise). The
/// scorer used by each benchmark determines which semantic produces these numbers;
/// see <see cref="BenchmarkScorer"/> for the per-benchmark variants.
///
/// <para><see cref="SurfacedFromContext"/> is whether the judge's answerer LLM produced
/// an answer (vs. the not-answerable sentinel) from the retrieved content; <see
/// cref="AnsweredCorrectly"/> is whether the judge's grader LLM judged that answer as
/// matching the gold answer. Both are null when the question wasn't judged.
/// <see cref="DateSurfaced"/>, <see cref="GoldAnswer"/>, and <see cref="JudgeContext"/>
/// are persistence fields populated by later work.</para>
/// </summary>
public sealed record QuestionResult(
    string QuestionId,
    string Query,
    string Category,
    IReadOnlyList<string> GroundTruthIds,
    IReadOnlyList<string> GroundTruthTexts,
    IReadOnlyList<RetrievalHit> Retrieved,
    double RecallAt5,
    double RecallAt10,
    double RecallAt50,
    double Ndcg10,
    bool? SurfacedFromContext = null,
    bool? AnsweredCorrectly = null,
    bool? DateSurfaced = null,
    string? GoldAnswer = null,
    IReadOnlyList<string>? JudgeContext = null,
    string? JudgeAnswer = null,
    string? JudgeGraderVerdict = null);
