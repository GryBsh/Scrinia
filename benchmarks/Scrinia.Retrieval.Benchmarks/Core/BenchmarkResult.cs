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
    string ScriniaVersion,
    // Tier 2 enrichment metadata. All five fields are null when --enrich none (the
    // default) — preserves compat with non-Tier-2 result files. When set, they
    // record which provider/model/ops were active and the cache effectiveness,
    // so a Tier 2 result file can't be silently compared against a non-Tier-2
    // baseline (the consumer sees the populated fields and knows).
    string? EnrichmentProvider = null,
    string? EnrichmentModel = null,
    string? EnrichmentOps = null,
    int? EnrichmentCacheHits = null,
    int? EnrichmentCacheMisses = null,
    // Singleflight diagnostic: how many parallel callers collapsed into a still-
    // running LLM task and shared its result. Distinct from CacheHits (disk-NDJSON
    // reads) — InflightHits never read NDJSON but also never triggered an LLM
    // call. Should equal "LLM calls saved by --ingest-concurrency stampede
    // suppression" on cold-cache parallel runs; near-zero on warm or sequential runs.
    int? EnrichmentInflightHits = null);

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
