using Scrinia.Core.Configuration;

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
