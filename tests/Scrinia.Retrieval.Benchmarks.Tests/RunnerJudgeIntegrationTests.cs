using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Judge;
using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Integration test for the <see cref="AnswerabilityJudge"/> wiring in
/// <see cref="BenchmarkRunner"/>: proves the post-scoring judge pass attaches a verdict
/// to <see cref="QuestionResult"/> without disturbing the R@k numbers produced by the
/// existing <see cref="BenchmarkScorer"/> pipeline.
/// </summary>
public sealed class RunnerJudgeIntegrationTests : IDisposable
{
    private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"runner_judge_{Guid.NewGuid():N}.ndjson");
    public void Dispose() { try { File.Delete(_cacheFile); } catch { } }

    private sealed class ScriptedLlmClient : IBenchLlmClient
    {
        private readonly Queue<string?> _replies;
        public int CallCount { get; private set; }
        public ScriptedLlmClient(params string?[] replies) => _replies = new(replies);
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string?> CompleteAsync(string s, string u, int m, bool j, CancellationToken ct)
        { CallCount++; return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null); }
    }

    private sealed class StubRetriever : IRetriever
    {
        private readonly IReadOnlyList<RetrievalHit> _hits;
        public StubRetriever(IReadOnlyList<RetrievalHit> hits) => _hits = hits;
        public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default) =>
            Task.FromResult(_hits);
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    private sealed class CapturingLlmClient : IBenchLlmClient
    {
        public string? LastAnswererUser { get; private set; }
        private int _n;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string?> CompleteAsync(string s, string u, int m, bool j, CancellationToken ct)
        {
            // First call is the answerer (captures the retrieved contents), second is the grader.
            if (_n++ == 0) LastAnswererUser = u;
            return Task.FromResult<string?>(_n == 1 ? "Lisbon" : "correct");
        }
    }

    private sealed class SurfacingStubRetriever : IRetriever, ISurfacedContentProvider
    {
        public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetrievalHit>>([new RetrievalHit("D1:1", "HITS-TEXT-TOKEN", 1.0)]);
        public Task<IReadOnlyList<string>> GetSurfacedContentsAsync(string query, int k, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["SURFACED-TOKEN memory content"]);
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Judge_SourcesContent_FromSurfacedContentProvider_NotHits()
    {
        var retriever = new SurfacingStubRetriever();
        var question = new BenchmarkQuestion(
            "q1", "Where did Maria go?", [], ["D1:1"], [], "single-hop", GoldAnswer: "Lisbon");
        var llm = new CapturingLlmClient();
        var judge = new AnswerabilityJudge(llm, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

        await new BenchmarkRunner(NoOpAgentHarness.Instance, judge).RunAsync(
            "bench-test", "judge-test",
            ToAsync(new[] { question }), retriever, topK: 10,
            BenchmarkScorer.ScoreAnyHitById, ct: CancellationToken.None);

        llm.LastAnswererUser.Should().Contain("SURFACED-TOKEN");
        llm.LastAnswererUser.Should().NotContain("HITS-TEXT-TOKEN");
    }

    [Fact]
    public async Task RunAsync_WithJudge_PopulatesSurfacedAndCorrect_AndKeepsRecallIntact()
    {
        var retriever = new StubRetriever([new RetrievalHit("d1", "Maria booked Lisbon.", 1.0)]);
        var question = new BenchmarkQuestion(
            "q1", "Where did Maria go?", [], ["d1"], [], "single-hop", GoldAnswer: "Lisbon");

        var llm = new ScriptedLlmClient("Lisbon", "correct");
        var judge = new AnswerabilityJudge(llm, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

        var result = await new BenchmarkRunner(NoOpAgentHarness.Instance, judge).RunAsync(
            "bench-test", "judge-test",
            ToAsync(new[] { question }), retriever, topK: 10,
            BenchmarkScorer.ScoreAnyHitById, ct: CancellationToken.None);

        result.Questions.Should().HaveCount(1);
        result.Questions[0].SurfacedFromContext.Should().BeTrue();
        result.Questions[0].AnsweredCorrectly.Should().BeTrue();
        result.Questions[0].JudgeAnswer.Should().Be("Lisbon");
        result.Questions[0].JudgeGraderVerdict.Should().Be("correct");
        result.Questions[0].RecallAt10.Should().Be(1.0);
        result.Aggregate.SurfacedRate.Should().Be(1.0);
        result.Aggregate.CorrectRate.Should().Be(1.0);
    }

    [Fact]
    public async Task RunGroupedAsync_WithJudge_PopulatesSurfacedAndCorrect_AndKeepsRecallIntact()
    {
        var haystack = new[] { new HaystackDoc("d1", "Maria booked Lisbon.", null) };
        var retriever = new StubRetriever([new RetrievalHit("d1", "Maria booked Lisbon.", 1.0)]);
        var question = new BenchmarkQuestion(
            "q1", "Where did Maria go?", haystack, ["d1"], [], "single-hop", GoldAnswer: "Lisbon");
        var group = new BenchmarkQuestionGroup("g1", haystack, [question]);

        var llm = new ScriptedLlmClient("Lisbon", "correct");
        var judge = new AnswerabilityJudge(llm, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

        var result = await new BenchmarkRunner(NoOpAgentHarness.Instance, judge).RunGroupedAsync(
            "bench-test", "judge-test",
            ToAsync(new[] { group }), retriever, topK: 10,
            BenchmarkScorer.ScoreAnyHitById, ct: CancellationToken.None);

        result.Questions.Should().HaveCount(1);
        result.Questions[0].SurfacedFromContext.Should().BeTrue();
        result.Questions[0].AnsweredCorrectly.Should().BeTrue();
        result.Questions[0].JudgeAnswer.Should().Be("Lisbon");
        result.Questions[0].JudgeGraderVerdict.Should().Be("correct");
        result.Questions[0].RecallAt10.Should().Be(1.0);
        result.Aggregate.SurfacedRate.Should().Be(1.0);
        result.Aggregate.CorrectRate.Should().Be(1.0);
    }

    [Fact]
    public void WithCreatedDates_prefixes_known_and_passes_unknown_through()
    {
        var hits = new List<RetrievalHit> { new("D1:3", "I went yesterday", 1.0), new("D9:9", "unrelated", 0.5) };
        var dates = new Dictionary<string, DateTimeOffset?> { ["D1:3"] = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero) };
        var result = BenchmarkRunner.WithCreatedDates(hits.Select(h => h.Text).ToList(), hits, dates);
        Assert.Equal("created: 2023-05-04\nI went yesterday", result[0]);
        Assert.Equal("unrelated", result[1]);
    }

    [Fact]
    public void AnyDateSurfaced_true_when_any_passage_carries_a_created_line()
    {
        Assert.True(BenchmarkRunner.AnyDateSurfaced(["created: 2023-05-04\nfoo", "bar"]));
        Assert.False(BenchmarkRunner.AnyDateSurfaced(["foo", "bar"]));
    }

    [Fact]
    public async Task RunGroupedAsync_NoJudge_StillPersistsDatePrefixedContextAndGoldAnswer()
    {
        var timestamp = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero);
        var haystack = new[] { new HaystackDoc("d1", "Maria booked Lisbon.", timestamp) };
        var retriever = new StubRetriever([new RetrievalHit("d1", "Maria booked Lisbon.", 1.0)]);
        var question = new BenchmarkQuestion(
            "q1", "Where did Maria go?", haystack, ["d1"], [], "single-hop", GoldAnswer: "Lisbon");
        var group = new BenchmarkQuestionGroup("g1", haystack, [question]);

        var result = await new BenchmarkRunner(NoOpAgentHarness.Instance, judge: null).RunGroupedAsync(
            "bench-test", "no-judge-test",
            ToAsync(new[] { group }), retriever, topK: 10,
            BenchmarkScorer.ScoreAnyHitById,
            temporalScenario: BenchmarkRunner.SessionDateScenario,
            ct: CancellationToken.None);

        result.Questions.Should().HaveCount(1);
        result.Questions[0].JudgeContext.Should().NotBeNullOrEmpty();
        result.Questions[0].JudgeContext![0].Should().StartWith("created: ");
        result.Questions[0].DateSurfaced.Should().BeTrue();
        result.Questions[0].GoldAnswer.Should().Be("Lisbon");
        result.Questions[0].SurfacedFromContext.Should().BeNull();
        result.Questions[0].AnsweredCorrectly.Should().BeNull();
        result.RunMetadata.TemporalScenario.Should().Be("session-date-ingest");
    }

    [Fact]
    public void RunMetadata_serializes_granularity_and_scenario_and_defaults_null()
    {
        var meta = new RunMetadata("locomo", "bm25", 50, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "v",
            Granularity: "dialog", TemporalScenario: "session-date-ingest");
        string json = JsonSerializer.Serialize(meta, Program.JsonOutputOptions);
        var back = JsonSerializer.Deserialize<RunMetadata>(json, Program.JsonOutputOptions)!;
        Assert.Equal("dialog", back.Granularity);
        Assert.Equal("session-date-ingest", back.TemporalScenario);

        var bare = new RunMetadata("membench", "bm25", 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "v");
        Assert.Null(bare.Granularity);
        Assert.Null(bare.TemporalScenario);
    }
}
