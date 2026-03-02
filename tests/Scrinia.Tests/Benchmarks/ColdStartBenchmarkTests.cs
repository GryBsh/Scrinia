using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Measures cold-start overhead — tokens consumed before the first useful query.
/// Expected: Scrinia=0, Flat-file=linear with corpus, Auto=capped at index size.
/// </summary>
public sealed class ColdStartBenchmarkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task ColdStartOverhead_ScalesWithCorpusSize(int factCount)
    {
        var corpus = BenchmarkCorpus.Generate(factCount);

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        int scriniaColdStart = scrinia.GetColdStartTokens();
        int flatColdStart = flat.GetColdStartTokens();
        int autoColdStart = auto.GetColdStartTokens();

        output.WriteLine($"Cold start at {factCount} facts:");
        output.WriteLine($"  Scrinia:   {scriniaColdStart,8:N0} tokens");
        output.WriteLine($"  Flat-file: {flatColdStart,8:N0} tokens");
        output.WriteLine($"  Auto:      {autoColdStart,8:N0} tokens");

        // Scrinia has zero cold-start overhead
        scriniaColdStart.Should().Be(0, "Scrinia loads nothing until queried");

        // Flat-file cold start grows with corpus
        flatColdStart.Should().BeGreaterThan(0, "flat-file always loads everything");

        // Auto memory has some overhead from the index
        if (factCount >= 50)
            autoColdStart.Should().BeLessThan(flatColdStart, "auto index should be smaller than full corpus");
    }

    [Fact]
    public async Task FirstQueryCost_MeasuresStartupPenalty()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var targetFact = corpus[0];

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        // First query cost = cold start + query cost
        var scriniaResult = await scrinia.QueryAsync(targetFact.Question, targetFact.Key);
        var flatResult = await flat.QueryAsync(targetFact.Question, targetFact.Key);
        var autoResult = await auto.QueryAsync(targetFact.Question, targetFact.Key);

        int scriniaTotal = scrinia.GetColdStartTokens() + scriniaResult.TokensConsumed;
        int flatTotal = flat.GetColdStartTokens() + flatResult.TokensConsumed;
        int autoTotal = auto.GetColdStartTokens() + autoResult.TokensConsumed;

        BenchmarkReporter.WriteComparisonTable(output,
            "First Query Total Cost (cold start + query) @ 100 facts",
            ["System", "Cold Start", "Query", "Total", "Found?"],
            [
                ["Scrinia",   $"{scrinia.GetColdStartTokens():N0}", $"{scriniaResult.TokensConsumed:N0}", $"{scriniaTotal:N0}", $"{scriniaResult.FoundTarget}"],
                ["Flat-file", $"{flat.GetColdStartTokens():N0}",    $"{flatResult.TokensConsumed:N0}",    $"{flatTotal:N0}",    $"{flatResult.FoundTarget}"],
                ["Auto",      $"{auto.GetColdStartTokens():N0}",    $"{autoResult.TokensConsumed:N0}",    $"{autoTotal:N0}",    $"{autoResult.FoundTarget}"],
            ]);

        // Scrinia first query should be cheaper than flat-file at 100 facts
        scriniaTotal.Should().BeLessThan(flatTotal, "Scrinia selective retrieval should beat loading everything");
    }
}
