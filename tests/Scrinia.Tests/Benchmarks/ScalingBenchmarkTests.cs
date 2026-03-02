using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Measures how token consumption scales with corpus size.
/// Expected: Flat-file linear, Scrinia near-constant, Auto sublinear.
/// </summary>
public sealed class ScalingBenchmarkTests(ITestOutputHelper output)
{
    private static readonly int[] ScalePoints = [10, 50, 100, 500];

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task TokensPerQuery_ScalesWithCorpusSize(int factCount)
    {
        var corpus = BenchmarkCorpus.Generate(factCount);
        // Use first 5 facts as queries (consistent across scales)
        var queries = corpus.Take(5).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaTotal = 0, flatTotal = 0, autoTotal = 0;

        foreach (var fact in queries)
        {
            scrinia.ResetBudget();
            flat.ResetBudget();
            auto.ResetBudget();

            var sr = await scrinia.QueryAsync(fact.Question, fact.Key);
            var fr = await flat.QueryAsync(fact.Question, fact.Key);
            var ar = await auto.QueryAsync(fact.Question, fact.Key);

            scriniaTotal += sr.TokensConsumed;
            flatTotal += fr.TokensConsumed;
            autoTotal += ar.TokensConsumed;
        }

        double scriniaAvg = scriniaTotal / (double)queries.Count;
        double flatAvg = flatTotal / (double)queries.Count;
        double autoAvg = autoTotal / (double)queries.Count;

        output.WriteLine($"Scale point: {factCount} facts");
        output.WriteLine($"  Avg tokens/query — Scrinia: {scriniaAvg:N0}  Flat-file: {flatAvg:N0}  Auto: {autoAvg:N0}");
        output.WriteLine($"  Ratio vs Flat — Scrinia: {scriniaAvg / flatAvg:P1}  Auto: {autoAvg / flatAvg:P1}");

        // Scrinia should always be cheaper than flat-file (even at 10 facts, at worst equal)
        if (factCount >= 50)
            scriniaAvg.Should().BeLessThan(flatAvg, $"Scrinia should be cheaper than flat-file at {factCount} facts");
    }

    [Fact]
    public async Task ScalingSummary_AllPoints()
    {
        var scriniaValues = new double[ScalePoints.Length];
        var flatValues = new double[ScalePoints.Length];
        var autoValues = new double[ScalePoints.Length];

        for (int s = 0; s < ScalePoints.Length; s++)
        {
            int factCount = ScalePoints[s];
            var corpus = BenchmarkCorpus.Generate(factCount);
            var queries = corpus.Take(5).ToList();

            await using var scrinia = new ScriniaMemorySystem();
            await scrinia.SetupAsync(corpus);

            await using var flat = new FlatFileMemorySystem();
            await flat.SetupAsync(corpus);

            await using var auto = new AutoMemorySystem();
            await auto.SetupAsync(corpus);

            int scriniaTotal = 0, flatTotal = 0, autoTotal = 0;
            foreach (var fact in queries)
            {
                scrinia.ResetBudget();
                flat.ResetBudget();
                auto.ResetBudget();

                var sr = await scrinia.QueryAsync(fact.Question, fact.Key);
                var fr = await flat.QueryAsync(fact.Question, fact.Key);
                var ar = await auto.QueryAsync(fact.Question, fact.Key);

                scriniaTotal += sr.TokensConsumed;
                flatTotal += fr.TokensConsumed;
                autoTotal += ar.TokensConsumed;
            }

            scriniaValues[s] = scriniaTotal / (double)queries.Count;
            flatValues[s] = flatTotal / (double)queries.Count;
            autoValues[s] = autoTotal / (double)queries.Count;
        }

        BenchmarkReporter.WriteScalingTable(output,
            "Avg Tokens Per Query — Scaling Curve",
            "facts",
            ScalePoints,
            new Dictionary<string, double[]>
            {
                ["Scrinia"] = scriniaValues,
                ["Flat-file"] = flatValues,
                ["Auto"] = autoValues,
            });

        // Compute growth rates: (value at 500) / (value at 10)
        double scriniaGrowth = scriniaValues[0] > 0 ? scriniaValues[^1] / scriniaValues[0] : 0;
        double flatGrowth = flatValues[0] > 0 ? flatValues[^1] / flatValues[0] : 0;
        double autoGrowth = autoValues[0] > 0 ? autoValues[^1] / autoValues[0] : 0;

        output.WriteLine("");
        output.WriteLine("Growth rate (500-fact / 10-fact):");
        output.WriteLine($"  Scrinia:   {scriniaGrowth:F1}x");
        output.WriteLine($"  Flat-file: {flatGrowth:F1}x");
        output.WriteLine($"  Auto:      {autoGrowth:F1}x");

        BenchmarkReporter.WriteVerdict(output, "Scaling",
            scriniaGrowth <= autoGrowth && scriniaGrowth <= flatGrowth ? "Scrinia"
            : autoGrowth <= flatGrowth ? "Auto" : "Flat-file",
            $"Growth factor 10→500: Scrinia={scriniaGrowth:F1}x, Flat-file={flatGrowth:F1}x, Auto={autoGrowth:F1}x");

        // Flat-file should grow ~50x (linear), Scrinia should grow much less
        flatGrowth.Should().BeGreaterThan(10, "flat-file should show roughly linear growth");
        scriniaGrowth.Should().BeLessThan(flatGrowth, "Scrinia should scale better than flat-file");
    }
}
