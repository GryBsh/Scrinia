namespace Scrinia.Core;

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
        lock (Gate) ct = _shutdown.Token;

        // Counted BEFORE the work can start, so a drain running concurrently with this call
        // cannot observe an empty registry and conclude the work is done.
        Interlocked.Increment(ref _inFlight);

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
    public static async Task<bool> DrainAsync(TimeSpan timeout)
    {
        if (InFlightCount == 0) return true;

        if (await WaitForEmptyAsync(timeout).ConfigureAwait(false))
            return true;

        CancellationTokenSource expired;
        lock (Gate)
        {
            expired = _shutdown;
            // Hand subsequent work a live token — a drain must not poison later writes.
            // Swapping under the lock means Fire never reads a source we are about to dispose.
            _shutdown = new CancellationTokenSource();
        }

        await expired.CancelAsync().ConfigureAwait(false);
        await WaitForEmptyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        expired.Dispose();
        return false;
    }

    /// <summary>
    /// Polls until nothing is outstanding or the budget runs out. Polling (rather than awaiting a
    /// snapshot of tasks) is deliberate: work fired <em>while</em> the drain runs must also be
    /// waited for, and a snapshot would miss it.
    /// </summary>
    private static async Task<bool> WaitForEmptyAsync(TimeSpan budget)
    {
        long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (InFlightCount > 0)
        {
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }
}
