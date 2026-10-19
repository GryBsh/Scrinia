namespace Scrinia.Core;

internal interface IBackgroundWorkParticipant
{
    Task FlushAsync(CancellationToken cancellationToken);
    bool IsDisposed { get; }
    bool HasPendingWork { get; }
}

internal interface IBackgroundWorkRegistration : IDisposable { }

/// <summary>
/// Tracks detached background work — currently <see cref="IMemoryEventSink"/> dispatch from the
/// MCP write path — so it can be waited on instead of abandoned.
///
/// <para>Write responses still return without waiting for embedding latency; the difference is
/// that the work is now <em>owned</em>. <see cref="DrainAsync"/> lets the CLI finish outstanding
/// indexing before the process exits (previously a write could return, the user's shell prompt
/// come back, and the process die with vectors half-written), and lets tests wait for a write's
/// side effects instead of racing them.</para>
///
/// <para>Work items receive a shutdown <see cref="CancellationToken"/>. Sinks can back off for a
/// long time — <c>VectorWriteRetry</c> waits 2s + 10s + 30s — so a drain that runs out of patience
/// cancels rather than blocking or abandoning mid-write.</para>
/// </summary>
public static class BackgroundWork
{
    private static readonly Lock Gate = new();
    private static CancellationTokenSource _shutdown = new();
    private static int _inFlight;
    private static readonly HashSet<Registration> Participants = [];
    private static readonly SemaphoreSlim DrainGate = new(1, 1);
    private static bool _finalizing;
    private static readonly Queue<(Func<CancellationToken, Task> Work, string Description)> DeferredWork = [];
    private static readonly Queue<Registration> DeferredRegistrations = [];

