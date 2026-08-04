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
            BackgroundWork.Fire(async _ =>
            {
                await Task.Delay(20);
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
        BackgroundWork.Fire(async _ =>
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
}

/// <summary>
/// Serializes tests that observe <see cref="BackgroundWork"/>'s process-wide in-flight registry.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackgroundWorkCollection
{
    public const string Name = "BackgroundWork";
}
