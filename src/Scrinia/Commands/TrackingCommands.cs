using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;
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
}

/// <summary>CLI adapter for local tracking status and explicit policy transitions.</summary>
public sealed class TrackingCliCommands
{
    public int Status(bool json = false)
    {
        WorkspaceSetup.Configure(null);
        LegacyFilesystemObservation observation = new LegacyMigrationService().Inspect(WorkspaceSetup.Root);
        string state = observation.State == LegacyMigrationState.Fresh ? "fresh-default-local" : observation.State.ToString().ToLowerInvariant();
        return Render(new CliTrackingOutput("tracking status", state, observation.ErrorCode.ToString().ToUpperInvariant()), json, (int)observation.ExitClass);
    }

    public int Set(string policy, bool json = false) => Apply(policy, false, json);
    public int MigrateLegacy(string to, bool json = false) => Apply(to, true, json);

    private static int Apply(string value, bool migrate, bool json)
    {
        WorkspaceSetup.Configure(null);
        if (!TrackingCommands.TryParseTarget(value, out TrackingPolicy policy))
            return Render(new CliTrackingOutput(migrate ? "tracking migrate-legacy" : "tracking set", "", "CLI_INVALID_ARGUMENT"), json, 2);
        var service = new LegacyMigrationService();
        LegacyMigrationResult result = migrate
            ? service.Migrate(WorkspaceSetup.Root, policy)
            : LegacyMigrationStateMachine.ApplyCommand(service.Inspect(WorkspaceSetup.Root).State, policy, isMigration: false);
        return Render(new CliTrackingOutput(migrate ? "tracking migrate-legacy" : "tracking set", policy.ToString().ToLowerInvariant(), result.ErrorCode.ToString().ToUpperInvariant()), json, (int)result.ExitClass);
    }

    private static int Render(CliTrackingOutput output, bool json, int exit)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliTrackingOutput));
        else Console.WriteLine($"{output.Tracking}\tUNVERIFIED");
        return exit;
    }
}

internal sealed record CliTrackingOutput(string Command, string Tracking, string ErrorCode);