    private sealed class Registration(IBackgroundWorkParticipant participant) : IBackgroundWorkRegistration
    {
        private IBackgroundWorkParticipant? _participant = participant;
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool TryGetParticipant(out IBackgroundWorkParticipant value)
        {
            if (Volatile.Read(ref _participant) is { } target)
            {
                value = target;
                return true;
            }

            value = null!;
            return false;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _participant, null);
            lock (Gate) Participants.Remove(this);
        }
    }

    internal static IBackgroundWorkRegistration Register(IBackgroundWorkParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        var registration = new Registration(participant);
        lock (Gate)
        {
            if (_finalizing)
                DeferredRegistrations.Enqueue(registration);
            else
                Participants.Add(registration);
        }
        return registration;
    }

    internal static Action? BeforeFinalQuiescenceForTests { get; set; }
    internal static Action<Task<bool>>? BeforeDeferredWorkReleaseForTests { get; set; }
    internal static Action<bool>? DrainGateWaitObservedForTests { get; set; }

    /// <summary>Number of work items currently outstanding. Diagnostics and tests only.</summary>
    public static int InFlightCount => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool and counts it as outstanding until it
    /// finishes. Never throws: a work item that faults is logged to stderr and dropped, because a
    /// plugin sink must not be able to take the process down.
    /// </summary>
    /// <param name="work">The work, given a token cancelled when a drain gives up.</param>
    /// <param name="description">Short label used in the stderr warning if the work faults.</param>
    public static void Fire(Func<CancellationToken, Task> work, string description)
    {
        ArgumentNullException.ThrowIfNull(work);

        CancellationToken ct;
        lock (Gate)
        {
            if (_finalizing)
            {
                DeferredWork.Enqueue((work, description));
                return;
            }

            ct = _shutdown.Token;
            // Admission and token capture are one synchronized operation. A final drain snapshot
            // cannot pass between reading an empty registry and admitting this work.
            Interlocked.Increment(ref _inFlight);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when a drain expires — the caller already reported the timeout.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Background work '{description}' failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Waits for outstanding work to finish, up to <paramref name="timeout"/>.
    /// Returns <see langword="true"/> if everything completed. On expiry, cancels the outstanding
    /// items' token, gives them a brief grace period to unwind, and returns <see langword="false"/>.
    /// </summary>
    public static Task<bool> DrainAsync(TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A hook installed for a later invocation must not intercept this drain's finalization.
        var beforeDeferredRelease = BeforeDeferredWorkReleaseForTests;
        var drainGateWaitObserved = DrainGateWaitObservedForTests;
        _ = Task.Run(() => CompleteDrainAsync(timeout, completion, beforeDeferredRelease, drainGateWaitObserved));
        return completion.Task;
    }

    private static async Task CompleteDrainAsync(
        TimeSpan timeout,
        TaskCompletionSource<bool> completion,
        Action<Task<bool>>? beforeDeferredRelease,
        Action<bool>? drainGateWaitObserved)
    {
        try
        {
            await WaitForDrainAdmissionAsync(drainGateWaitObserved).ConfigureAwait(false);
            bool drained = await DrainCoreAsync(timeout).ConfigureAwait(false);
            // Completion is the public boundary. Deferred work may become eligible afterwards,
            // even before an asynchronously scheduled caller continuation resumes.
            completion.TrySetResult(drained);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            try
            {
                beforeDeferredRelease?.Invoke(completion.Task);
            }
            finally
            {
                try
                {
                    ReleaseDeferredWork();
                }
                finally
                {
                    // Another drain must observe deferred replay before taking its snapshot.
                    DrainGate.Release();
                }
            }
        }
    }

    private static async Task<bool> DrainCoreAsync(TimeSpan timeout)
    {
        var scheduled = new HashSet<Registration>();
        var completed = new HashSet<Registration>();
        if (await WaitForEmptyAsync(timeout, scheduled, completed).ConfigureAwait(false))
            return true;

        CancellationTokenSource expired;
        lock (Gate)
        {
            _finalizing = false;
            expired = _shutdown;
            // Hand subsequent work a live token — a drain must not poison later writes.
            // Swapping under the lock means Fire never reads a source we are about to dispose.
            _shutdown = new CancellationTokenSource();
        }

        await expired.CancelAsync().ConfigureAwait(false);
        await WaitForEmptyAsync(TimeSpan.FromSeconds(2), scheduled, completed).ConfigureAwait(false);
        // Keep the retired source alive until process exit. An admitted item can still be
        // holding its token after the grace period, and disposing it would corrupt that work.
        return false;
    }

    private static async Task WaitForDrainAdmissionAsync(Action<bool>? drainGateWaitObserved)
    {
        Task admission = DrainGate.WaitAsync();
        try
        {
            drainGateWaitObserved?.Invoke(admission.IsCompleted);
        }
        finally
        {
            await admission.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until nothing is outstanding or the budget runs out. Polling (rather than awaiting a
    /// snapshot of tasks) is deliberate: work fired <em>while</em> the drain runs must also be
    /// waited for, and a snapshot would miss it.
    /// </summary>
    private static async Task<bool> WaitForEmptyAsync(
        TimeSpan budget,
        HashSet<Registration> scheduled,
        HashSet<Registration> completed)
    {
        long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (true)
        {
            if (ScheduleParticipantsAndCheckForQuiescence(scheduled, completed)) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool ScheduleParticipantsAndCheckForQuiescence(
        HashSet<Registration> scheduled,
        HashSet<Registration> completed)
    {
        Registration[] registrations;
        CancellationToken shutdownToken;
        lock (Gate)
        {
            registrations = Participants
                .Where(registration => !registration.IsDisposed
                    && registration.TryGetParticipant(out var participant)
                    && !participant.IsDisposed)
                .Where(registration => !completed.Contains(registration)
                    || registration.TryGetParticipant(out var participant)
                    && participant.HasPendingWork)
                .Where(scheduled.Add)
                .ToArray();
            shutdownToken = _shutdown.Token;
            Interlocked.Add(ref _inFlight, registrations.Length);

            // This is the drain's linearization point. Fire and Register both take Gate, so work
            // admitted before this check is counted and anything admitted after it belongs to the
            // next drain rather than being mistaken for already-quiescent work.
            if (InFlightCount == 0)
            {
                if (registrations.Length == 0)
                {
                    BeforeFinalQuiescenceForTests?.Invoke();
                    _finalizing = true;
                    return true;
                }
            }
        }

        foreach (var registration in registrations)
            StartParticipantFlush(registration, scheduled, completed, shutdownToken);

        return false;
    }

    private static void ReleaseDeferredWork()
    {
        (Func<CancellationToken, Task> Work, string Description)[] deferred;
        Registration[] deferredRegistrations;
        lock (Gate)
        {
            _finalizing = false;
            deferredRegistrations = [.. DeferredRegistrations];
            DeferredRegistrations.Clear();
            foreach (var registration in deferredRegistrations)
                Participants.Add(registration);
            deferred = [.. DeferredWork];
            DeferredWork.Clear();
        }

        foreach (var (work, description) in deferred)
            Fire(work, description);
    }

    private static void StartParticipantFlush(
        Registration registration,
        HashSet<Registration> scheduled,
        HashSet<Registration> completed,
        CancellationToken shutdownToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!registration.TryGetParticipant(out var participant) || participant.IsDisposed)
                    return;
                await participant.FlushAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Background work 'participant flush' failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (Gate)
                {
                    // A flush may discover and queue more participant work after the drain's
                    // previous admission check. Remove the completed admission before dropping
                    // the in-flight count so the next check can admit that work atomically.
                    scheduled.Remove(registration);
                    if (registration.TryGetParticipant(out var completedParticipant)
                        && !completedParticipant.HasPendingWork)
                        completed.Add(registration);
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }, CancellationToken.None);
    }
}
