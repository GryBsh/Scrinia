using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Services;

// B5 Task 9 — bounded background maintenance adapter.
//
// One shared Core path, one bounded invocation at a time: the scheduler fires
// at most one FileMemoryMaintenanceService invocation through the unchanged
// void BackgroundWork.Fire boundary, then stops. It never enumerates,
// classifies, deletes, retries, drains unrelated global work, or touches
// plugins. Program owns the single global drain; this scheduler only closes
// its own admission, cancels its active invocation, and awaits it.
internal sealed class MaintenanceScheduler : IDisposable
{
    // Fixed code scheduling defaults. These are code constants, never policy
    // keys: the five approved policy keys live in MaintenancePolicy and the
    // scheduler reads only their loaded result (BackgroundEnabled gate plus
    // fail-closed invalid handling). The startup batch fires immediately on
    // StartMaintenanceScheduler; Period documents the cadence a host timer may
    // use for later single-flight TrySchedule calls.
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    internal delegate Task<MaintenanceResult> MaintenanceInvokeAsync(
        MaintenanceRequest request, CancellationToken cancellationToken);

    private readonly FileMemoryStore _store;
    private readonly IWorkspaceConfig _config;
    private readonly MaintenanceInvokeAsync _invoke;
    private readonly object _gate = new();
    private bool _admissionClosed;
    private bool _disposed;
    private bool _active;
    private CancellationTokenSource? _activeCts;
    private TaskCompletionSource<bool>? _completion;

    internal MaintenanceScheduler(
        FileMemoryStore store, IWorkspaceConfig config, MaintenanceInvokeAsync? invoke = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _invoke = invoke ?? InvokeCoreAsync;
    }

    internal bool IsRunning
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    internal bool AdmissionClosed
    {
        get
        {
            lock (_gate) return _admissionClosed || _disposed;
        }
    }

    // Fires one bounded maintenance invocation via BackgroundWork.Fire.
    // Returns false (zero work) when admission is closed, disposed, already
    // active (single-flight), background-disabled, or policy-invalid.
    internal bool TrySchedule()
    {
        lock (_gate)
        {
            if (_admissionClosed || _disposed || _active)
                return false;
        }

        if (!IsBackgroundEnabled())
            return false;

        TaskCompletionSource<bool> completion;
        CancellationTokenSource activeCts;
        lock (_gate)
        {
            // Recheck under the lock: a concurrent Stop or schedule must win.
            if (_admissionClosed || _disposed || _active)
                return false;
            _active = true;
            activeCts = new CancellationTokenSource();
            _activeCts = activeCts;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
        }

        BackgroundWork.Fire(RunOneAsync, "background maintenance");
        return true;
    }

    // Closes admission, cancels the active invocation, and awaits it.
    // Never drains unrelated global work: Program owns the single global
    // background-work drain. Idempotent and never throws.
    internal Task StopAsync()
    {
        Task? pending;
        CancellationTokenSource? activeCts;
        lock (_gate)
        {
            _admissionClosed = true;
            activeCts = _activeCts;
            pending = _completion?.Task;
        }

        try
        {
            activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return pending ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        Task pending;
        CancellationTokenSource? activeCts;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _admissionClosed = true;
            activeCts = _activeCts;
            pending = _completion?.Task ?? Task.CompletedTask;
        }

        try
        {
            activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // The active invocation runs on the thread pool via
        // BackgroundWork.Fire, never on this thread, so blocking here cannot
        // deadlock the work being awaited. This gives Dispose the same
        // close-cancel-await ordering as StopAsync.
        pending.GetAwaiter().GetResult();

        lock (_gate)
        {
            if (ReferenceEquals(_activeCts, activeCts))
            {
                activeCts?.Dispose();
                _activeCts = null;
            }
        }
    }

    private async Task RunOneAsync(CancellationToken drainToken)
    {
        CancellationToken activeToken;
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            activeToken = _activeCts?.Token ?? CancellationToken.None;
            completion = _completion;
        }

        // Torn down between Fire and run: never run unowned work.
        if (completion is null)
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(drainToken, activeToken);
        try
        {
            // Fixed code scheduling defaults: resolved workspace scopes,
            // configured batch ceiling, no explicit protection, fresh run
            // (never a continuation). Exactly one invocation; a continuation
            // in the result is ignored here — the next startup or interval
            // may schedule a fresh invocation. No recursive Fire, no retry.
            var request = new MaintenanceRequest(null, null, null, null);
            await _invoke(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation/deadline: the current filesystem
            // operation may finish, but no new candidate starts and no
            // advanced cursor is stored. Expected shutdown path: no warning,
            // no retry.
        }
        catch (Exception exception)
        {
            // Fault isolation: a faulting batch is dropped with a sanitized
            // warning. Only the exception type name is logged — never the
            // message, stack, path, content, or secret material — and the
            // batch is never retried from inside the adapter.
            Console.Error.WriteLine(
                $"[scrinia:warn] Background maintenance failed: {exception.GetType().Name}");
        }
        finally
        {
            CancellationTokenSource? owned = null;
            lock (_gate)
            {
                _active = false;
                if (_activeCts is not null)
                {
                    owned = _activeCts;
                    _activeCts = null;
                }
            }

            owned?.Dispose();
            completion.TrySetResult(true);
        }
    }

    private Task<MaintenanceResult> InvokeCoreAsync(
        MaintenanceRequest request, CancellationToken cancellationToken) =>
        new FileMemoryMaintenanceService(_store, _config).ExecuteAsync(request, cancellationToken);

    private bool IsBackgroundEnabled()
    {
        MaintenancePolicyLoadResult loaded;
        try
        {
            loaded = MaintenancePolicy.Load(_config);
        }
        catch (Exception)
        {
            // Fail closed: a config access failure means zero work.
            return false;
        }

        if (!loaded.IsValid || loaded.Policy is null)
            return false;
        return loaded.Policy.BackgroundEnabled;
    }
}
