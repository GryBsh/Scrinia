using Scrinia.Core;
using Scrinia.Services;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Commands;

/// <summary>
/// Bounded background maintenance lifecycle for the CLI process. Extracted from the former
/// <c>WorkspaceSetup</c> composition root so that type owns only workspace identity.
/// </summary>
internal static class MaintenanceSchedulerHost
{
    private static MaintenanceScheduler? _maintenanceScheduler;

    /// <summary>
    /// The active bounded background maintenance adapter, if one was started
    /// for this process. Owned by <see cref="StartMaintenanceScheduler"/> and
    /// <see cref="StopMaintenanceSchedulerAsync"/>; the single global
    /// background-work drain stays in <c>Program</c>.
    /// </summary>
    internal static MaintenanceScheduler? ActiveMaintenanceScheduler => _maintenanceScheduler;

    /// <summary>
    /// Starts the B5 bounded background adapter: one single-flight
    /// <see cref="MaintenanceScheduler"/> over the configured file store that
    /// fires at most one Core maintenance invocation via
    /// <c>BackgroundWork.Fire</c>. Returns <c>null</c> when no file-backed
    /// store is configured. A background-disabled or invalid policy schedules
    /// zero work but still returns the scheduler so shutdown stays ordered.
    /// </summary>
    internal static MaintenanceScheduler? StartMaintenanceScheduler(string workspaceRoot)
    {
        if (MemoryStoreContext.Current is not FileMemoryStore fileStore &&
            MemoryStoreContext.Default is not FileMemoryStore)
            return null;
        FileMemoryStore store = MemoryStoreContext.Current as FileMemoryStore
            ?? (FileMemoryStore)MemoryStoreContext.Default!;
        var scheduler = new MaintenanceScheduler(
            store, CoreWorkspaceConfig.Load(workspaceRoot));
        _maintenanceScheduler = scheduler;
        scheduler.TrySchedule();
        return scheduler;
    }

    /// <summary>
    /// Stops the active background adapter inner-first (close admission,
    /// cancel and await its active invocation), then disposes it. Never
    /// drains unrelated global work — the outer global drain stays in
    /// <c>Program</c>.
    /// </summary>
    internal static async Task StopMaintenanceSchedulerAsync()
    {
        MaintenanceScheduler? scheduler = _maintenanceScheduler;
        _maintenanceScheduler = null;
        if (scheduler is null)
            return;
        await scheduler.StopAsync().ConfigureAwait(false);
        scheduler.Dispose();
    }
}
