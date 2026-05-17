using System.Reflection;
using Scrinia.Core;

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
    public async Task<BenchmarkRunResult> RunAsync(
        string benchmarkName,
        string modeLabel,
        IAsyncEnumerable<BenchmarkQuestion> questions,
        IRetriever retriever,
        int topK,
        IProgress<RunProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeLabel);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(retriever);

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<QuestionResult>();
        int n = 0;

        await foreach (var q in questions.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();

            if (n > 0)
                await retriever.ResetAsync(ct);

            foreach (var d in q.Haystack)
                await retriever.IngestAsync(d, ct);

            var hits = await retriever.RetrieveAsync(q.QueryText, topK, ct);
            var scored = BenchmarkScorer.ScoreQuestion(q, hits);
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
            ScriniaVersion: ReadScriniaVersion());

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
        IProgress<RunProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeLabel);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(retriever);

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<QuestionResult>();
        bool first = true;

        await foreach (var group in groups.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!first) await retriever.ResetAsync(ct);
            first = false;

            foreach (var doc in group.SharedHaystack)
                await retriever.IngestAsync(doc, ct);

            foreach (var q in group.Questions)
            {
                ct.ThrowIfCancellationRequested();
                var hits = await retriever.RetrieveAsync(q.QueryText, topK, ct);
                var scored = BenchmarkScorer.ScoreQuestion(q, hits);
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
            ScriniaVersion: ReadScriniaVersion());
        return new BenchmarkRunResult(meta, aggregate, perCategory, results);
    }

    private static string ReadScriniaVersion() =>
        typeof(FileMemoryStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";
}

/// <summary>Reported via <see cref="IProgress{T}"/> after each question completes.</summary>
public sealed record RunProgress(int Completed, QuestionResult LastResult);
