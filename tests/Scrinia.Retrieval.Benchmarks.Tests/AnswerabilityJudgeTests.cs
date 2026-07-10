using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Judge;
using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AnswerabilityJudgeTests : IDisposable
{
    private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"judge_{Guid.NewGuid():N}.ndjson");
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

    private AnswerabilityJudge Judge(ScriptedLlmClient c) =>
        new(c, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

    [Fact]
    public async Task Judge_CorrectGrade_YieldsAnswerableTrue()
    {
        var c = new ScriptedLlmClient("Lisbon", "correct");
        var v = await Judge(c).JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v.Should().NotBeNull();
        v!.Answerable.Should().BeTrue();
        v.Answer.Should().Be("Lisbon");
    }

    [Fact]
    public async Task Judge_IncorrectGrade_YieldsAnswerableFalse()
    {
        var c = new ScriptedLlmClient("Paris", "incorrect");
        var v = await Judge(c).JudgeAsync("q1", "Where?", "Lisbon", ["noise"], default);
        v!.Answerable.Should().BeFalse();
    }

    [Fact]
    public async Task Judge_NoGold_ReturnsNull_AndCountsNoGold()
    {
        var c = new ScriptedLlmClient();
        var j = Judge(c);
        var v = await j.JudgeAsync("q1", "Where?", null, ["x"], default);
        v.Should().BeNull();
        j.NoGoldCount.Should().Be(1);
        c.CallCount.Should().Be(0, "no gold means no judge calls");
    }

    [Fact]
    public async Task Judge_AnswererFailure_ReturnsNull_CountsFailed_NotCached()
    {
        var j = Judge(new ScriptedLlmClient((string?)null));
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default);
        v.Should().BeNull();
        j.FailedCount.Should().Be(1);
        // A fresh judge over the same inputs re-attempts (nothing was cached).
        var j2 = Judge(new ScriptedLlmClient("Lisbon", "correct"));
        (await j2.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default))!.Answerable.Should().BeTrue();
    }

    [Fact]
    public async Task Judge_GraderFailure_ReturnsNull_CountsFailed_NotCached()
    {
        var j = Judge(new ScriptedLlmClient("Lisbon", (string?)null));
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default);
        v.Should().BeNull();
        j.FailedCount.Should().Be(1);
        // A fresh judge over the same inputs re-attempts the grader (not cached). The
        // answerer's genuine "Lisbon" completion from attempt 1 *was* cache-through
        // persisted (that's correct, per-call behavior), so it is not re-asked here --
        // only the grader reply needs to be scripted for this second call to succeed.
        var j2 = Judge(new ScriptedLlmClient("correct"));
        (await j2.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default))!.Answerable.Should().BeTrue();
    }

    [Fact]
    public async Task Judge_AnswererBlank_ThenSucceeds_RetriesAndRecovers()
    {
        // The answerer returns blank on the first attempt, then a real answer — the retry
        // recovers the question instead of dropping it from the aggregate. The grader then
        // grades it correct.
        var c = new ScriptedLlmClient(null, "Lisbon", "correct");
        var j = Judge(c);
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v!.Answerable.Should().BeTrue();
        j.FailedCount.Should().Be(0, "a blank that recovers on retry is not a failure");
        c.CallCount.Should().Be(3, "answerer blank + answerer retry + grader");
    }

    [Fact]
    public async Task Judge_GraderBlank_ThenSucceeds_RetriesAndRecovers()
    {
        // The grader returns blank once, then a verdict — the grader retry recovers it.
        var c = new ScriptedLlmClient("Lisbon", null, "correct");
        var j = Judge(c);
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v!.Answerable.Should().BeTrue();
        j.FailedCount.Should().Be(0);
        c.CallCount.Should().Be(3, "answerer + grader blank + grader retry");
    }

    [Fact]
    public async Task Judge_AnswererBlank_EveryAttempt_CountsFailedAfterRetries()
    {
        // Every answerer attempt is blank. After exhausting retries the question is dropped
        // (null verdict, failed++), and the LLM was actually re-called up to the cap — not
        // skipped on the first blank as it was before retries were added.
        var c = new ScriptedLlmClient(); // always returns null
        var j = Judge(c);
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default);
        v.Should().BeNull();
        j.FailedCount.Should().Be(1);
        c.CallCount.Should().Be(3, "answerer retried up to the attempt cap before giving up");
    }

    [Fact]
    public async Task Judge_CachesVerdict_NoSecondLlmCall_OnIdenticalInputs()
    {
        _ = await Judge(new ScriptedLlmClient("Lisbon", "correct"))
            .JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        var spy = new ScriptedLlmClient(); // would return null if called
        var v = await Judge(spy).JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v!.Answerable.Should().BeTrue();
        spy.CallCount.Should().Be(0, "identical inputs must hit the cache");
    }
}
