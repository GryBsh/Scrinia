using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.TestProcess;

internal static class AdmissionCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 5 && args[0] is "install" or "install-hold")
            return await RunInstallerAsync(args).ConfigureAwait(false);
        if (args.Length != 2) return 1;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (args[0] is "config" or "config-save" or "tracking" or "add")
            return await RunConfigurationAsync(args[0], args[1], deadline.Token).ConfigureAwait(false);
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
    private static async Task<int> RunInstallerAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FileLock.DefaultLogger = new ContentionLogger();
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(deadline.Token).ConfigureAwait(false);
        bool paused = false;
        try
        {
            Scrinia.Commands.EmbeddingsBundleInstaller.FailureInjector = (phase, _) =>
            {
                if (args[0] == "install-hold" && !paused && phase == Scrinia.Commands.EmbeddingsInstallPhase.BeforeCopy)
                {
                    paused = true;
                    TestProcessProtocol.WriteEventAsync("copying").GetAwaiter().GetResult();
                    TestProcessProtocol.WaitForContinueAsync(deadline.Token).GetAwaiter().GetResult();
                }
                return null;
            };
            var result = Scrinia.Commands.EmbeddingsBundleInstaller.Install(args[1], args[2], args[3], args[4], deadline.Token);
            if (result.Status != Scrinia.Commands.EmbeddingsInstallStatus.Success)
                throw new IOException(result.Message);
            await TestProcessProtocol.WriteEventAsync(result.AlreadyInstalled ? "identical" : "installed").ConfigureAwait(false);
            return 0;
        }
        finally { Scrinia.Commands.EmbeddingsBundleInstaller.FailureInjector = null; }
    }

    private static async Task<int> RunConfigurationAsync(string operation, string root, CancellationToken ct)
    {
        FileLock.DefaultLogger = new ContentionLogger();
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(ct).ConfigureAwait(false);
        try
        {
            if (operation == "config")
                Scrinia.Core.Configuration.WorkspaceConfig.SetValue(root, "config-child", "kept");
            else if (operation == "config-save")
                Scrinia.Core.Configuration.WorkspaceConfig.Save(root, new() { ["config-child"] = "kept" });
            else if (operation == "add")
            {
                string home = Path.Combine(root, "test-home");
                string consumer = Path.Combine(root, "consumer");
                var registry = new Scrinia.Core.Configuration.SharedBaseRegistry(home, consumer);
                var result = new Scrinia.Core.Configuration.SharedBaseOperations(registry, home, consumer).Add("team", root);
                if (result.ErrorCode == Scrinia.Core.Configuration.ConfigurationErrorCode.ConfigRecoveryRequired)
                {
                    await TestProcessProtocol.WriteEventAsync("recovery-required").ConfigureAwait(false);
                    return 4;
                }
                if (!result.Succeeded) throw new InvalidOperationException(result.ErrorCode.ToString(), result.Exception);
            }
            else
            {
                var result = new Scrinia.Core.Migration.LegacyMigrationService().Set(
                    root, Scrinia.Core.Configuration.TrackingPolicy.Local);
                if (!result.Succeeded) throw new InvalidOperationException(result.ErrorCode.ToString(), result.Exception);
            }
        }
        catch (Scrinia.Core.Migration.TrackingRecoveryRequiredException)
        {
            await TestProcessProtocol.WriteEventAsync("recovery-required").ConfigureAwait(false);
            return 4;
        }
        await TestProcessProtocol.WriteEventAsync("completed").ConfigureAwait(false);
        return 0;
    }

    private sealed class ContentionLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 1) return;
            string message = formatter(state, exception);
            string name = message.Contains(".scri-plugin-embeddings.install.lock", StringComparison.Ordinal)
                ? "waiting-install" : message.Contains(".bundle-restore.lock", StringComparison.Ordinal)
                ? "waiting-workspace" : "waiting-config";
            TestProcessProtocol.WriteEventAsync(name).GetAwaiter().GetResult();
        }
    }

}
