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
