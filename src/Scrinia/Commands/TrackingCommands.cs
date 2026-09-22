using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core.Migration;
using ConsoleAppFramework;
using Scrinia.Mcp;
using System.Text.Json;

namespace Scrinia.Commands;

/// <summary>CLI-only argument and presentation helpers for migration commands.</summary>
internal static class TrackingCommands
{
    internal static IReadOnlyList<IReadOnlyList<string>> InspectionArgv(string workspaceRoot) =>
    [
        ["git", "-C", workspaceRoot, "status", "--short"],
        ["git", "-C", workspaceRoot, "ls-files"],
        ["git", "-C", workspaceRoot, "check-ignore", "-v", "--no-index", ".scrinia/config.json", ".scrinia/facts.json"],
    ];

    internal static IReadOnlyList<IReadOnlyList<string>> RegistrationArgv(string root, IReadOnlyList<string> manifest)
    {
        var commands = new List<IReadOnlyList<string>>
        {
            new[] { "git", "-C", root, "add", "--" }.Concat(manifest).ToArray(),
        };
        commands.AddRange(InspectionArgv(root));
        return commands;
    }

    internal static bool TryParseTarget(string? value, out TrackingPolicy policy) => value switch
    {
        "local" => Set(TrackingPolicy.Local, out policy),
        "project-git" => Set(TrackingPolicy.ProjectGit, out policy),
        _ => Set(TrackingPolicy.Unset, out policy, false),
    };

    internal static string ReplaceWorkspaceRoot(string argument, string workspaceRoot) =>
        argument == "<workspace-root>" ? workspaceRoot : argument;

    private static bool Set(TrackingPolicy value, out TrackingPolicy policy, bool result = true)
    {
        policy = value;
        return result;
    }

    // Bulk counterpart of the `/agent/` recall promotion (Task 4): promote every remaining legacy
    // `local:agent` NMP/2 entry to `.scrinia/agent/{subject}.md` through the shared
    // `ScriniaMcpTools.PromoteLocalAgentAsync` primitive. Legacy entries are sidecar-less (only a
    // `.nmp2` body, no `.meta.json`), so they never appear in the sidecar-backed index that
    // `ListScoped` reads — this walks the scope directory's artifact bodies directly instead.
    // Fault-isolated per entry: one bad artifact (decode failure, permission error, coordinator
    // rejection) is swallowed so the sweep finishes the rest, matching the existing
    // `tracking migrate-legacy` "keep going on failure" semantics.
    private static readonly string LocalAgentScope = "local:agent";

    internal static async Task SweepLegacyAgentsAsync(IMemoryStore store, CancellationToken cancellationToken)
    {
        string storeDir = store.GetStoreDirForScope(LocalAgentScope);
        if (!Directory.Exists(storeDir)) return;

        foreach (string nmp2 in Directory.EnumerateFiles(storeDir, "*.nmp2"))
        {
            string subject = Path.GetFileNameWithoutExtension(nmp2);
            try
            {
                MemoryAddress address = MemoryAddress.Parse("/agent/" + subject);
                await ScriniaMcpTools.PromoteLocalAgentAsync(
                    store, address, subject, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Fault-isolated by design: a single legacy entry must not abort the sweep.
            }
        }
    }
}

/// <summary>CLI adapter for local tracking status and explicit policy transitions.</summary>
public sealed class TrackingCliCommands
{
    public int Status(bool json = false)
    {
        WorkspaceSetup.Configure(null);
        string? envSingle = Environment.GetEnvironmentVariable(EffectivePolicy.EnvSingle);
        string? envDouble = Environment.GetEnvironmentVariable(EffectivePolicy.EnvDouble);
        LegacyFilesystemObservation observation = new LegacyMigrationService().Inspect(WorkspaceSetup.Root, envSingle, envDouble);
        string state = observation.State == LegacyMigrationState.Fresh ? "fresh-default-local" : observation.State.ToString().ToLowerInvariant();
        ConfigurationJsonResult output = observation.ErrorCode == ConfigurationErrorCode.None
            ? ConfigurationJsonResult.Success("tracking status", false, tracking: state, state: state,
                gitCommands: TrackingCommands.InspectionArgv(WorkspaceSetup.Root))
            : ConfigurationJsonResult.Failure("tracking status", observation.ErrorCode);
        return Render(output, json, (int)observation.ExitClass);
    }

    public Task<int> Set([Argument] string policy, bool json = false) => Apply(policy, false, json);
    public Task<int> MigrateLegacy(string to, bool json = false) => Apply(to, true, json);

    private static async Task<int> Apply(string value, bool migrate, bool json)
    {
        WorkspaceSetup.Configure(null);
        if (!TrackingCommands.TryParseTarget(value, out TrackingPolicy policy))
            return Render(ConfigurationJsonResult.Failure(migrate ? "tracking migrate-legacy" : "tracking set", ConfigurationErrorCode.CliInvalidArgument), json, 2);
        string? envSingle = Environment.GetEnvironmentVariable(EffectivePolicy.EnvSingle);
        string? envDouble = Environment.GetEnvironmentVariable(EffectivePolicy.EnvDouble);
        var service = new LegacyMigrationService();
        LegacyMigrationResult result = migrate
            ? service.Migrate(WorkspaceSetup.Root, policy, envSingle, envDouble)
            : service.Set(WorkspaceSetup.Root, policy, envSingle, envDouble);
        string command = migrate ? "tracking migrate-legacy" : "tracking set";
        ConfigurationJsonResult output = result.ErrorCode == ConfigurationErrorCode.None
            ? ConfigurationJsonResult.Success(command, result.Changed, tracking: policy.ToString().ToLowerInvariant(),
                state: policy.ToString().ToLowerInvariant(), warnings: result.Warnings,
                gitCommands: TrackingCommands.InspectionArgv(WorkspaceSetup.Root))
            : ConfigurationJsonResult.Failure(command, result.ErrorCode, result.Exception?.Message);

        // Approved F2 legacy cleanup: after a successful tracking migration, bulk-sweep any
        // remaining legacy `local:agent` NMP/2 entries into first-class file entities. Fault-isolated
        // per entry inside the sweep; the config migration outcome above is unchanged.
        if (migrate && result.ErrorCode == ConfigurationErrorCode.None)
            await TrackingCommands.SweepLegacyAgentsAsync(WorkspaceSetup.Store, CancellationToken.None).ConfigureAwait(false);

        return Render(output, json, (int)result.ExitClass);
    }

    private static int Render(ConfigurationJsonResult output, bool json, int exit)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.ConfigurationJsonResult));
        else if (output.ErrorCode is null) Console.WriteLine($"{output.Tracking}\tUNVERIFIED");
        else Console.Error.WriteLine($"{output.ErrorCode}: {output.Error}");
        return exit;
    }
}
