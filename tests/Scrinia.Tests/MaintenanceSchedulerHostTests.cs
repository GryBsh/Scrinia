using Scrinia.Commands;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

// B5 Task 4 — the maintenance scheduler lifecycle moved out of WorkspaceSetup
// into its own bounded background adapter. These two tests pin the extraction:
// Start owns the single adapter and Stop returns it to null, and both live on
// MaintenanceSchedulerHost rather than the workspace-identity-only WorkspaceSetup.
public sealed class MaintenanceSchedulerHostTests
{
    [Fact]
    public void Start_WithFileStore_ReturnsScheduler()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = scope.WorkspaceDir;

        var scheduler = MaintenanceSchedulerHost.StartMaintenanceScheduler(root);

        Assert.NotNull(scheduler);
        Assert.Same(scheduler, MaintenanceSchedulerHost.ActiveMaintenanceScheduler);
    }

    [Fact]
    public void Stop_ClearsActiveScheduler()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = scope.WorkspaceDir;

        var scheduler = MaintenanceSchedulerHost.StartMaintenanceScheduler(root);
        MaintenanceSchedulerHost.StopMaintenanceSchedulerAsync().GetAwaiter().GetResult();

        Assert.NotNull(scheduler);
        Assert.Null(MaintenanceSchedulerHost.ActiveMaintenanceScheduler);
    }
}
