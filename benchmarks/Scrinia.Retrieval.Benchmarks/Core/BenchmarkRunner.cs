using System.Reflection;
using Scrinia.Core;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Judge;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Generic ingest-query-score loop. Drives an <see cref="IRetriever"/> through a stream
/// of <see cref="BenchmarkQuestion"/>s, scoring each via <see cref="BenchmarkScorer"/>
/// and aggregating into a final <see cref="BenchmarkRunResult"/>.
///
/// The runner is benchmark-agnostic — per-benchmark logic lives in the loader and the
/// runner just drives whatever stream the loader produces. The retriever decides what
/// "ingest" and "reset" mean (per-question for LongMemEval/MemBench/ConvoMem,
/// per-conversation for LoCoMo).
/// </summary>
public sealed class BenchmarkRunner
{
    public const string SessionDateScenario = "session-date-ingest";
    public const int DefaultJudgeContextK = 10;

    private readonly IAgentHarness _agent;
    private readonly AnswerabilityJudge? _judge;

    /// <summary>Default constructor — uses the no-op agent harness (baseline runs).</summary>
    public BenchmarkRunner() : this(NoOpAgentHarness.Instance) { }

    /// <summary>
    /// Constructor accepting a configured agent harness. When the harness is non-null
    /// and any of its ops are enabled, the runner generates anchors per ingest doc
    /// and/or transforms the query before each retrieval. <see cref="NoOpAgentHarness"/>
    /// (the default) is a zero-cost passthrough so the runner code path is identical
    /// for agent-on and agent-off runs.
    /// </summary>
    public BenchmarkRunner(IAgentHarness agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
    }

    /// <summary>
    /// Constructor additionally accepting an <see cref="AnswerabilityJudge"/>. When
    /// non-null, the runner runs the judge as a post-scoring pass on every question,
    /// attaching its verdict to the <see cref="QuestionResult"/> without altering the
    /// R@k/NDCG numbers produced by <see cref="BenchmarkScorer"/>. Null (the other
    /// constructors) skips the judge pass entirely — zero-cost when unused.
    /// </summary>
    public BenchmarkRunner(IAgentHarness agent, AnswerabilityJudge? judge) : this(agent)
    {
        _judge = judge;
    }

    public async Task<BenchmarkRunResult> RunAsync(
        string benchmarkName,
        string modeLabel,
        IAsyncEnumerable<BenchmarkQuestion> questions,
        IRetriever retriever,
        int topK,
        BenchmarkScorer.Scorer scorer,
        IProgress<RunProgress>? progress = null,
        int ingestConcurrency = 1,
        string? granularity = null,
        string? temporalScenario = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeLabel);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(scorer);
        if (ingestConcurrency < 1) ingestConcurrency = 1;

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<QuestionResult>();
        int n = 0;

        await foreach (var q in questions.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();

            if (n > 0)
                await retriever.ResetAsync(ct);

            await IngestHaystackAsync(retriever, q.Haystack, ingestConcurrency, _agent, ct);

            var dateById = q.Haystack.ToDictionary(d => d.Id, d => d.Timestamp, StringComparer.OrdinalIgnoreCase);

            string queryText = await TransformQueryAsync(q.QueryText, ct);
            var hits = await retriever.RetrieveAsync(queryText, topK, ct);
            var scored = scorer(q, hits);

            int contextK = _judge?.JudgeK ?? DefaultJudgeContextK;
            var judgeContext = await BuildAnswererContextAsync(retriever, queryText, hits, dateById, contextK, ct);
            scored = scored with
            {
                GoldAnswer = q.GoldAnswer,
                JudgeContext = judgeContext,
                DateSurfaced = AnyDateSurfaced(judgeContext),
            };

            if (_judge is not null)
            {
                var verdict = await _judge.JudgeAsync(q.Id, q.QueryText, q.GoldAnswer, judgeContext, ct);
                if (verdict is not null)
                    scored = scored with
                    {
                        SurfacedFromContext = verdict.SurfacedFromContext,
                        AnsweredCorrectly = verdict.AnsweredCorrectly,
                        JudgeAnswer = verdict.Answer,
                        JudgeGraderVerdict = verdict.GraderVerdict,
                    };
            }
            results.Add(scored);
            n++;
            progress?.Report(new RunProgress(n, scored));
        }

