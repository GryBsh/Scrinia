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

    private sealed class TestMonotonicClock : IMonotonicClock
    {
        public TimeSpan GetElapsed() => TimeSpan.Zero;
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

    private enum TestResult
    {
        Transient,
        Success
    }
}
