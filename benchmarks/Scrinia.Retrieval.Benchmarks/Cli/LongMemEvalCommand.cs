using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Wires the <c>longmemeval</c> subcommand. The headline benchmark — per-question
/// haystacks of ~53 sessions × 500 questions. Honours MemPalace's
/// <c>lme_split_50_450.json</c> via <c>--split full|dev|held_out</c> so the
/// held-out number we publish is on the same 450 questions MemPalace held out.
/// </summary>
internal static class LongMemEvalCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? dataPath = Program.Flag(args, "--data");
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            Console.Error.WriteLine("error: --data <longmemeval_s_cleaned.json> is required");
            return 1;
        }

        var split = ParseSplit(Program.Flag(args, "--split") ?? "full");
        string? splitFile = Program.Flag(args, "--split-file");
        var granularity = ParseGranularity(Program.Flag(args, "--granularity") ?? "session");
        int topK = Program.FlagInt(args, "--top-k", 50);
        string output = Program.Flag(args, "--output")
            ?? DefaultOutputPath("longmemeval", split);

        var (retriever, mode) = await RetrieverFactory.BuildAsync(args, "longmemeval", ct);

        Console.WriteLine($"longmemeval: data={dataPath}, split={split}, granularity={granularity}, mode={RetrieverFactory.ModeLabel(mode, args)}, top-k={topK}");

        var agent = await RetrieverFactory.BuildAgentAsync(args, ct);
        var judge = await RetrieverFactory.BuildJudgeAsync(args, ct);
        var loader = new LongMemEvalLoader(dataPath, split, splitFile, granularity);
        await using (retriever)
        {
            var runner = new BenchmarkRunner(agent, judge);
            var progress = new Progress<RunProgress>(p =>
            {
                if (p.Completed % 25 == 0 || p.Completed == 1)
                    Console.WriteLine($"  q{p.Completed:D4} [{p.LastResult.Category}] r@5={p.LastResult.RecallAt5:F2} r@10={p.LastResult.RecallAt10:F2}");
            });

            var result = await runner.RunAsync(
                benchmarkName: "longmemeval",
                modeLabel: RetrieverFactory.ModeLabel(mode, args) + (split == LongMemEvalLoader.SplitMode.Full ? "" : "_" + split.ToString().ToLowerInvariant()),
                questions: CategoryFilter.ApplyAsync(loader.LoadAsync(ct), Program.Flag(args, "--category"), ct),
                retriever: retriever,
                topK: topK,
                scorer: BenchmarkScorer.ScoreAnyHitById,
                progress: progress,
                ingestConcurrency: Program.FlagInt(args, "--ingest-concurrency", 1),
                ct: ct);

            PrintSummary(result);
            if (judge is not null)
                RetrieverFactory.PrintJudgeSummary(judge, result);
            if (retriever is AgenticScriniaRetriever ag)
                RetrieverFactory.PrintAgenticStats(ag.Stats);
            if (agent is LlmAgentHarness la)
                RetrieverFactory.PrintAgentStats(la.Stats);
            await WriteJsonAsync(output, result, ct);
            Console.WriteLine($"wrote {output}");
        }
        return 0;
    }

    private static LongMemEvalLoader.SplitMode ParseSplit(string raw) => raw.ToLowerInvariant() switch
    {
        "full" => LongMemEvalLoader.SplitMode.Full,
        "dev" => LongMemEvalLoader.SplitMode.Dev,
        "held_out" or "heldout" => LongMemEvalLoader.SplitMode.HeldOut,
        _ => throw new ArgumentException($"unknown --split '{raw}'. Expected: full | dev | held_out"),
    };

    private static LongMemEvalLoader.Granularity ParseGranularity(string raw) => raw.ToLowerInvariant() switch
    {
        "session" => LongMemEvalLoader.Granularity.Session,
        "turn" => LongMemEvalLoader.Granularity.Turn,
        _ => throw new ArgumentException($"unknown --granularity '{raw}'. Expected: session | turn"),
    };

    private static string DefaultOutputPath(string benchmark, LongMemEvalLoader.SplitMode split)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm", Program.CultureInvariant);
        string splitSuffix = split == LongMemEvalLoader.SplitMode.Full ? "full" : split.ToString().ToLowerInvariant();
        string dir = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "results");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"scrinia_{benchmark}_{splitSuffix}_{stamp}.json");
    }

    private static void PrintSummary(BenchmarkRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {result.RunMetadata.Benchmark} / {result.RunMetadata.Mode} ===");
        string lowerBoundSuffix = result.RunMetadata.Mode.Contains("agentic-ingest") ? " (provenance lower bound)" : "";
        Console.WriteLine($"questions: {result.Aggregate.QuestionCount}");
        Console.WriteLine($"R@5:   {result.Aggregate.RecallAt5:P1}{lowerBoundSuffix}");
        Console.WriteLine($"R@10:  {result.Aggregate.RecallAt10:P1}{lowerBoundSuffix}");
        Console.WriteLine($"R@50:  {result.Aggregate.RecallAt50:P1}{lowerBoundSuffix}");
        Console.WriteLine($"NDCG@10: {result.Aggregate.Ndcg10:F3}");
        Console.WriteLine();
        Console.WriteLine("per-question-type R@5 / R@10:");
        foreach (var (cat, agg) in result.PerCategory.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {cat,-32} {agg.RecallAt5,7:P1} / {agg.RecallAt10,7:P1}  (n={agg.QuestionCount})");
    }

    private static async Task WriteJsonAsync(string path, BenchmarkRunResult result, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, Program.JsonOutputOptions, ct);
    }
}
