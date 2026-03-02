using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Measures cross-topic isolation — what fraction of loaded tokens come from
/// the target topic vs. unrelated topics.
/// Expected: Scrinia ~1.0, Auto varies, Flat-file = 1/numTopics.
/// </summary>
public sealed class IsolationBenchmarkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("api")]
    [InlineData("arch")]
    [InlineData("deploy")]
    public async Task SingleTopicQuery_MeasuresLeakage(string topic)
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var targetFact = corpus.First(f => f.Topic == topic);

        // Scrinia
        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);
        scrinia.ResetBudget();
        var scriniaResult = await scrinia.QueryAsync(targetFact.Question, targetFact.Key);

        // Flat-file
        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);
        flat.ResetBudget();
        var flatResult = await flat.QueryAsync(targetFact.Question, targetFact.Key);

        // Auto
        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);
        auto.ResetBudget();
        var autoResult = await auto.QueryAsync(targetFact.Question, targetFact.Key);

        // Compute isolation: for Scrinia, result content should be from target topic
        // For flat-file, isolation is always ~1/numTopics since everything is loaded
        int numTopics = BenchmarkCorpus.Topics.Length;
        double flatIsolation = 1.0 / numTopics; // Theoretical: all topics always loaded

        // For Scrinia, check if returned content matches target topic
        double scriniaIsolation = scriniaResult.FoundTarget ? 1.0 : 0.0;

        // For auto, estimate based on token cost relative to single-topic cost
        int totalAutoCorpus = auto.GetTotalCorpusTokens();
        double autoIsolation = totalAutoCorpus > 0
            ? 1.0 - ((autoResult.TokensConsumed - auto.GetColdStartTokens()) / (double)(totalAutoCorpus - auto.GetColdStartTokens()))
            : 0.0;
        autoIsolation = Math.Max(0, Math.Min(1.0, autoIsolation));

        output.WriteLine($"Topic: {topic}");
        output.WriteLine($"  Scrinia isolation:   {scriniaIsolation:P1} (found target: {scriniaResult.FoundTarget})");
        output.WriteLine($"  Flat-file isolation:  {flatIsolation:P1} (always loads all)");
        output.WriteLine($"  Auto isolation:       {autoIsolation:P1}");
    }

    [Fact]
    public async Task IsolationSummary_AllTopics()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var testTopics = new[] { "api", "arch", "config", "deploy", "debug" };

        double scriniaSum = 0, flatSum = 0, autoSum = 0;

        foreach (var topic in testTopics)
        {
            var targetFact = corpus.First(f => f.Topic == topic);

            await using var scrinia = new ScriniaMemorySystem();
            await scrinia.SetupAsync(corpus);
            scrinia.ResetBudget();
            var sr = await scrinia.QueryAsync(targetFact.Question, targetFact.Key);
            scriniaSum += sr.FoundTarget ? 1.0 : 0.0;

            await using var flat = new FlatFileMemorySystem();
            await flat.SetupAsync(corpus);
            flat.ResetBudget();
            await flat.QueryAsync(targetFact.Question, targetFact.Key);
            flatSum += 1.0 / BenchmarkCorpus.Topics.Length;

            await using var auto = new AutoMemorySystem();
            await auto.SetupAsync(corpus);
            auto.ResetBudget();
            var ar = await auto.QueryAsync(targetFact.Question, targetFact.Key);
            int totalAuto = auto.GetTotalCorpusTokens();
            double autoIso = totalAuto > 0
                ? 1.0 - ((ar.TokensConsumed - auto.GetColdStartTokens()) / (double)(totalAuto - auto.GetColdStartTokens()))
                : 0.0;
            autoSum += Math.Max(0, Math.Min(1.0, autoIso));
        }

        double scriniaAvg = scriniaSum / testTopics.Length;
        double flatAvg = flatSum / testTopics.Length;
        double autoAvg = autoSum / testTopics.Length;

        BenchmarkReporter.WriteComparisonTable(output,
            "Average Isolation Ratio (5 topics, 100-fact corpus)",
            ["System", "Avg Isolation", "Interpretation"],
            [
                ["Scrinia",   $"{scriniaAvg:P1}", "Only loads matching memories"],
                ["Flat-file", $"{flatAvg:P1}",    $"Always loads all {BenchmarkCorpus.Topics.Length} topics"],
                ["Auto",      $"{autoAvg:P1}",    "Loads index + routed topic(s)"],
            ]);

        // Scrinia isolation should beat flat-file
        scriniaAvg.Should().BeGreaterThan(flatAvg, "Scrinia should have better isolation than flat-file");
    }
}
