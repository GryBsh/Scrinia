using FluentAssertions;
using Scrinia.Mcp;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Verifies that updated facts are correctly returned by search and that
/// old content is no longer surfaced. Only Scrinia supports staleness detection.
/// </summary>
public sealed class UpdateCorrectnessBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UpdatedFact_SearchReturnsNewVersion()
    {
        var corpus = BenchmarkCorpus.Generate(50);
        var updates = BenchmarkCorpus.GenerateUpdates(corpus, 0, 5, 10, 15, 20);

        // Test each system
        var results = new List<string[]>();

        // Scrinia
        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);
        int scriniaFound = 0;
        foreach (var update in updates)
        {
            await scrinia.UpdateFactAsync(update);
            var result = await scrinia.QueryAsync(update.UniqueTerms[0], update.Key);
            if (result.FoundTarget) scriniaFound++;
        }
        results.Add(["Scrinia", $"{scriniaFound}/{updates.Count}", $"{scriniaFound == updates.Count}"]);

        // Flat-file
        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);
        int flatFound = 0;
        foreach (var update in updates)
        {
            await flat.UpdateFactAsync(update);
            var result = await flat.QueryAsync(update.UniqueTerms[0], update.Key);
            if (result.FoundTarget) flatFound++;
        }
        results.Add(["Flat-file", $"{flatFound}/{updates.Count}", $"{flatFound == updates.Count}"]);

        // Auto
        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);
        int autoFound = 0;
        foreach (var update in updates)
        {
            await auto.UpdateFactAsync(update);
            var result = await auto.QueryAsync(update.UniqueTerms[0], update.Key);
            if (result.FoundTarget) autoFound++;
        }
        results.Add(["Auto", $"{autoFound}/{updates.Count}", $"{autoFound == updates.Count}"]);

        BenchmarkReporter.WriteComparisonTable(output,
            "Updated Fact Retrieval (5 updates in 50-fact corpus)",
            ["System", "Found", "All Found?"],
            results);

        // All systems should find updated content
        scriniaFound.Should().Be(updates.Count, "Scrinia should find all updated facts");
        flatFound.Should().Be(updates.Count, "Flat-file should find all updated facts");
        autoFound.Should().Be(updates.Count, "Auto should find all updated facts");
    }

    [Fact]
    public async Task UpdatedFact_OldContentNotReturned()
    {
        var corpus = BenchmarkCorpus.Generate(50);
        var updates = BenchmarkCorpus.GenerateUpdates(corpus, 0, 10, 20);

        var results = new List<string[]>();

        // Scrinia — old needle terms should not appear in search results
        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);
        int scriniaStale = 0;
        foreach (var update in updates)
        {
            string oldNeedle = corpus.First(f => f.Key == update.Key).UniqueTerms[0];
            await scrinia.UpdateFactAsync(update);
            var result = await scrinia.QueryAsync(oldNeedle, update.Key);
            if (result.FoundTarget) scriniaStale++;
        }
        results.Add(["Scrinia", $"{scriniaStale}/{updates.Count}", $"{scriniaStale == 0}"]);

        // Flat-file
        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);
        int flatStale = 0;
        foreach (var update in updates)
        {
            string oldNeedle = corpus.First(f => f.Key == update.Key).UniqueTerms[0];
            await flat.UpdateFactAsync(update);
            var result = await flat.QueryAsync(oldNeedle, update.Key);
            if (result.FoundTarget) flatStale++;
        }
        results.Add(["Flat-file", $"{flatStale}/{updates.Count}", $"{flatStale == 0}"]);

        // Auto
        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);
        int autoStale = 0;
        foreach (var update in updates)
        {
            string oldNeedle = corpus.First(f => f.Key == update.Key).UniqueTerms[0];
            await auto.UpdateFactAsync(update);
            var result = await auto.QueryAsync(oldNeedle, update.Key);
            if (result.FoundTarget) autoStale++;
        }
        results.Add(["Auto", $"{autoStale}/{updates.Count}", $"{autoStale == 0}"]);

        BenchmarkReporter.WriteComparisonTable(output,
            "Stale Content Check (search for OLD needle after update)",
            ["System", "Stale Hits", "Clean?"],
            results);

        // No system should return old content after update
        scriniaStale.Should().Be(0, "Scrinia should not return old content");
        flatStale.Should().Be(0, "Flat-file should not return old content");
        autoStale.Should().Be(0, "Auto should not return old content");
    }

    [Fact]
    public async Task StalenessDetection_OnlyScrinia()
    {
        var corpus = BenchmarkCorpus.Generate(10);

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        // Store a fact with reviewAfter in the past
        var tools = new ScriniaMcpTools();
        await tools.Store(
            ["This fact should be marked stale."],
            "debug:stale-test",
            description: "Test staleness",
            reviewAfter: "2020-01-01");

        // List should show stale marker
        string listOutput = await tools.List();
        output.WriteLine("List output with stale marker:");
        output.WriteLine(listOutput);

        listOutput.Should().Contain("stale", "Scrinia should mark memories past their review date");

        BenchmarkReporter.WriteVerdict(output,
            "Staleness detection",
            "Scrinia (exclusive)",
            "Only system with reviewAfter/reviewWhen markers");
    }
}
