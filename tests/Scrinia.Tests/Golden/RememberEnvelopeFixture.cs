using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Drives the four <c>remember</c> whole-memory outcomes through the live internal
/// <see cref="ScriniaMcpTools.Remember"/> facade so golden envelopes can be captured and pinned.
///
/// Each outcome is driven exactly the way the whole-memory fault seam already models it in
/// <c>McpWholeMemoryTransactionTests</c>: Created / Replaced are plain first / second writes to a
/// fresh scope, SuccessWithWarning is an <c>ArchiveCleanup</c> prune failure, and RecoveryRequired is
/// a rollback that cannot be proved (LiveArtifactWrite / RollbackWrite injects). No new fault logic
/// is invented here — the seam is reused.
/// </summary>
internal static class RememberEnvelopeFixture
{
    private const string ActionLabel = "stored";

    public static async Task<McpResponse> RenderAsync(string outcomeTag)
    {
        switch (outcomeTag)
        {
            case "created":
                return await DriveCreatedAsync();
            case "replaced":
                return await DriveReplacedAsync();
            case "success-with-warning":
                return await DriveSuccessWithWarningAsync();
            case "recovery-required":
                return await DriveRecoveryRequiredAsync();
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcomeTag), outcomeTag, "Unknown remember envelope outcome tag.");
        }
    }

    private static async Task<McpResponse> DriveCreatedAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        return await new ScriniaMcpTools().Remember(
            ["first write of a fresh memory"], MemoryAddress.Parse("/b1/remember-created"),
            actionLabel: ActionLabel);
    }

    private static async Task<McpResponse> DriveReplacedAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.Remember(["existing memory"], MemoryAddress.Parse("/b1/remember-replaced"),
            actionLabel: ActionLabel);
        return await tools.Remember(["second write overwrites it"],
            MemoryAddress.Parse("/b1/remember-replaced"), actionLabel: ActionLabel);
    }

    private static async Task<McpResponse> DriveSuccessWithWarningAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        await tools.Remember(["existing memory"], MemoryAddress.Parse("/b1/remember-warning"),
            actionLabel: ActionLabel);
        // Post-commit prune/archive cleanup fails: the whole-memory commit still lands, but the
        // facade promotes the result to success_with_warning.
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);
        return await tools.Remember(["second write with cleanup pending"],
            MemoryAddress.Parse("/b1/remember-warning"), actionLabel: ActionLabel);
    }

    private static async Task<McpResponse> DriveRecoveryRequiredAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.Remember(["existing memory"], MemoryAddress.Parse("/b1/remember-recovery"),
            actionLabel: ActionLabel);
        var prior = FileMemoryStore.WholeMemoryFaultInjector;
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException("injected transaction failure"),
            _ => null
        };
        try
        {
            return await tools.Remember(["second write whose rollback cannot be proved"],
                MemoryAddress.Parse("/b1/remember-recovery"), actionLabel: ActionLabel);
        }
        finally
        {
            FileMemoryStore.WholeMemoryFaultInjector = prior;
        }
    }
}
