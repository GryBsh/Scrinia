using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Judge;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Dataset-free judge replay. Task 4 persists the exact date-prefixed answerer context
/// (<see cref="QuestionResult.JudgeContext"/>) and <see cref="QuestionResult.GoldAnswer"/>
/// on every question of every run, so re-judging never needs the original dataset, the
/// retriever, or any reconstruction — it re-plays the judge over exactly what the live
/// run fed it, for exact live/replay parity. Runs produced before that persistence
/// existed carry no <see cref="QuestionResult.JudgeContext"/>; those questions are left
/// untouched and the skip count is logged (by design — pre-plan files are not
/// replay-ready).
/// </summary>
internal static class JudgeReplayCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? inputPath = Program.Flag(args, "--input");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Console.Error.WriteLine("[scrinia-bench:judge-replay] --input <result.json> is required.");
            return 1;
        }

        // Replay's entire purpose is re-running the LLM judge, so a judge configuration
        // is mandatory here (unlike the live-run commands, where --judge is optional).
        var judge = await RetrieverFactory.BuildJudgeAsync(args, ct);
        if (judge is null)
        {
            Console.Error.WriteLine(
                "[scrinia-bench:judge-replay] --judge is required (with --judge-provider/--judge-model/...); " +
                "judge-replay re-runs only the LLM judge, so a judge configuration is mandatory.");
            return 1;
        }

        BenchmarkRunResult input;
        await using (var readStream = File.OpenRead(inputPath))
        {
            input = await JsonSerializer.DeserializeAsync<BenchmarkRunResult>(readStream, Program.JsonOutputOptions, ct)
                ?? throw new InvalidOperationException(
                    $"[scrinia-bench:judge-replay] failed to deserialize '{inputPath}': empty or invalid JSON.");
        }

        var outp = await ReplayAsync(input, judge, ct);

        string judgedPath = JudgedPath(inputPath);
        await using (var writeStream = File.Create(judgedPath))
        {
            await JsonSerializer.SerializeAsync(writeStream, outp, Program.JsonOutputOptions, ct);
        }
        Console.WriteLine($"[scrinia-bench:judge-replay] wrote {judgedPath}");
        return 0;
    }

    /// <summary>
    /// Re-judges every question whose <see cref="QuestionResult.JudgeContext"/> and
    /// <see cref="QuestionResult.GoldAnswer"/> survived from the original run; questions
    /// missing either (pre-Task-4 runs, or genuinely gold-less questions) are skipped —
    /// left byte-for-byte as-is — and counted. Recomputes <see
    /// cref="BenchmarkRunResult.Aggregate"/>/<see cref="BenchmarkRunResult.PerCategory"/>
    /// over the resulting set and stamps <see cref="RunMetadata.TemporalScenario"/> to
    /// <see cref="BenchmarkRunner.SessionDateScenario"/> (replay always re-judges the
    /// session-date-ingest context), while keeping the original run's <see
    /// cref="RunMetadata.Granularity"/> untouched.
    /// </summary>
    public static async Task<BenchmarkRunResult> ReplayAsync(
        BenchmarkRunResult input, AnswerabilityJudge judge, CancellationToken ct)
    {
        int skipped = 0;
        var updated = new List<QuestionResult>(input.Questions.Count);
        foreach (var q in input.Questions)
        {
            if (q.JudgeContext is not { Count: > 0 } || string.IsNullOrWhiteSpace(q.GoldAnswer))
            {
                skipped++;
                updated.Add(q);
                continue;
            }

            var verdict = await judge.JudgeAsync(q.QuestionId, q.Query, q.GoldAnswer, q.JudgeContext, ct);
            updated.Add(verdict is null
                ? q
                : q with
                {
                    SurfacedFromContext = verdict.SurfacedFromContext,
                    AnsweredCorrectly = verdict.AnsweredCorrectly,
                    JudgeAnswer = verdict.Answer,
                    JudgeGraderVerdict = verdict.GraderVerdict,
                    DateSurfaced = BenchmarkRunner.AnyDateSurfaced(q.JudgeContext),
                });
        }

        if (skipped > 0)
            Console.Error.WriteLine($"[scrinia-bench:judge-replay] skipped {skipped}/{input.Questions.Count} question(s) with no persisted JudgeContext (pre-plan runs are not replay-ready).");

        var aggregate = BenchmarkScorer.Aggregate(updated);
        var perCategory = BenchmarkScorer.PerCategory(updated);
        var meta = input.RunMetadata with { TemporalScenario = BenchmarkRunner.SessionDateScenario };
        return new BenchmarkRunResult(meta, aggregate, perCategory, updated);
    }

    /// <summary>Sibling output path: <c>foo.json</c> → <c>foo.judged.json</c>; anything
    /// without a <c>.json</c> extension just gets <c>.judged.json</c> appended.</summary>
    private static string JudgedPath(string inputPath) =>
        inputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? inputPath[..^".json".Length] + ".judged.json"
            : inputPath + ".judged.json";
}
