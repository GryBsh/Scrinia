using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Services;
using Scrinia.TestDoubles;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Tests;

// B5 Task 9 — bounded background maintenance scheduler (TDD).
// Covers disabled zero-work, single-flight, cancellation/deadline with no
// advanced cursor, fault isolation with sanitized warnings, exact
// inner-then-outer shutdown ordering, no unrelated drain, plugin
// independence, and Serve wiring.
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class MaintenanceSchedulerTests
{
    private static MaintenanceResult EmptyCompletedResult() =>
        new(0, 0, 0, true, false, false, false, false, false, false,
            null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null);

    private static string WellFormedToken()
    {
        byte[] payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(160);
        string encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "b5c2." + encoded;
    }

    private static CoreWorkspaceConfig DefaultConfig() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static CoreWorkspaceConfig DisabledConfig() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Scrinia:Maintenance:BackgroundEnabled"] = "false",
        });

    private static CoreWorkspaceConfig InvalidConfig() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Scrinia:Maintenance:JournalCleanupBatchSize"] = "0",
        });

    private static async Task<bool> WaitForIdleAsync(MaintenanceScheduler scheduler, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (scheduler.IsRunning)
        {
            if (Environment.TickCount64 >= deadline)
                return false;
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                return false;
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    // ── Disabled mode: zero work ────────────────────────────────────────

    [Fact]
    public async Task DisabledMode_SchedulesZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        int calls = 0;
        Task<MaintenanceResult> Invoke(MaintenanceRequest request, CancellationToken token) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 0
                ? EmptyCompletedResult()
                : EmptyCompletedResult());

        using var scheduler = new MaintenanceScheduler(store, DisabledConfig(), Invoke);
        Assert.False(scheduler.TrySchedule());
        Assert.Equal(0, calls);
        Assert.False(scheduler.IsRunning);
        await scheduler.StopAsync().ConfigureAwait(false);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InvalidPolicy_SchedulesZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        int calls = 0;
        Task<MaintenanceResult> Invoke(MaintenanceRequest request, CancellationToken token)
        {
            calls++;
            return Task.FromResult(EmptyCompletedResult());
        }

        using var scheduler = new MaintenanceScheduler(store, InvalidConfig(), Invoke);
        Assert.False(scheduler.TrySchedule());
        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(0, calls);
        Assert.False(scheduler.IsRunning);
        await scheduler.StopAsync().ConfigureAwait(false);
    }

    // ── Single-flight: one invocation at a time ─────────────────────────

    [Fact]
    public async Task SingleFlight_SecondScheduleWhileActiveIsSkipped()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        int calls = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<MaintenanceResult> Invoke(MaintenanceRequest _, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return EmptyCompletedResult();
        }

        using var scheduler = new MaintenanceScheduler(store, DefaultConfig(), Invoke);
        Assert.True(scheduler.TrySchedule());
        Assert.True(await WaitForConditionAsync(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false));

        // Active invocation holds the single flight: second schedule is skipped.
        Assert.False(scheduler.TrySchedule());
        Assert.False(scheduler.TrySchedule());
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult(true);
        Assert.True(await WaitForIdleAsync(scheduler, TimeSpan.FromSeconds(5)).ConfigureAwait(false));

        // After completion the flight is free again: scheduling resumes.
        Assert.True(scheduler.TrySchedule());
        Assert.True(await WaitForConditionAsync(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        await scheduler.StopAsync().ConfigureAwait(false);
        Assert.True(await WaitForIdleAsync(scheduler, TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    // ── Cancellation/deadline: no advanced cursor, no retry ─────────────

    [Fact]
    public async Task Cancellation_StopsActiveWithoutAdvancedCursorOrRetry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        int calls = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        async Task<MaintenanceResult> Invoke(MaintenanceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            observed = ct;
            // Fixed code defaults: fresh invocation, never a continuation.
            Assert.Null(request.ContinuationToken);
            entered.TrySetResult(true);
            // Cooperative: the in-flight filesystem op may finish, but no new
            // candidate starts and no advanced cursor is stored.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new MaintenanceResult(0, 0, 0, true, false, false, false, false, false, false,
                WellFormedToken(), Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null);
        }

        using var scheduler = new MaintenanceScheduler(store, DefaultConfig(), Invoke);
        Assert.True(scheduler.TrySchedule());
        Assert.True(await WaitForConditionAsync(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5)).ConfigureAwait(false));

        await scheduler.StopAsync().ConfigureAwait(false);
        Assert.True(await WaitForIdleAsync(scheduler, TimeSpan.FromSeconds(5)).ConfigureAwait(false));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(observed.CanBeCanceled);

        // No retry after cancellation and no follow-up for the (ignored)
        // continuation: exactly one invocation ever fired.
        await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(scheduler.IsRunning);

        // Admission is closed after Stop: no further scheduling.
        Assert.False(scheduler.TrySchedule());
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // ── Fault isolation: sanitized warning, no retry ────────────────────

    [Fact]
    public async Task Fault_IsolatedWithSanitizedWarningAndNoRetry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        int calls = 0;
        const string secretPath = "/tmp/secret-evil-path-9f3a";
        Task<MaintenanceResult> Invoke(MaintenanceRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException($"boom at {secretPath} with sensitive detail");
        }

        TextWriter previousError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            using var scheduler = new MaintenanceScheduler(store, DefaultConfig(), Invoke);
            Assert.True(scheduler.TrySchedule());
            Assert.True(await WaitForIdleAsync(scheduler, TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            await scheduler.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Equal(1, Volatile.Read(ref calls));
        string warning = captured.ToString();
        Assert.Contains("Background maintenance failed", warning, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive detail", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("boom at", warning, StringComparison.Ordinal);
    }

    // ── Shutdown ordering: inner active first, then outer return ────────

    [Fact]
    public async Task ShutdownOrdering_InnerCompletesBeforeStopReturns()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var sequence = new List<string>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Record(string step)
        {
            lock (sequence) sequence.Add(step);
        }

        async Task<MaintenanceResult> Invoke(MaintenanceRequest _, CancellationToken ct)
        {
            Record("invoke-start");
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptyCompletedResult();
            }
            catch (OperationCanceledException)
            {
                Record("invoke-end");
                throw;
            }
        }

        using var scheduler = new MaintenanceScheduler(store, DefaultConfig(), Invoke);
        Assert.True(scheduler.TrySchedule());
        Assert.True(await WaitForConditionAsync(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5)).ConfigureAwait(false));

        Record("stop-called");
        await scheduler.StopAsync().ConfigureAwait(false);
        Record("stop-returned");

        string[] ordered;
        lock (sequence) ordered = sequence.ToArray();
        Assert.Equal(new[] { "invoke-start", "stop-called", "invoke-end", "stop-returned" }, ordered);
    }

    // ── No unrelated drain ──────────────────────────────────────────────

    [Fact]
    public void Scheduler_NeverDrainsUnrelatedWork()
    {
        string schedulerSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Services", "MaintenanceScheduler.cs"));
        Assert.DoesNotContain("DrainAsync", schedulerSource, StringComparison.Ordinal);

        string serveSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Commands", "ScriniaCommands.Serve.cs"));
        Assert.DoesNotContain("DrainAsync", serveSource, StringComparison.Ordinal);

        string programSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Program.cs"));
        Assert.Equal(1, CountOccurrences(programSource, "DrainAsync"));
    }

    // ── Plugin independence ─────────────────────────────────────────────

    [Fact]
    public void Scheduler_IsIndependentOfPlugins()
    {
        string schedulerSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Services", "MaintenanceScheduler.cs"));

        foreach (string forbidden in new[]
        {
            "McpPluginHost",
            "IScriniaPlugin",
            "IMemoryOperationHook",
            "IMemoryEventSink",
            "MemoryEventSink",
            "SearchContributor",
            "IEmbeddingProvider",
            "Embedding",
            "VectorStore",
            "Plugin",
        })
            Assert.DoesNotContain(forbidden, schedulerSource, StringComparison.Ordinal);

        // The one bounded invocation path owns the Core service directly.
        Assert.Contains("BackgroundWork.Fire", schedulerSource, StringComparison.Ordinal);
        Assert.Contains("FileMemoryMaintenanceService", schedulerSource, StringComparison.Ordinal);
        Assert.Contains("MaintenanceRequest", schedulerSource, StringComparison.Ordinal);
        Assert.Contains("BackgroundEnabled", schedulerSource, StringComparison.Ordinal);
    }

    // ── Serve wiring ────────────────────────────────────────────────────

    [Fact]
    public void Serve_OwnsSchedulerLifecycleWithoutGlobalDrain()
    {
        string serveSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Commands", "ScriniaCommands.Serve.cs"));

        Assert.Contains("StartMaintenanceScheduler", serveSource, StringComparison.Ordinal);
        Assert.Contains("StopMaintenanceSchedulerAsync", serveSource, StringComparison.Ordinal);
        Assert.Contains("try", serveSource, StringComparison.Ordinal);
        Assert.Contains("finally", serveSource, StringComparison.Ordinal);

        // Serve never enumerates, classifies, deletes, or drains: it only
        // starts/stops the bounded adapter around the existing host run.
        foreach (string forbidden in new[]
        {
            "DrainAsync",
            "File.Delete",
            "Directory.Enumerate",
            "ExecuteAsync",
            "FileMemoryMaintenanceService",
        })
            Assert.DoesNotContain(forbidden, serveSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceSetup_OwnsSchedulerSetupWithoutDrain()
    {
        string setupSource = File.ReadAllText(
            Scrinia.TestDoubles.RepoPaths.Combine("src", "Scrinia", "Commands", "WorkspaceSetup.cs"));

        Assert.Contains("StartMaintenanceScheduler", setupSource, StringComparison.Ordinal);
        Assert.Contains("StopMaintenanceSchedulerAsync", setupSource, StringComparison.Ordinal);
        Assert.Contains("MaintenanceScheduler", setupSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DrainAsync", setupSource, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
