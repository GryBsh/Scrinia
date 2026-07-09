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
    public async Task RunAsync_WithJudge_PopulatesAnswerable_AndKeepsRecallIntact()
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
        result.Questions[0].Answerable.Should().BeTrue();
        result.Questions[0].JudgeAnswer.Should().Be("Lisbon");
        result.Questions[0].JudgeGraderVerdict.Should().Be("correct");
        result.Questions[0].RecallAt10.Should().Be(1.0);
        result.Aggregate.Answerable.Should().Be(1.0);
    }

    [Fact]
    public async Task RunGroupedAsync_WithJudge_PopulatesAnswerable_AndKeepsRecallIntact()
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
        result.Questions[0].Answerable.Should().BeTrue();
        result.Questions[0].JudgeAnswer.Should().Be("Lisbon");
        result.Questions[0].JudgeGraderVerdict.Should().Be("correct");
        result.Questions[0].RecallAt10.Should().Be(1.0);
        result.Aggregate.Answerable.Should().Be(1.0);
    }
}
