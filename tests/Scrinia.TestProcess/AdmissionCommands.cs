using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.TestProcess;

internal static class AdmissionCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) return 1;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (args[0] != "hold")
        {
            using var store = new FileMemoryStore(args[1]);
            await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
            await TestProcessProtocol.WaitForContinueAsync(deadline.Token).ConfigureAwait(false);
            await TestProcessProtocol.WriteEventAsync("entered").ConfigureAwait(false);
            using var operation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                MemoryAddress address = MemoryAddress.Parse("/concurrent");
                var write = new MemoryWrite(address, ["survives"u8.ToArray()],
                    new Dictionary<string, IReadOnlyList<string>>(), []);
                switch (args[0])
                {
                    case "store": await store.StoreAsync(write, operation.Token).ConfigureAwait(false); break;
                    case "append": await store.AppendAsync(write, operation.Token).ConfigureAwait(false); break;
                    case "read": await store.ReadArtifactAsync(address, operation.Token).ConfigureAwait(false); break;
                    default: return 1;
                }
                await TestProcessProtocol.WriteEventAsync("completed").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TestProcessProtocol.WriteEventAsync("blocked").ConfigureAwait(false);
            }
            return 0;
        }
        var admission = new WorkspaceAdmission(args[1], (lease, ct) =>
            BundleRestoreCoordinator.RecoverUnderLease(lease, BundleReadLimits.Default, ct));
        using var held = admission.EnterRestore(TimeSpan.FromSeconds(10), deadline.Token);
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(deadline.Token).ConfigureAwait(false);
        await TestProcessProtocol.WriteEventAsync("completed").ConfigureAwait(false);
        return 0;
    }
}
