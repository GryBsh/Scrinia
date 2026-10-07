using Scrinia.Core;
using Scrinia.Core.Bundles;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    private static WorkspaceLease EnterFileEntityAdmission(IMemoryStore store, bool mutation, CancellationToken ct)
    {
        var admission = new WorkspaceAdmission(store.WorkspaceRoot,
            (lease, token) =>
            {
                try { BundleRestoreCoordinator.RecoverUnderLease(lease, BundleReadLimits.Default, token); }
                finally { if (store is FileMemoryStore files) files.InvalidateCaches(); }
            });
        WorkspaceLease lease = mutation
            ? admission.EnterMutation(TimeSpan.FromMinutes(5), ct)
            : admission.EnterRead(TimeSpan.FromMinutes(5), ct);
        if (lease.RecoveryPerformed && store is FileMemoryStore files) files.InvalidateCaches();
        return lease;
    }
}
