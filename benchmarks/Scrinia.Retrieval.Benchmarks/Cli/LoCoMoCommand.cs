using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Wires the <c>locomo</c> subcommand. Per-conversation haystack reuse: each of the
/// 10 conversations is ingested once and queried by all its ~200 QA pairs before
/// the retriever resets. Reports R@5, R@10, R@50 side-by-side because MemPalace's
/// 100% R@50 claim was structurally guaranteed (K &gt; session count) under an LLM
/// rerank — we want to see for ourselves whether the gap matters without rerank.
/// </summary>
internal static class LoCoMoCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? dataPath = Program.Flag(args, "--data");
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            Console.Error.WriteLine("error: --data <locomo10.json> is required");
            return 1;
        }

        var granularity = ParseGranularity(Program.Flag(args, "--granularity") ?? "session");
        int topK = Program.FlagInt(args, "--top-k", 50);
        var (retriever, mode) = await RetrieverFactory.BuildAsync(args, "locomo", ct);
        string output = Program.Flag(args, "--output")
            ?? DefaultOutputPath("locomo", RetrieverFactory.ModeLabel(mode, args));

        Console.WriteLine($"locomo: data={dataPath}, granularity={granularity}, mode={RetrieverFactory.ModeLabel(mode, args)}, top-k={topK}");

        var agent = await RetrieverFactory.BuildAgentAsync(args, ct);
        var loader = new LoCoMoLoader(dataPath, granularity);
        await using (retriever)
        {
            var runner = new BenchmarkRunner(agent);
            var progress = new Progress<RunProgress>(p =>
            {
                if (p.Completed % 100 == 0 || p.Completed == 1)
                    Console.WriteLine($"  q{p.Completed:D4} [{p.LastResult.Category}] r@5={p.LastResult.RecallAt5:F2} r@10={p.LastResult.RecallAt10:F2} r@50={p.LastResult.RecallAt50:F2}");
            });

            var result = await runner.RunGroupedAsync(
                benchmarkName: "locomo",
                modeLabel: RetrieverFactory.ModeLabel(mode, args),
                groups: CategoryFilter.ApplyGroupedAsync(loader.LoadAsync(ct), Program.Flag(args, "--category"), ct),
                retriever: retriever,
                topK: topK,
                scorer: BenchmarkScorer.ScoreFractionalById,
                progress: progress,
                ingestConcurrency: Program.FlagInt(args, "--ingest-concurrency", 1),
                ct: ct);

            PrintSummary(result);
            if (retriever is AgenticScriniaRetriever ag)
                RetrieverFactory.PrintAgenticStats(ag.Stats);
            await WriteJsonAsync(output, result, ct);
            Console.WriteLine($"wrote {output}");
        }
        return 0;
    }

    private static LoCoMoLoader.Granularity ParseGranularity(string raw) => raw.ToLowerInvariant() switch
    {
        "session" => LoCoMoLoader.Granularity.Session,
        "dialog" => LoCoMoLoader.Granularity.Dialog,
        _ => throw new ArgumentException($"unknown --granularity '{raw}'. Expected: session | dialog"),
    };

    private static string DefaultOutputPath(string benchmark, string mode)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm", Program.CultureInvariant);
        string dir = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "results");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"scrinia_{benchmark}_{mode}_{stamp}.json");
    }

    private static void PrintSummary(BenchmarkRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {result.RunMetadata.Benchmark} / {result.RunMetadata.Mode} ===");
        Console.WriteLine($"questions: {result.Aggregate.QuestionCount}");
        Console.WriteLine($"R@5:   {result.Aggregate.RecallAt5:P1}");
        Console.WriteLine($"R@10:  {result.Aggregate.RecallAt10:P1}");
        Console.WriteLine($"R@50:  {result.Aggregate.RecallAt50:P1}");
        Console.WriteLine($"NDCG@10: {result.Aggregate.Ndcg10:F3}");
        Console.WriteLine();
        Console.WriteLine("per-category R@5 / R@10 / R@50:");
        foreach (var (cat, agg) in result.PerCategory.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {cat,-28} {agg.RecallAt5,7:P1} / {agg.RecallAt10,7:P1} / {agg.RecallAt50,7:P1}  (n={agg.QuestionCount})");
    }

    private static async Task WriteJsonAsync(string path, BenchmarkRunResult result, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, Program.JsonOutputOptions, ct);
    }
}
