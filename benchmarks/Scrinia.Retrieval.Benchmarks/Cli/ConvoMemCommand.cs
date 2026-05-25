using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Wires the <c>convomem</c> subcommand: parses args, builds the loader + retriever,
/// runs the benchmark, prints a summary, and writes the result JSON.
/// </summary>
internal static class ConvoMemCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? dataRoot = Program.Flag(args, "--data");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            Console.Error.WriteLine("error: --data <dir> is required");
            return 1;
        }

        int? limit = Program.Flag(args, "--limit") is { } limRaw && int.TryParse(limRaw, out int lim) ? lim : 50;
        int topK = Program.FlagInt(args, "--top-k", 50);
        var (retriever, mode) = await RetrieverFactory.BuildAsync(args, "convomem", ct);
        string output = Program.Flag(args, "--output")
            ?? DefaultOutputPath("convomem", RetrieverFactory.ModeLabel(mode, args));

        Console.WriteLine($"convomem: data={dataRoot}, per-cat-limit={limit}, mode={RetrieverFactory.ModeLabel(mode, args)}, top-k={topK}");

        var agent = await RetrieverFactory.BuildAgentAsync(args, ct);
        var loader = new ConvoMemLoader(dataRoot, perCategoryLimit: limit);
        await using (retriever)
        {
            var runner = new BenchmarkRunner(agent);
            var progress = new Progress<RunProgress>(p =>
            {
                if (p.Completed % 25 == 0 || p.Completed == 1)
                    Console.WriteLine($"  q{p.Completed:D4} [{p.LastResult.Category}] hit@5={p.LastResult.HitAt5} hit@10={p.LastResult.HitAt10}");
            });

            var result = await runner.RunAsync(
                benchmarkName: "convomem",
                modeLabel: RetrieverFactory.ModeLabel(mode, args),
                questions: CategoryFilter.ApplyAsync(loader.LoadAsync(ct), Program.Flag(args, "--category"), ct),
                retriever: retriever,
                topK: topK,
                progress: progress,
                ingestConcurrency: Program.FlagInt(args, "--ingest-concurrency", 1),
                ct: ct);

            result = RetrieverFactory.WithEnrichmentMetadata(result, retriever);
            PrintSummary(result);
            await WriteJsonAsync(output, result, ct);
            Console.WriteLine($"wrote {output}");
        }
        return 0;
    }

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
        Console.WriteLine("per-category R@5 / R@10:");
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