        var aggregate = BenchmarkScorer.Aggregate(results);
        var perCategory = BenchmarkScorer.PerCategory(results);
        var meta = new RunMetadata(
            Benchmark: benchmarkName,
            Mode: modeLabel,
            TopK: topK,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            QuestionCount: results.Count,
            ScriniaVersion: ReadScriniaVersion(),
            Granularity: granularity,
            TemporalScenario: temporalScenario);

        return new BenchmarkRunResult(meta, aggregate, perCategory, results);
    }

    /// <summary>
    /// Variant for benchmarks where multiple questions share a haystack (LoCoMo, where
    /// each conversation hosts ~200 QA pairs). Ingests each group's haystack once and
    /// runs every question against it before resetting for the next group — vs the
    /// default <see cref="RunAsync"/> which ingests a fresh haystack per question.
    /// </summary>
    public async Task<BenchmarkRunResult> RunGroupedAsync(
        string benchmarkName,
        string modeLabel,
        IAsyncEnumerable<BenchmarkQuestionGroup> groups,
        IRetriever retriever,
        int topK,
        BenchmarkScorer.Scorer scorer,
        IProgress<RunProgress>? progress = null,
        int ingestConcurrency = 1,
        string? granularity = null,
        string? temporalScenario = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeLabel);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(scorer);
        if (ingestConcurrency < 1) ingestConcurrency = 1;

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<QuestionResult>();
        bool first = true;

        await foreach (var group in groups.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!first) await retriever.ResetAsync(ct);
            first = false;

            await IngestHaystackAsync(retriever, group.SharedHaystack, ingestConcurrency, _agent, ct);

            var dateById = group.SharedHaystack.ToDictionary(d => d.Id, d => d.Timestamp, StringComparer.OrdinalIgnoreCase);

            foreach (var q in group.Questions)
            {
                ct.ThrowIfCancellationRequested();
                string queryText = await TransformQueryAsync(q.QueryText, ct);
                var hits = await retriever.RetrieveAsync(queryText, topK, ct);
                var scored = scorer(q, hits);

                int contextK = _judge?.JudgeK ?? DefaultJudgeContextK;
                var judgeContext = await BuildAnswererContextAsync(retriever, queryText, hits, dateById, contextK, ct);
                scored = scored with
                {
                    GoldAnswer = q.GoldAnswer,
                    JudgeContext = judgeContext,
                    DateSurfaced = AnyDateSurfaced(judgeContext),
                };

                if (_judge is not null)
                {
                    var verdict = await _judge.JudgeAsync(q.Id, q.QueryText, q.GoldAnswer, judgeContext, ct);
                    if (verdict is not null)
                        scored = scored with
                        {
                            SurfacedFromContext = verdict.SurfacedFromContext,
                            AnsweredCorrectly = verdict.AnsweredCorrectly,
                            JudgeAnswer = verdict.Answer,
                            JudgeGraderVerdict = verdict.GraderVerdict,
                        };
                }
                results.Add(scored);
                progress?.Report(new RunProgress(results.Count, scored));
            }
        }

        var aggregate = BenchmarkScorer.Aggregate(results);
        var perCategory = BenchmarkScorer.PerCategory(results);
        var meta = new RunMetadata(
            Benchmark: benchmarkName,
            Mode: modeLabel,
            TopK: topK,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            QuestionCount: results.Count,
            ScriniaVersion: ReadScriniaVersion(),
            Granularity: granularity,
            TemporalScenario: temporalScenario);
        return new BenchmarkRunResult(meta, aggregate, perCategory, results);
    }

    public static IReadOnlyList<string> WithCreatedDates(
        IReadOnlyList<string> hitTexts, IReadOnlyList<RetrievalHit> hits,
        IReadOnlyDictionary<string, DateTimeOffset?> dateById)
    {
        var outp = new List<string>(hitTexts.Count);
        for (int i = 0; i < hitTexts.Count; i++)
        {
            DateTimeOffset? date = i < hits.Count && dateById.TryGetValue(hits[i].Id, out var d) ? d : null;
            outp.Add(AnswererDateFormat.Apply(hitTexts[i], date));
        }
        return outp;
    }

    /// <summary>Deterministic, model-independent: did Scrinia put a resolvable created date in the answerer's context?</summary>
    public static bool AnyDateSurfaced(IReadOnlyList<string> contents)
    {
        foreach (var c in contents) if (AnswererDateFormat.Present(c)) return true;
        return false;
    }

    /// <summary>Assembles the date-prefixed answerer context for one question. Agentic retrievers return
    /// already-date-prefixed surfaced content (Task 5); id-mapped retrievers get their top-k hit texts
    /// date-prefixed here. This is the exact list persisted as JudgeContext and fed to the judge.</summary>
    private static async Task<IReadOnlyList<string>> BuildAnswererContextAsync(
        IRetriever retriever, string query, IReadOnlyList<RetrievalHit> hits,
        IReadOnlyDictionary<string, DateTimeOffset?> dateById, int k, CancellationToken ct)
    {
        if (retriever is ISurfacedContentProvider scp)
            return await scp.GetSurfacedContentsAsync(query, k, ct);
        var top = hits.Take(k).ToList();
        return WithCreatedDates(top.Select(h => h.Text).ToList(), top, dateById);
    }

    /// <summary>
    /// Ingest a question's haystack docs into the retriever, optionally generating
    /// agent-harness anchors per doc first. Sequential when
    /// <paramref name="concurrency"/> is 1 (the default — matches prior behavior).
    /// When greater, uses <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// so multiple ingest calls — and any LLM calls inside them — run concurrently.
    /// The retriever's <c>IngestAsync</c> implementation must be safe for concurrent
    /// invocations; <see cref="ScriniaRetriever"/> serializes its index write internally.
    /// </summary>
    private static async Task IngestHaystackAsync(
        IRetriever retriever,
        IReadOnlyList<HaystackDoc> docs,
        int concurrency,
        IAgentHarness agent,
        CancellationToken ct)
    {
        if (concurrency <= 1)
        {
            foreach (var d in docs)
            {
                var anchors = await agent.GenerateAnchorsAsync(d.Text, ct);
                var typed = await agent.GenerateTypedKeywordsAsync(d.Text, ct);
                await retriever.IngestAsync(d, anchors, typed, ct);
            }
            return;
        }
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct,
        };
        await Parallel.ForEachAsync(docs, options, async (doc, c) =>
        {
            var anchors = await agent.GenerateAnchorsAsync(doc.Text, c);
            var typed = await agent.GenerateTypedKeywordsAsync(doc.Text, c);
            await retriever.IngestAsync(doc, anchors, typed, c);
        });
    }

    /// <summary>
    /// Apply the agent's query-side transforms (rewrite and/or HyDE) before sending
    /// the query to the retriever. Both transforms compose: if rewrite is enabled,
    /// the original query is replaced with the rewritten form; if HyDE is enabled,
    /// the generated hypothetical is appended to whatever query text we end up
    /// using (separated by two newlines so an embedding-based retriever sees both
    /// surfaces as one semantic blob). When neither op is enabled — or both return
    /// null — the original query passes through unchanged.
    /// </summary>
    private async Task<string> TransformQueryAsync(string originalQuery, CancellationToken ct)
    {
        string current = originalQuery;
        string? rewritten = await _agent.RewriteQueryAsync(originalQuery, ct);
        if (!string.IsNullOrWhiteSpace(rewritten))
            current = rewritten;
        string? hypothetical = await _agent.GenerateHypotheticalAsync(originalQuery, ct);
        if (!string.IsNullOrWhiteSpace(hypothetical))
            current = current + "\n\n" + hypothetical;
        return current;
    }

    private static string ReadScriniaVersion() =>
        typeof(FileMemoryStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";
}

/// <summary>Reported via <see cref="IProgress{T}"/> after each question completes.</summary>
public sealed record RunProgress(int Completed, QuestionResult LastResult);
