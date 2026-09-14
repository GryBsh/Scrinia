using FluentAssertions;
using Scrinia.Core;
using Xunit;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Scrinia.Services;
using TransactionalFailureCode = Scrinia.Core.TransactionalFailureCode;
using TransactionalRetryPolicy = Scrinia.Core.TransactionalRetryPolicy;

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

    [Fact]
    public async Task Invalid_classifier_output_becomes_invalid_response_without_success()
    {
        var budget = new RetryBudget(new TransactionalRetryPolicy(), new TestMonotonicClock(),
            (_, _) => ValueTask.CompletedTask);

        var result = await budget.ExecuteAsync(_ => Task.FromResult(TestResult.Success),
            new InvalidClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.InvalidResponse);
        result.Ambiguous.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_before_callback_does_not_invoke_operation_or_schedule_delay()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callbacks = 0;
        var budget = new RetryBudget(new TransactionalRetryPolicy(), new TestMonotonicClock(),
            (_, _) => throw new Xunit.Sdk.XunitException("delay must not be scheduled"));

        var result = await budget.ExecuteAsync(_ =>
        {
            callbacks++;
            return Task.FromResult(TestResult.Success);
        }, new ResultClassifier(), cancellation.Token);

        result.FailureCode.Should().Be(TransactionalFailureCode.Cancelled);
        result.Attempts.Should().Be(0);
        callbacks.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_after_callback_start_is_ambiguous_and_does_not_retry()
    {
        var callbacks = 0;
        var budget = new RetryBudget(new TransactionalRetryPolicy(), new TestMonotonicClock(),
            (_, _) => throw new Xunit.Sdk.XunitException("delay must not be scheduled"));

        var result = await budget.ExecuteAsync<TestResult>(_ =>
        {
            callbacks++;
            throw new OperationCanceledException();
        }, new CancellationClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.Cancelled);
        result.Attempts.Should().Be(1);
        result.StateMayHaveChanged.Should().BeTrue();
        result.Ambiguous.Should().BeTrue();
        callbacks.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_cancelled_classification_maps_to_invalid_response()
    {
        var budget = new RetryBudget(new TransactionalRetryPolicy(), new TestMonotonicClock(),
            (_, _) => ValueTask.CompletedTask);

        var result = await budget.ExecuteAsync(_ => Task.FromResult(TestResult.Success),
            new InvalidCancellationClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.InvalidResponse);
        result.Cancelled.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Retry_result_accepts_each_terminal_truth_table_row()
    {
        var valid = new[]
        {
            () => new RetryResult<TestResult>(TestResult.Success, 1, TimeSpan.Zero, null, false, false, false, false),
            () => new RetryResult<TestResult>(default, 3, TimeSpan.Zero, TransactionalFailureCode.DeclaredTransient, true, false, true, false),
            () => new RetryResult<TestResult>(default, 0, TimeSpan.Zero, TransactionalFailureCode.Cancelled, false, false, false, true),
            () => new RetryResult<TestResult>(default, 1, TimeSpan.Zero, TransactionalFailureCode.Cancelled, true, true, false, true),
            () => new RetryResult<TestResult>(default, 1, TimeSpan.Zero, TransactionalFailureCode.RetryMonotonicClockFailed, true, true, false, false),
            () => new RetryResult<TestResult>(default, 1, TimeSpan.Zero, TransactionalFailureCode.RetryMonotonicClockMovedBackwards, true, true, false, false),
            () => new RetryResult<TestResult>(default, 1, TimeSpan.Zero, TransactionalFailureCode.RetryElapsedOverflow, true, true, false, false)
        };

        foreach (var factory in valid)
            FluentActions.Invoking(factory).Should().NotThrow();
    }

    [Fact]
    public void Retry_classification_rejects_invalid_terminal_combinations()
    {
        var invalid = new[]
        {
            new RetryClassification(RetryTerminalState.Success, null, true, false, false, false, null),
            new RetryClassification(RetryTerminalState.Success, TransactionalFailureCode.Permanent, false, false, true, false, null),
            new RetryClassification(RetryTerminalState.CancellationBeforeCallback, TransactionalFailureCode.Cancelled, true, true, false, true, null),
            new RetryClassification(RetryTerminalState.RetryElapsedOverflow, TransactionalFailureCode.RetryElapsedOverflow, true, false, false, false, null),
            new RetryClassification((RetryTerminalState)99, null, false, false, false, false, null)
        };

        invalid.Should().OnlyContain(classification => !classification.IsValid);
    }

    [Fact]
    public async Task Backward_monotonic_clock_is_terminal_and_never_retries()
    {
        var callbacks = 0;
        var budget = new RetryBudget(new TransactionalRetryPolicy(),
            new SequenceClock(TimeSpan.FromSeconds(1), TimeSpan.Zero),
            (_, _) => ValueTask.CompletedTask);

        var result = await budget.ExecuteAsync(_ =>
        {
            callbacks++;
            return Task.FromResult(TestResult.Transient);
        }, new ResultClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.RetryMonotonicClockMovedBackwards);
        result.Ambiguous.Should().BeTrue();
        callbacks.Should().Be(2);
    }

    [Fact]
    public async Task Monotonic_clock_failure_stops_without_delay_or_additional_callback()
    {
        var callbacks = 0;
        var delays = 0;
        var budget = new RetryBudget(new TransactionalRetryPolicy(), new FailingClock(),
            (_, _) => { delays++; return ValueTask.CompletedTask; });

        var result = await budget.ExecuteAsync(_ =>
        {
            callbacks++;
            return Task.FromResult(TestResult.Transient);
        }, new ResultClassifier(), CancellationToken.None);

        result.FailureCode.Should().Be(TransactionalFailureCode.RetryMonotonicClockFailed);
        callbacks.Should().Be(1);
        delays.Should().Be(0);
    }

    [Fact]
    public async Task Adapter_preserves_commit_failure_evidence_context_and_handle()
    {
        TransactionalCommitContext? captured = null;
        var evidence = new TransactionalRecoveryEvidence("recovery/commit", "journal", "v1");
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (context, _) =>
            {
                captured = context;
                return Task.FromResult(new TransactionalParticipantResult(
                    TransactionalParticipantState.Changed,
            new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.CommitFailed, "commit failed", true), evidence));
            }
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());

        await adapter.PrepareAsync(CancellationToken.None);
        var act = () => adapter.CommitAsync(CancellationToken.None);

        var error = await act.Should().ThrowAsync<TransactionalParticipantException>();
        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.CommitFailed);
        error.Which.Evidence.Should().Be(evidence);
        error.Which.Ambiguous.Should().BeTrue();
        adapter.EvidencePath.Should().Be("recovery/commit");
        captured!.TransactionId.Should().Be("tx");
        captured.Scope.Should().Be("scope");
        captured.PreparedHandle.Reference.Should().Be("opaque");
    }

    [Fact]
    public async Task Adapter_status_failure_is_typed_instead_of_unknown()
    {
        var evidence = new TransactionalRecoveryEvidence("recovery/status", "journal", "v1");
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Status = (_, _) => Task.FromResult(new TransactionalCommitStatusResult(
                TransactionalCommitStatus.Unknown, evidence,
                new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.VerificationFailed, "status failed", true)))
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        var error = await FluentActions.Invoking(() => adapter.GetCommitStatusAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.VerificationFailed);
        error.Which.Evidence.Should().Be(evidence);
        error.Which.Ambiguous.Should().BeTrue();
        adapter.EvidencePath.Should().Be("recovery/status");
    }

    [Fact]
    public async Task Successful_follow_up_operation_clears_stale_evidence()
    {
        var evidence = new TransactionalRecoveryEvidence("recovery/commit", "journal", "v1");
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => Task.FromResult(new TransactionalParticipantResult(
                TransactionalParticipantState.Changed,
                new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.CommitFailed, "failed", true), evidence)),
            Rollback = (_, _) => Task.FromResult(new TransactionalParticipantResult(
                TransactionalParticipantState.RolledBack, null, null))
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);
        await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();
        adapter.EvidencePath.Should().Be("recovery/commit");

        await adapter.RollbackAsync(CancellationToken.None);

        adapter.EvidencePath.Should().BeEmpty();
        adapter.RecoveryState.Should().NotBeNull();
        adapter.RecoveryState!.LifecycleState.Should().Be(TransactionalParticipantLifecycleState.RolledBack);
        adapter.RecoveryState.StateMayHaveChanged.Should().BeFalse();
        adapter.RecoveryState.Ambiguous.Should().BeFalse();
        adapter.RecoveryState.QuarantineState.Should().Be(TransactionalQuarantineState.None);
    }

    [Fact]
    public async Task Ordinary_exception_creates_ambiguous_recovery_state_with_quarantine()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => throw new InvalidOperationException("provider failed")
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        adapter.RecoveryState.Should().NotBeNull();
        adapter.RecoveryState!.LifecycleState.Should().Be(TransactionalParticipantLifecycleState.Ambiguous);
        adapter.RecoveryState.StateMayHaveChanged.Should().BeTrue();
        adapter.RecoveryState.Ambiguous.Should().BeTrue();
        adapter.RecoveryState.QuarantineState.Should().Be(TransactionalQuarantineState.Quarantined);
        adapter.RecoveryState.LastFailureCode.Should().Be(TransactionalFailureCode.Permanent);
    }

    [Fact]
    public async Task Successful_status_and_commit_clear_previous_ambiguity_state()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => Task.FromResult(new TransactionalParticipantResult(
                TransactionalParticipantState.Committed, null, null)),
            Status = (_, _) => Task.FromResult(new TransactionalCommitStatusResult(
                TransactionalCommitStatus.Committed, null, null))
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await adapter.GetCommitStatusAsync(CancellationToken.None);

        adapter.RecoveryState.Should().NotBeNull();
        adapter.RecoveryState!.LifecycleState.Should().Be(TransactionalParticipantLifecycleState.Committed);
        adapter.RecoveryState.StateMayHaveChanged.Should().BeFalse();
        adapter.RecoveryState.Ambiguous.Should().BeFalse();
        adapter.RecoveryState.QuarantineState.Should().Be(TransactionalQuarantineState.None);
    }

    [Fact]
    public async Task Ordinary_exception_uses_fixed_failure_detail_without_exception_message()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => throw new InvalidOperationException("secret provider detail")
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        var error = await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.Permanent);
        error.Which.Failure.Detail.Should().Be("observer diagnostic omitted");
        error.Which.Failure.Detail.Should().NotContain("secret provider detail");
    }

    [Fact]
    public async Task Recovery_state_preserves_retry_metadata_after_lifecycle_update()
    {
        var attempts = 0;
        var participant = new TestParticipant
        {
            RetryPolicy = new(2, initialDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero),
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => Task.FromResult(++attempts == 1
                ? new TransactionalParticipantResult(TransactionalParticipantState.Changed,
                    new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient, "retry", false),
                    new TransactionalRecoveryEvidence("recovery/retry", "journal", "v1"))
                : new TransactionalParticipantResult(TransactionalParticipantState.Committed, null, null))
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());

        await adapter.PrepareAsync(CancellationToken.None);
        await adapter.CommitAsync(CancellationToken.None);

        attempts.Should().Be(2);
        adapter.RecoveryState.Should().NotBeNull();
        adapter.RecoveryState!.RetryAttempt.Should().Be(2);
        adapter.RecoveryState.RetryElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Rehydration_preserves_complete_evidence_triple()
    {
        var state = new B4ParticipantRecoveryState(
            "tx", "op", "scope", "/memory/item", "participant", 1, "participant-key", "kind",
            ["transaction"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.Ambiguous, "opaque", "v1", 3, 42, true, true,
            Scrinia.Core.TransactionalFailureCode.CommitFailed, "recovery/commit", "journal", "v2",
            TransactionalQuarantineState.Quarantined, "ambiguous participant state");

        var adapter = new TransactionalParticipantAdapter(new TestParticipant(), state);

        adapter.RecoveryState.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task Null_prepare_callback_result_is_typed_as_invalid_response()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult<TransactionalPrepareResult>(null!)
        };

        var error = await FluentActions.Invoking(() => new TransactionalParticipantAdapter(participant, Request())
                .PrepareAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.InvalidResponse);
        error.Which.Evidence.Should().BeNull();
    }

    [Fact]
    public async Task Null_commit_callback_result_is_typed_as_invalid_response()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => Task.FromResult<TransactionalParticipantResult>(null!)
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        var error = await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.InvalidResponse);
        error.Which.Evidence.Should().BeNull();
    }

    [Fact]
    public async Task Null_rollback_callback_result_is_typed_as_invalid_response()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Rollback = (_, _) => Task.FromResult<TransactionalParticipantResult>(null!)
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        var error = await FluentActions.Invoking(() => adapter.RollbackAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.InvalidResponse);
        error.Which.Evidence.Should().BeNull();
    }

    [Fact]
    public async Task Null_status_callback_result_is_typed_as_invalid_response()
    {
        var participant = new TestParticipant
        {
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Status = (_, _) => Task.FromResult<TransactionalCommitStatusResult>(null!)
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());
        await adapter.PrepareAsync(CancellationToken.None);

        var error = await FluentActions.Invoking(() => adapter.GetCommitStatusAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        error.Which.Failure.Code.Should().Be(Scrinia.Plugin.Abstractions.TransactionalFailureCode.InvalidResponse);
        error.Which.Evidence.Should().BeNull();
    }

    [Fact]
    public async Task Successful_retry_replaces_evidence_from_the_previous_attempt()
    {
        var evidence = new TransactionalRecoveryEvidence("recovery/first-attempt", "journal", "v1");
        var attempts = 0;
        var participant = new TestParticipant
        {
            RetryPolicy = new(2, initialDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero),
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) => Task.FromResult(++attempts == 1
                ? new TransactionalParticipantResult(TransactionalParticipantState.Changed,
                    new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient, "retry", true), evidence)
                : new TransactionalParticipantResult(TransactionalParticipantState.Committed, null, null))
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());

        await adapter.PrepareAsync(CancellationToken.None);
        await adapter.CommitAsync(CancellationToken.None);

        attempts.Should().Be(2);
        adapter.EvidencePath.Should().BeEmpty();
    }

    [Fact]
    public async Task Thrown_failure_without_evidence_clears_evidence_from_previous_retry_attempt()
    {
        var evidence = new TransactionalRecoveryEvidence("recovery/first-attempt", "journal", "v1");
        var attempts = 0;
        var participant = new TestParticipant
        {
            RetryPolicy = new(2, initialDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero),
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                    return Task.FromResult(new TransactionalParticipantResult(
                        TransactionalParticipantState.Changed,
                        new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient, "retry", true), evidence));

                throw new InvalidOperationException("commit failed");
            }
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());

        await adapter.PrepareAsync(CancellationToken.None);
        await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        attempts.Should().Be(2);
        adapter.EvidencePath.Should().BeEmpty();
    }

    [Fact]
    public async Task Typed_failure_without_evidence_clears_evidence_from_previous_retry_attempt()
    {
        var evidence = new TransactionalRecoveryEvidence("recovery/first-attempt", "journal", "v1");
        var attempts = 0;
        var participant = new TestParticipant
        {
            RetryPolicy = new(2, initialDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero),
            Prepare = (_, _) => Task.FromResult(new TransactionalPrepareResult(new("opaque", "v1"), null, null)),
            Commit = (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                    return Task.FromResult(new TransactionalParticipantResult(
                        TransactionalParticipantState.Changed,
                        new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient, "retry", true), evidence));

                throw new TransactionalParticipantException(
                    new(Scrinia.Plugin.Abstractions.TransactionalFailureCode.CommitFailed, "commit failed", true),
                    null,
                    true);
            }
        };
        var adapter = new TransactionalParticipantAdapter(participant, Request());

        await adapter.PrepareAsync(CancellationToken.None);
        await FluentActions.Invoking(() => adapter.CommitAsync(CancellationToken.None))
            .Should().ThrowAsync<TransactionalParticipantException>();

        attempts.Should().Be(2);
        adapter.EvidencePath.Should().BeEmpty();
    }

    [Fact]
    public void Null_callback_results_are_invalid_response_not_permanent()
    {
        TransactionalParticipantAdapter.ClassifyReturnedFailure((TransactionalPrepareResult?)null, true)
            .Should().Match<RetryClassification>(x => x.FailureCode == TransactionalFailureCode.InvalidResponse && x.Ambiguous);
        TransactionalParticipantAdapter.ClassifyReturnedFailure((TransactionalParticipantResult?)null, true)
            .Should().Match<RetryClassification>(x => x.FailureCode == TransactionalFailureCode.InvalidResponse && x.Ambiguous);
        TransactionalParticipantAdapter.ClassifyReturnedFailure((TransactionalCommitStatusResult?)null, true)
            .Should().Match<RetryClassification>(x => x.FailureCode == TransactionalFailureCode.InvalidResponse && x.Ambiguous);
    }

    [Fact]
    public void All_lifecycle_returned_failures_and_typed_exceptions_preserve_disposition()
    {
        var failure = new TransactionalParticipantFailure(
            Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient, "retry", true);
        var evidence = new TransactionalRecoveryEvidence("recovery/lifecycle", "journal", "v1");
        var prepare = new TransactionalPrepareResult(null, failure, evidence);
        var participant = new TransactionalParticipantResult(TransactionalParticipantState.Changed, failure, evidence);
        var status = new TransactionalCommitStatusResult(TransactionalCommitStatus.Unknown, evidence, failure);

        TransactionalParticipantAdapter.ClassifyReturnedFailure(prepare, true).Should().Match<RetryClassification>(x =>
            x.Retryable && x.StateMayHaveChanged && x.Ambiguous);
        TransactionalParticipantAdapter.ClassifyReturnedFailure(participant, true).Should().Match<RetryClassification>(x =>
            x.Retryable && x.StateMayHaveChanged && x.Ambiguous);
        TransactionalParticipantAdapter.ClassifyReturnedFailure(status, true).Should().Match<RetryClassification>(x =>
            x.Retryable && x.StateMayHaveChanged && x.Ambiguous);

        var typed = new TransactionalParticipantException(failure, evidence, true);
        TransactionalParticipantAdapter.ClassifyThrownException(typed, true).Should().Match<RetryClassification>(x =>
            x.Retryable && x.StateMayHaveChanged && x.Ambiguous);
    }

    private static MultiMemoryParticipantRequest Request() =>
        new MultiMemoryTransactionRequest(
            "scope",
            [new MultiMemoryParticipantRequest(MemoryAddress.Parse("/b3"), "b3-op", "b3-kind")],
            [new MultiMemoryTransactionalParticipantRequest(
                MemoryAddress.Parse("/memory/item"), "participant", 1, "participant-key",
                "op", "kind", new Dictionary<string, string>(), new Dictionary<string, string>())])
            .RequestedParticipants("tx")
            .Last();

    private sealed class TestMonotonicClock : IMonotonicClock
    {
        public TimeSpan GetElapsed() => TimeSpan.Zero;
    }

    private sealed class SequenceClock(params TimeSpan[] values) : IMonotonicClock
    {
        private readonly Queue<TimeSpan> _values = new(values);
        public TimeSpan GetElapsed() => _values.Count == 0 ? values[^1] : _values.Dequeue();
    }

    private sealed class FailingClock : IMonotonicClock
    {
        public TimeSpan GetElapsed() => throw new InvalidOperationException("clock unavailable");
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

    private sealed class InvalidClassifier : IRetryClassifier<TestResult>
    {
        public RetryClassification Classify(TestResult result, bool callbackStarted) =>
            new(RetryTerminalState.Success, null, false, false, true, false, null);

        public RetryClassification Classify(Exception exception, bool callbackStarted) =>
            new(RetryTerminalState.Success, null, false, false, true, false, null);
    }

    private sealed class InvalidCancellationClassifier : IRetryClassifier<TestResult>
    {
        public RetryClassification Classify(TestResult result, bool callbackStarted) =>
            new(RetryTerminalState.Success, null, false, false, false, true, null);

        public RetryClassification Classify(Exception exception, bool callbackStarted) =>
            new(RetryTerminalState.Success, null, false, false, false, true, null);
    }

    private sealed class CancellationClassifier : IRetryClassifier<TestResult>
    {
        public RetryClassification Classify(TestResult result, bool callbackStarted) =>
            new(RetryTerminalState.Success, null, false, false, false, false, null);

        public RetryClassification Classify(Exception exception, bool callbackStarted) =>
            new(RetryTerminalState.CancellationAfterCallbackStart, TransactionalFailureCode.Cancelled,
                callbackStarted, callbackStarted, false, true, null);
    }

    private sealed class TestParticipant : ITransactionalMemoryParticipant
    {
        internal Func<TransactionalPrepareContext, CancellationToken, Task<TransactionalPrepareResult>>? Prepare { get; init; }
        internal Func<TransactionalCommitContext, CancellationToken, Task<TransactionalParticipantResult>>? Commit { get; init; }
        internal Func<TransactionalRollbackContext, CancellationToken, Task<TransactionalParticipantResult>>? Rollback { get; init; }
        internal Func<TransactionalCommitContext, CancellationToken, Task<TransactionalCommitStatusResult>>? Status { get; init; }
        internal Scrinia.Plugin.Abstractions.TransactionalRetryPolicy RetryPolicy { get; init; } =
            new(1, initialDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero);

        public string Name => "test";
        public string Version => "1";
        public TransactionalParticipantDescriptor Descriptor => new("participant", 1, ["transaction"], RetryPolicy, "participant-key");
        public Task<TransactionalPrepareResult> PrepareAsync(TransactionalPrepareContext context, CancellationToken cancellationToken = default) => Prepare!(context, cancellationToken);
        public Task<TransactionalParticipantResult> CommitAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) => Commit!(context, cancellationToken);
        public Task<TransactionalParticipantResult> RollbackAsync(TransactionalRollbackContext context, CancellationToken cancellationToken = default) => Rollback!(context, cancellationToken);
        public Task<TransactionalCommitStatusResult> GetCommitStatusAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) => Status!(context, cancellationToken);
    }

    private sealed record TestResult
    {
        private TestResult(string name) => Name = name;
        private string Name { get; }
        internal static readonly TestResult Transient = new("transient");
        internal static readonly TestResult Success = new("success");
    }
}
