using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.TestProcess;

internal static class TrackingCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) return 1;
        string root = args[1];
        if (args[0].StartsWith("recover-", StringComparison.Ordinal))
        {
            LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(root, null, null);
            LegacyMigrationState expected = args[0] == "recover-local" ? LegacyMigrationState.Local : LegacyMigrationState.ProjectGit;
            if (result.ErrorCode != ConfigurationErrorCode.None || result.State != expected) return 2;
            await TestProcessProtocol.WriteEventAsync("recovered").ConfigureAwait(false);
            return 0;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(deadline.Token).ConfigureAwait(false);
        if (args[0] == "prepared-crash")
            DurableFile.FailureInjector = (phase, path) =>
            {
                if (phase == DurableWritePhase.BeforeWrite && Path.GetFileName(path) == "config.json")
                    Environment.Exit(73);
                return null;
            };
        else if (args[0] == "committed-crash")
            DurableFile.DeleteFailureInjector = (phase, path) =>
            {
                if (phase == DurableDeletePhase.BeforeDelete && Path.GetFileName(path) == ".tracking-recovery.json")
                    Environment.Exit(73);
                return null;
            };
        else return 1;
        try { new LegacyMigrationService().Set(root, TrackingPolicy.ProjectGit); }
        finally { DurableFile.FailureInjector = null; DurableFile.DeleteFailureInjector = null; }
        return 3; // The requested crash boundary must be reached.
    }
}
