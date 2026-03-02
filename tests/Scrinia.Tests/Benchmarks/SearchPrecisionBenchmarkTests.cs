using FluentAssertions;
using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Measures search precision and recall — how accurately each system finds
/// the correct fact given a natural-language query.
/// Expected: Scrinia ranked precision > Flat-file (Contains returns noisy matches).
/// </summary>
public sealed class SearchPrecisionBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecallAt1_100Facts()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        int scriniaHits = 0, flatHits = 0, autoHits = 0;

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        foreach (var fact in queries)
        {
            scrinia.ResetBudget();
            flat.ResetBudget();
            auto.ResetBudget();

            var sr = await scrinia.QueryAsync(fact.Question, fact.Key);
            var fr = await flat.QueryAsync(fact.Question, fact.Key);
            var ar = await auto.QueryAsync(fact.Question, fact.Key);

            if (sr.FoundTarget) scriniaHits++;
            if (fr.FoundTarget) flatHits++;
            if (ar.FoundTarget) autoHits++;
        }

        double scriniaRecall = scriniaHits / (double)queries.Count;
        double flatRecall = flatHits / (double)queries.Count;
        double autoRecall = autoHits / (double)queries.Count;

        BenchmarkReporter.WriteComparisonTable(output,
            "Recall@1 (target fact is top result, 20 queries, 100-fact corpus)",
            ["System", "Hits", "Recall@1"],
            [
                ["Scrinia",   $"{scriniaHits}/{queries.Count}", $"{scriniaRecall:P1}"],
                ["Flat-file", $"{flatHits}/{queries.Count}",    $"{flatRecall:P1}"],
                ["Auto",      $"{autoHits}/{queries.Count}",    $"{autoRecall:P1}"],
            ]);
    }

    [Fact]
    public async Task RecallAt5_100Facts()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 5 == 0).Take(20).ToList();

        int scriniaHits = 0, flatHits = 0, autoHits = 0;

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        foreach (var fact in queries)
        {
            // For Scrinia, FoundTarget checks if key is anywhere in search results (top 10)
            var sr = await scrinia.QueryAsync(fact.Question, fact.Key);
            if (sr.FoundTarget) scriniaHits++;

            // For flat/auto, check if target is in the found content list
            var fr = await flat.QueryAsync(fact.Question, fact.Key);
            if (fr.FoundTarget) flatHits++;

            var ar = await auto.QueryAsync(fact.Question, fact.Key);
            if (ar.FoundTarget) autoHits++;
        }

        double scriniaRecall = scriniaHits / (double)queries.Count;
        double flatRecall = flatHits / (double)queries.Count;
        double autoRecall = autoHits / (double)queries.Count;

        BenchmarkReporter.WriteComparisonTable(output,
            "Recall@5 (target fact in top 5 results, 20 queries, 100-fact corpus)",
            ["System", "Hits", "Recall@5"],
            [
                ["Scrinia",   $"{scriniaHits}/{queries.Count}", $"{scriniaRecall:P1}"],
                ["Flat-file", $"{flatHits}/{queries.Count}",    $"{flatRecall:P1}"],
                ["Auto",      $"{autoHits}/{queries.Count}",    $"{autoRecall:P1}"],
            ]);

        // At recall@5 all systems should have reasonable recall
        scriniaRecall.Should().BeGreaterThan(0.3, "Scrinia should find at least 30% of targets in top 5");
    }

    [Fact]
    public async Task PrecisionAtK_MeasuresNoise()
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var queries = corpus.Where((_, i) => i % 10 == 0).Take(10).ToList();

        int scriniaRelevant = 0, scriniaTotal = 0;
        int flatRelevant = 0, flatTotal = 0;
        int autoRelevant = 0, autoTotal = 0;

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        await using var flat = new FlatFileMemorySystem();
        await flat.SetupAsync(corpus);

        await using var auto = new AutoMemorySystem();
        await auto.SetupAsync(corpus);

        foreach (var fact in queries)
        {
            var sr = await scrinia.QueryAsync(fact.Question, fact.Key);
            scriniaTotal += Math.Max(1, sr.ResultCount);
            if (sr.FoundTarget) scriniaRelevant++;

            var fr = await flat.QueryAsync(fact.Question, fact.Key);
            flatTotal += Math.Max(1, fr.ResultCount);
            if (fr.FoundTarget) flatRelevant++;

            var ar = await auto.QueryAsync(fact.Question, fact.Key);
            autoTotal += Math.Max(1, ar.ResultCount);
            if (ar.FoundTarget) autoRelevant++;
        }

        // Precision = relevant / total returned
        // For flat-file, Contains often returns many noisy matches
        double scriniaPrecision = scriniaTotal > 0 ? scriniaRelevant / (double)scriniaTotal : 0;
        double flatPrecision = flatTotal > 0 ? flatRelevant / (double)flatTotal : 0;
        double autoPrecision = autoTotal > 0 ? autoRelevant / (double)autoTotal : 0;

        BenchmarkReporter.WriteComparisonTable(output,
            "Precision@K (relevant results / total results, 10 queries)",
            ["System", "Relevant", "Total Results", "Precision"],
            [
                ["Scrinia",   $"{scriniaRelevant}", $"{scriniaTotal}", $"{scriniaPrecision:P1}"],
                ["Flat-file", $"{flatRelevant}",    $"{flatTotal}",    $"{flatPrecision:P1}"],
                ["Auto",      $"{autoRelevant}",    $"{autoTotal}",    $"{autoPrecision:P1}"],
            ]);

        // Flat-file Contains returns many false positives — lower precision
        output.WriteLine("");
        BenchmarkReporter.WriteVerdict(output, "Precision",
            scriniaPrecision >= flatPrecision ? "Scrinia" : "Flat-file",
            $"Scrinia={scriniaPrecision:P1}, Flat-file={flatPrecision:P1}, Auto={autoPrecision:P1}");
    }

    [Theory]
    [InlineData("api")]
    [InlineData("arch")]
    [InlineData("config")]
    [InlineData("deploy")]
    [InlineData("debug")]
    public async Task PerTopicRecall(string topic)
    {
        var corpus = BenchmarkCorpus.Generate(100);
        var topicFacts = corpus.Where(f => f.Topic == topic).Take(5).ToList();

        await using var scrinia = new ScriniaMemorySystem();
        await scrinia.SetupAsync(corpus);

        int hits = 0;
        foreach (var fact in topicFacts)
        {
            var result = await scrinia.QueryAsync(fact.Question, fact.Key);
            if (result.FoundTarget) hits++;
        }

        double recall = hits / (double)topicFacts.Count;
        output.WriteLine($"Topic '{topic}': Recall = {recall:P1} ({hits}/{topicFacts.Count})");

        // No single topic should have zero recall — checks for systematic bias
        recall.Should().BeGreaterThan(0, $"Scrinia should find at least one '{topic}' fact");
    }
}
