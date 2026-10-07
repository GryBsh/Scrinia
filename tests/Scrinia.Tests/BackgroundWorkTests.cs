using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Covers <see cref="BackgroundWork"/>, the registry that replaced the bare
/// <c>_ = Task.Run(...)</c> in <c>MemoryTools.FireEventSinkAsync</c>.
///
/// <para>The defect it fixes: detached sink work was untracked, so nothing could wait for it.
/// Sinks fired by one test kept running into the next one (mutating its store), and were still
/// in flight when the process exited — losing half-written embeddings for real users.</para>
///
/// <para>These tests are sequential (<see cref="BackgroundWorkCollection"/>) because
/// <see cref="BackgroundWork"/> is a process-wide registry, and a concurrent test's in-flight
/// item would be visible to this one's drain.</para>
/// </summary>
[Collection(BackgroundWorkCollection.Name)]
public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task DrainAsync_WaitsForFiredWorkToComplete()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        int completed = 0;
        for (int i = 0; i < 20; i++)
        {
            BackgroundWork.Fire(async cancellationToken =>
            {
                await Task.Delay(20, cancellationToken);
                Interlocked.Increment(ref completed);
            }, "test-work");
        }

        bool drained = await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));

        drained.Should().BeTrue("all work was short and well within the timeout");
        Volatile.Read(ref completed).Should().Be(20, "drain must not return before every fired item finished");
        BackgroundWork.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_ReturnsTrue_WhenNothingInFlight()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task ThrowingWork_IsSwallowed_AndDoesNotFaultDrain()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        BackgroundWork.Fire(_ => throw new InvalidOperationException("boom"), "throws-sync");
        BackgroundWork.Fire(async cancellationToken =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom-async");
        }, "throws-async");

        // The whole point: a misbehaving plugin sink must never take the process with it.
        bool drained = await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));

        drained.Should().BeTrue();
        BackgroundWork.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_CancelsWorkAndReturnsFalse_WhenTimeoutExpires()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));

        var observedCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Models VectorWriteRetry, which backs off 2s + 10s + 30s. Without cancellation a
        // shutdown drain would either block for 42s or abandon the work mid-write.
        BackgroundWork.Fire(async ct =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
        }, "long-retry");

        bool drained = await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(250));

        drained.Should().BeFalse("the work outlasted the drain timeout");
        (await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().BeTrue("an expired drain must cancel outstanding work rather than abandon it");
    }

    [Fact]
    public async Task Fire_AfterAnExpiredDrain_StillRunsToCompletion()
    {
        // A drain cancels the token it handed out; the next Fire must get a fresh one,
        // or every post-shutdown-attempt write would be dead on arrival.
        BackgroundWork.Fire(ct => Task.Delay(TimeSpan.FromMinutes(5), ct), "long-retry");
        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(250))).Should().BeFalse();

        int ran = 0;
        BackgroundWork.Fire(async ct =>
        {
            await Task.Delay(10, ct);
            Interlocked.Increment(ref ran);
        }, "after-drain");

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        Volatile.Read(ref ran).Should().Be(1);
    }

    [Fact]
    public async Task DrainTimeout_DoesNotDisposeTokenWhileAdmittedWorkIsStillRunning()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenUsable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        BackgroundWork.Fire(async cancellationToken =>
        {
            started.TrySetResult(true);
            await release.Task;
            try
            {
                using var waitHandle = cancellationToken.WaitHandle;
                tokenUsable.TrySetResult(waitHandle.WaitOne(0));
            }
            catch (ObjectDisposedException)
            {
                tokenUsable.TrySetResult(false);
            }
        }, "late-token-user");

        (await started.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();

        release.TrySetResult(true);
        (await tokenUsable.Task.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue(
            "a cancellation token remains usable until the admitted work item completes");
    }

    [Fact]
    public async Task DrainAsync_FlushesRegisteredParticipants()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = BackgroundWork.Register(new TestParticipant(_ =>
        {
            flushed.TrySetResult(true);
            return Task.CompletedTask;
        }));

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await flushed.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task DrainAsync_IncludesParticipantWorkRegisteredDuringDrain()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFlushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IBackgroundWorkRegistration? secondRegistration = null;

        using var firstRegistration = BackgroundWork.Register(new TestParticipant(async cancellationToken =>
        {
            firstStarted.TrySetResult(true);
            await Task.Delay(20, cancellationToken);
            secondRegistration = BackgroundWork.Register(new TestParticipant(_ =>
            {
                secondFlushed.TrySetResult(true);
                return Task.CompletedTask;
            }));
        }));

        var drain = BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        (await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        (await drain).Should().BeTrue();
        (await secondFlushed.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        secondRegistration?.Dispose();
    }

    [Fact]
    public async Task DrainAsync_IncludesWorkQueuedDuringItsFinalSnapshot()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var queued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        BackgroundWork.Fire(async cancellationToken =>
        {
            BackgroundWork.Fire(_ =>
            {
                queued.TrySetResult(true);
                return Task.CompletedTask;
            }, "queued-during-drain");
            await Task.Yield();
        }, "admitter");

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await queued.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        BackgroundWork.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_ReschedulesParticipantThatQueuesWorkAfterItsFlushCheck()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        int flushes = 0;
        bool dirty = true;
        using var registration = BackgroundWork.Register(new TestParticipant(_ =>
        {
            if (Interlocked.Increment(ref flushes) == 1)
            {
                dirty = true;
                return Task.CompletedTask;
            }

            dirty = false;
            return Task.CompletedTask;
        }, () => !dirty, () => dirty));

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        Volatile.Read(ref flushes).Should().Be(2,
            "work queued by a participant after its first flush must be admitted to the same drain");
        dirty.Should().BeFalse();
    }

    [Fact]
    public async Task DrainAsync_HoldsAdmissionsAfterFinalQuiescenceUntilDrainReturns()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var admitted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescenceReached = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuiescence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fireInvoked = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        BackgroundWork.BeforeFinalQuiescenceForTests = () =>
        {
            quiescenceReached.TrySetResult(true);
            releaseQuiescence.Task.GetAwaiter().GetResult();
        };
        try
        {
            var drain = BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
            (await quiescenceReached.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
            var fire = Task.Run(() =>
            {
                fireInvoked.TrySetResult(true);
                BackgroundWork.Fire(_ =>
                {
                    admitted.TrySetResult(true);
                    return Task.CompletedTask;
                }, "finalization-race");
            });
            (await fireInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
            releaseQuiescence.TrySetResult(true);

            (await drain).Should().BeTrue();
            admitted.Task.IsCompleted.Should().BeFalse(
                "work arriving after the final quiescence decision must be held until drain has returned");
            await fire;
            BackgroundWork.BeforeFinalQuiescenceForTests = null;
            (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await admitted.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        }
        finally
        {
            BackgroundWork.BeforeFinalQuiescenceForTests = null;
        }
    }

    [Fact]
    public async Task DrainAsync_HoldsRegistrationAfterFinalQuiescenceForTheNextDrain()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var quiescenceReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuiescence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationCreated = new TaskCompletionSource<IBackgroundWorkRegistration>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        BackgroundWork.BeforeFinalQuiescenceForTests = () =>
        {
            quiescenceReached.TrySetResult(true);
            releaseQuiescence.Task.GetAwaiter().GetResult();
        };

        try
        {
            var drain = BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
            (await quiescenceReached.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

            var registrationTask = Task.Run(() =>
            {
                var registration = BackgroundWork.Register(new TestParticipant(_ =>
                {
                    flushed.TrySetResult(true);
                    return Task.CompletedTask;
                }));
                registrationCreated.TrySetResult(registration);
            });

            releaseQuiescence.TrySetResult(true);
            (await drain).Should().BeTrue();
            await registrationTask;
            flushed.Task.IsCompleted.Should().BeFalse();

            using var registration = await registrationCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));
            (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await flushed.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        }
        finally
        {
            BackgroundWork.BeforeFinalQuiescenceForTests = null;
        }
    }

    [Fact]
    public async Task DrainAsync_CancelsTimedOutParticipant()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = BackgroundWork.Register(new TestParticipant(async ct =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { canceled.TrySetResult(true); throw; }
        }));

        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        (await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task RegisteredParticipantAfterTimeoutReceivesLiveToken()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        BackgroundWork.Fire(ct => Task.Delay(TimeSpan.FromMinutes(5), ct), "timeout-before-participant");
        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = BackgroundWork.Register(new TestParticipant(async ct =>
        {
            await Task.Delay(10, ct);
            completed.TrySetResult(true);
        }));

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task DisposedRegistration_IsNotFlushed()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        int flushes = 0;
        var registration = BackgroundWork.Register(new TestParticipant(_ =>
        {
            Interlocked.Increment(ref flushes);
            return Task.CompletedTask;
        }));
        registration.Dispose();

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        Volatile.Read(ref flushes).Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_FlushesParticipantRegisteredInAnotherExecutionContext()
    {
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5));
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<IBackgroundWorkRegistration> registrationTask;
        using (ExecutionContext.SuppressFlow())
        {
            registrationTask = Task.Run(() => BackgroundWork.Register(new TestParticipant(_ =>
            {
                flushed.TrySetResult(true);
                return Task.CompletedTask;
            })));
        }
        IBackgroundWorkRegistration registration = await registrationTask;

        try
        {
            (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await flushed.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        }
        finally
        {
            registration.Dispose();
        }
    }

    [Fact]
    public async Task FileWriteCounter_ObservesExactBytesAcrossExecutionContexts()
    {
        using var counter = new FileWriteCounter();
        Task write;
        using (ExecutionContext.SuppressFlow())
        {
            write = Task.Run(() => FileMemoryStore.WriteObserver?.Invoke("facts.json", new byte[17]));
        }

        await write;
        counter.FactsJsonBytes.Should().Be(17);
    }

    private sealed class TestParticipant(
        Func<CancellationToken, Task> flush,
        Func<bool>? isDisposed = null,
        Func<bool>? hasPendingWork = null)
        : IBackgroundWorkParticipant
    {
        public bool IsDisposed => isDisposed?.Invoke() ?? false;
        public bool HasPendingWork => hasPendingWork?.Invoke() ?? false;
        public Task FlushAsync(CancellationToken cancellationToken) => flush(cancellationToken);
    }
}

/// <summary>
/// Serializes tests that observe <see cref="BackgroundWork"/>'s process-wide in-flight registry.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition; the Collection suffix identifies the shared fixture contract.")]
public sealed class BackgroundWorkCollection
{
    public const string Name = "BackgroundWork";
}
