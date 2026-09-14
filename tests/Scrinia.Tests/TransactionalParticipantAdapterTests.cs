using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantAdapterTests
{
    [Fact]
    public async Task Retry_budget_retries_only_declared_transient_results()
    {
        var clock = new TestMonotonicClock();
        var delays = new List<TimeSpan>();
        var budget = new RetryBudget(
            new TransactionalRetryPolicy(3, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero),
            clock,
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });
        var classifier = new ResultClassifier();
        var attempts = 0;

        var result = await budget.ExecuteAsync(
            _ => Task.FromResult(++attempts < 3 ? TestResult.Transient : TestResult.Success),
            classifier,
            CancellationToken.None);

        result.Value.Should().Be(TestResult.Success);
        result.Attempts.Should().Be(3);
        result.FailureCode.Should().BeNull();
        delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task Retry_budget_maps_elapsed_addition_overflow_without_retrying()
    {
        var clock = new SequenceClock(TimeSpan.MaxValue);
        var callbacks = 0;
        var budget = new RetryBudget(new TransactionalRetryPolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), clock,
            (_, _) => throw new Xunit.Sdk.XunitException("delay must not be scheduled"));

        var result = await budget.ExecuteAsync(_ =>
        {
            callbacks++;
            return Task.FromResult(TestResult.Transient);
        }, new ResultClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.RetryElapsedOverflow);
        result.StateMayHaveChanged.Should().BeTrue();
        result.Ambiguous.Should().BeTrue();
        callbacks.Should().Be(1);
    }

    [Fact]
    public void Retry_result_rejects_contradictory_terminal_flags()
    {
        var act = () => new RetryResult<TestResult>(TestResult.Success, 1, TimeSpan.Zero,
            TransactionalFailureCode.DeclaredTransient, false, false, true, false);

        act.Should().Throw<ArgumentException>();
    }

    private sealed class TestMonotonicClock : IMonotonicClock
    {
        public TimeSpan GetElapsed() => TimeSpan.Zero;
    }

    private sealed class SequenceClock(params TimeSpan[] values) : IMonotonicClock
    {
        private readonly Queue<TimeSpan> _values = new(values);
        public TimeSpan GetElapsed() => _values.Count == 0 ? values[^1] : _values.Dequeue();
    }

    private sealed class ResultClassifier : IRetryClassifier<TestResult>
    {
        public RetryClassification Classify(TestResult result, bool callbackStarted) =>
            result == TestResult.Transient
                ? new(RetryTerminalState.Success, TransactionalFailureCode.DeclaredTransient, false, false, true, false, "transient")
                : new(RetryTerminalState.Success, null, false, false, false, false, null);

        public RetryClassification Classify(Exception exception, bool callbackStarted) =>
            new(RetryTerminalState.Success, TransactionalFailureCode.Permanent, false, false, false, false, null);
    }

    private sealed record TestResult
    {
        private TestResult(string name) => Name = name;
        private string Name { get; }
        internal static readonly TestResult Transient = new("transient");
        internal static readonly TestResult Success = new("success");
    }
}
