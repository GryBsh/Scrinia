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

public sealed record AggregateMetrics(
    int QuestionCount,
    double RecallAt5,
    double RecallAt10,
    double RecallAt50,
    double Ndcg10);

public sealed record QuestionResult(
    string QuestionId,
    string Query,
    string Category,
    IReadOnlyList<string> GroundTruthIds,
    IReadOnlyList<string> GroundTruthTexts,
    IReadOnlyList<RetrievalHit> Retrieved,
    bool HitAt5,
    bool HitAt10,
    bool HitAt50,
    double Ndcg10);
