using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>
    /// Walks the user through installing scrinia hooks (SessionStart, UserPromptSubmit) for
    /// each detected agent CLI. Prompts per CLI so the user can opt in selectively;
    /// user-authored hooks in the target config files are preserved (we own only blocks
    /// marked with our sentinel). Invoked by <c>scri setup --hooks</c>.
    /// </summary>
    private static async Task<int> InstallHooksFlowAsync(bool project)
    {
        var scope = project ? HookScope.Project : HookScope.User;
        string? hookWorkspaceRoot = scope == HookScope.Project ? WorkspaceSetup.Root : null;

        AnsiConsole.MarkupLine($"[bold]Agent CLI hooks ({scope})[/]");
        AnsiConsole.MarkupLine(
            "  [dim]SessionStart → `scri restore`, UserPromptSubmit → `scri hint` (tailored per client).[/]");
        int configured = await AgentHookSetup.InstallAsync(scope, hookWorkspaceRoot);

        // Artifacts are workspace-rooted regardless of hook scope — AGENTS.md/CLAUDE.md/plugin live
        // in the repo and travel with it. This also covers OpenCode, which has no shell-hook channel.
        AnsiConsole.MarkupLine("[bold]Workspace artifacts[/]");
        AnsiConsole.MarkupLine(
            "  [dim]AGENTS.md baseline, CLAUDE.md → @AGENTS.md, OpenCode plugin (workspace-rooted).[/]");
        int artifacts = await AgentArtifactSetup.InstallAsync(WorkspaceSetup.Root);

        if (configured == 0 && artifacts == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Nothing installed.[/] Run with at least one supported CLI " +
                "(claude, codex, copilot, opencode) on PATH.");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Installed hooks for {configured} agent CLI(s); wrote {artifacts} workspace artifact(s).[/]");
        return 0;
    }

    /// <summary>Removes scrinia-managed hooks and workspace artifacts at the chosen scope.</summary>
    private static async Task<int> UninstallHooksFlowAsync(bool project)
    {
        var scope = project ? HookScope.Project : HookScope.User;
        string? workspaceRoot = scope == HookScope.Project ? WorkspaceSetup.Root : null;

        AnsiConsole.MarkupLine($"[bold]Removing scrinia hooks ({scope})[/]");
        int removed = await AgentHookSetup.UninstallAsync(scope, workspaceRoot);

        AnsiConsole.MarkupLine("[bold]Removing scrinia workspace artifacts[/]");
        int artifactsRemoved = await AgentArtifactSetup.UninstallAsync(WorkspaceSetup.Root);

        if (removed == 0 && artifactsRemoved == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing scrinia-managed found to remove.[/]");
        }
        return 0;
    }
}
