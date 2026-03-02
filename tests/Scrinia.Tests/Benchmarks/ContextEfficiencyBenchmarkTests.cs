using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Measures token efficiency — how many tokens each system consumes per query.
/// Expected: Scrinia &lt;&lt; Auto &lt; Flat-file at 100+ facts.
/// </summary>
public sealed class ContextEfficiencyBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TokensPerQuery_100Facts_CompareAllSystems()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        // Pick 10 queries spread across topics
        var queries = corpus.Where((_, i) => i % 10 == 0).Take(10).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaTotal = 0, flatTotal = 0, autoTotal = 0;

        var rows = new List<string[]>();
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

            rows.Add([
                fact.Key,
                $"{sr.TokensConsumed:N0}",
                $"{fr.TokensConsumed:N0}",
                $"{ar.TokensConsumed:N0}",
            ]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            "Tokens Per Query (100-fact corpus, 10 queries)",
            ["Query", "Scrinia", "Flat-file", "Auto"],
            rows);

        double scriniaAvg = scriniaTotal / (double)queries.Count;
        double flatAvg = flatTotal / (double)queries.Count;
        double autoAvg = autoTotal / (double)queries.Count;

        output.WriteLine("");
        output.WriteLine($"Averages: Scrinia={scriniaAvg:N0}  Flat-file={flatAvg:N0}  Auto={autoAvg:N0}");

        string winner = scriniaAvg <= autoAvg && scriniaAvg <= flatAvg ? "Scrinia"
            : autoAvg <= flatAvg ? "Auto" : "Flat-file";
        BenchmarkReporter.WriteVerdict(output, "Token efficiency @ 100 facts", winner,
            $"avg tokens per query: Scrinia={scriniaAvg:N0}, Auto={autoAvg:N0}, Flat-file={flatAvg:N0}");

        // At 100 facts, Scrinia should be more efficient than flat-file
        scriniaAvg.Should().BeLessThan(flatAvg, "Scrinia should use fewer tokens than flat-file at 100 facts");
    }

    [Fact]
    public async Task CumulativeTokens_10Queries_ShowsGrowth()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 10 == 0).Take(10).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        var rows = new List<string[]>();
        for (int i = 0; i < queries.Count; i++)
        {
            await scrinia.QueryAsync(queries[i].Question, queries[i].Key);
            await flat.QueryAsync(queries[i].Question, queries[i].Key);
            await auto.QueryAsync(queries[i].Question, queries[i].Key);

            rows.Add([
                $"After Q{i + 1}",
                $"{scrinia.TokensConsumed:N0}",
                $"{flat.TokensConsumed:N0}",
                $"{auto.TokensConsumed:N0}",
            ]);
        }

        BenchmarkReporter.WriteComparisonTable(output,
            "Cumulative Tokens After Each Query (100-fact corpus)",
            ["Point", "Scrinia", "Flat-file", "Auto"],
            rows);

        // Flat-file cumulative should grow linearly (full corpus each time)
        flat.TokensConsumed.Should().BeGreaterThan(scrinia.TokensConsumed,
            "flat-file cumulative cost should exceed Scrinia");
    }

    [Fact]
    public async Task SelectivityRatio_SingleTopicQuery()
    {
        var corpus = BenchmarkCorpus.Generate(100);

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        // Query specifically about one topic
        var apiQuery = corpus.First(f => f.Topic == "api");

        scrinia.ResetBudget();
        flat.ResetBudget();
        auto.ResetBudget();

        var sr = await scrinia.QueryAsync(apiQuery.Question, apiQuery.Key);
        var fr = await flat.QueryAsync(apiQuery.Question, apiQuery.Key);
        var ar = await auto.QueryAsync(apiQuery.Question, apiQuery.Key);

        int totalCorpusFlat = flat.GetTotalCorpusTokens();

        double scriniaSelectivity = totalCorpusFlat > 0 ? 1.0 - (sr.TokensConsumed / (double)totalCorpusFlat) : 0;
        double flatSelectivity = 0; // Flat file always loads 100%
        double autoSelectivity = totalCorpusFlat > 0 ? 1.0 - (ar.TokensConsumed / (double)totalCorpusFlat) : 0;

        BenchmarkReporter.WriteComparisonTable(output,
            "Selectivity Ratio (single-topic query, 100 facts)",
            ["System", "Tokens Used", "Total Corpus", "Selectivity"],
            [
                ["Scrinia",   $"{sr.TokensConsumed:N0}", $"{totalCorpusFlat:N0}", $"{scriniaSelectivity:P1}"],
                ["Flat-file", $"{fr.TokensConsumed:N0}", $"{totalCorpusFlat:N0}", $"{flatSelectivity:P1}"],
                ["Auto",      $"{ar.TokensConsumed:N0}", $"{totalCorpusFlat:N0}", $"{autoSelectivity:P1}"],
            ]);

        // Scrinia should have high selectivity (loads small fraction of corpus)
        scriniaSelectivity.Should().BeGreaterThan(0.5, "Scrinia should load less than half the corpus for a single-topic query");
    }
}
