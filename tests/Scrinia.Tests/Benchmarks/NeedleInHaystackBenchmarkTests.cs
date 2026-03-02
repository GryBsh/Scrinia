using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Needle-in-haystack tests: finding facts by their unique fabricated terms
/// in a large corpus. All systems should find exact matches, but Scrinia
/// should do so at much lower token cost.
/// </summary>
public sealed class NeedleInHaystackBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FindNeedle_500Facts_CompareRecall()
    {
        var corpus = BenchmarkCorpus.Generate(500);
        // Pick 20 needles spread across the corpus
        var needles = corpus.Where((_, i) => i % 25 == 0).Take(20).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaFound = 0, flatFound = 0, autoFound = 0;

        foreach (var fact in needles)
        {
            string needleTerm = fact.UniqueTerms[0];

            scrinia.ResetBudget();
            flat.ResetBudget();
            auto.ResetBudget();

            var sr = await scrinia.QueryAsync(needleTerm, fact.Key);
            var fr = await flat.QueryAsync(needleTerm, fact.Key);
            var ar = await auto.QueryAsync(needleTerm, fact.Key);

            if (sr.FoundTarget) scriniaFound++;
            if (fr.FoundTarget) flatFound++;
            if (ar.FoundTarget) autoFound++;
        }

        BenchmarkReporter.WriteComparisonTable(output,
            "Needle-in-Haystack Recall (unique term search, 20 needles in 500 facts)",
            ["System", "Found", "Recall"],
            [
                ["Scrinia",   $"{scriniaFound}/{needles.Count}", $"{scriniaFound / (double)needles.Count:P1}"],
                ["Flat-file", $"{flatFound}/{needles.Count}",    $"{flatFound / (double)needles.Count:P1}"],
                ["Auto",      $"{autoFound}/{needles.Count}",    $"{autoFound / (double)needles.Count:P1}"],
            ]);

        // All systems should find most needles — exact terms always match
        flatFound.Should().Be(needles.Count, "flat-file Contains should find all unique needles");
    }

    [Fact]
    public async Task FindNeedle_TokenCostComparison()
    {
        var corpus = BenchmarkCorpus.Generate(500);
        var needles = corpus.Where((_, i) => i % 25 == 0).Take(20).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaTotalTokens = 0, flatTotalTokens = 0, autoTotalTokens = 0;

        foreach (var fact in needles)
        {
            scrinia.ResetBudget();
            flat.ResetBudget();
            auto.ResetBudget();

            var sr = await scrinia.QueryAsync(fact.UniqueTerms[0], fact.Key);
            var fr = await flat.QueryAsync(fact.UniqueTerms[0], fact.Key);
            var ar = await auto.QueryAsync(fact.UniqueTerms[0], fact.Key);

            scriniaTotalTokens += sr.TokensConsumed;
            flatTotalTokens += fr.TokensConsumed;
            autoTotalTokens += ar.TokensConsumed;
        }

        double scriniaAvg = scriniaTotalTokens / (double)needles.Count;
        double flatAvg = flatTotalTokens / (double)needles.Count;
        double autoAvg = autoTotalTokens / (double)needles.Count;

        BenchmarkReporter.WriteComparisonTable(output,
            "Token Cost Per Needle Search (avg over 20 needles, 500-fact corpus)",
            ["System", "Total Tokens", "Avg/Query", "Ratio vs Flat"],
            [
                ["Scrinia",   $"{scriniaTotalTokens:N0}", $"{scriniaAvg:N0}", $"{scriniaAvg / flatAvg:F2}x"],
                ["Flat-file", $"{flatTotalTokens:N0}",    $"{flatAvg:N0}",    "1.00x"],
                ["Auto",      $"{autoTotalTokens:N0}",    $"{autoAvg:N0}",    $"{autoAvg / flatAvg:F2}x"],
            ]);

        BenchmarkReporter.WriteVerdict(output, "Needle search token cost",
            scriniaAvg < flatAvg ? "Scrinia" : "Flat-file",
            $"Scrinia uses {scriniaAvg / flatAvg:P0} of flat-file's token budget");

        // Scrinia should use significantly fewer tokens at 500 facts
        scriniaAvg.Should().BeLessThan(flatAvg, "Scrinia should find needles at lower token cost than flat-file");
    }

    [Fact]
    public async Task MultiTermNeedle_RequiresBothTerms()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        // Query with needle term + topic term — should narrow to exactly one fact
        var testFacts = corpus.Where((_, i) => i % 10 == 0).Take(10).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaFP = 0, flatFP = 0, autoFP = 0;
        int scriniaTP = 0, flatTP = 0, autoTP = 0;

        foreach (var fact in testFacts)
        {
            // Multi-term query: needle + a topic keyword
            string multiQuery = $"{fact.UniqueTerms[0]} {fact.Topic}";

            var sr = await scrinia.QueryAsync(multiQuery, fact.Key);
            var fr = await flat.QueryAsync(multiQuery, fact.Key);
            var ar = await auto.QueryAsync(multiQuery, fact.Key);

            if (sr.FoundTarget) scriniaTP++; else scriniaFP++;
            if (fr.FoundTarget) flatTP++; else flatFP++;
            if (ar.FoundTarget) autoTP++; else autoFP++;
        }

        BenchmarkReporter.WriteComparisonTable(output,
            "Multi-Term Needle Search (needle + topic, 10 queries, 100 facts)",
            ["System", "True Positives", "False Negatives", "Success Rate"],
            [
                ["Scrinia",   $"{scriniaTP}", $"{scriniaFP}", $"{scriniaTP / (double)testFacts.Count:P1}"],
                ["Flat-file", $"{flatTP}",    $"{flatFP}",    $"{flatTP / (double)testFacts.Count:P1}"],
                ["Auto",      $"{autoTP}",    $"{autoFP}",    $"{autoTP / (double)testFacts.Count:P1}"],
            ]);
    }
}
