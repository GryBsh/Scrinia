using Spectre.Console;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Orchestrates static-artifact installation (AGENTS.md, CLAUDE.md, the OpenCode plugin) into a
/// workspace — the artifact analogue of <see cref="AgentHookSetup"/>. Artifacts are workspace-rooted
/// (they travel with the repo), so this takes a workspace root rather than a user/project scope.
///
/// <para>Installer order is significant: <see cref="AgentsMdWriter"/> runs before
/// <see cref="ClaudeMdWriter"/> so the <c>@AGENTS.md</c> import target exists before CLAUDE.md
/// references it.</para>
/// </summary>
public static class AgentArtifactSetup
{
    /// <summary>
    /// Built-in artifact installers, in dependency order (AGENTS.md before its CLAUDE.md importer).
    /// </summary>
    public static IReadOnlyList<IAgentArtifactInstaller> BuiltInInstallers =>
    [
        new AgentsMdWriter(),
        new ClaudeMdWriter(),
        new OpenCodePluginWriter(),
        new SkillWriter(),
    ];

    /// <summary>
    /// Write each applicable artifact under <paramref name="workspaceRoot"/>, prompting per artifact
    /// (unless <paramref name="nonInteractive"/>). Returns the count written.
    /// </summary>
    public static Task<int> InstallAsync(
        string workspaceRoot,
        bool nonInteractive = false,
        IReadOnlyList<IAgentArtifactInstaller>? installers = null)
    {
        installers ??= BuiltInInstallers;

        int written = 0;
        foreach (var installer in installers)
        {
            if (!installer.Applies(workspaceRoot))
                continue;

            bool proceed = nonInteractive
                || AnsiConsole.Confirm(
                    $"Write scrinia [bold]{Markup.Escape(installer.Name)}[/] to the workspace?",
                    defaultValue: true);

            if (!proceed)
            {
                AnsiConsole.MarkupLine($"[yellow]  Skipped {Markup.Escape(installer.Name)}.[/]");
                continue;
            }

            if (installer.Install(workspaceRoot))
            {
                AnsiConsole.MarkupLine($"[green]  Wrote {Markup.Escape(installer.Name)}.[/]");
                written++;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]  Failed to write {Markup.Escape(installer.Name)} — check file permissions.[/]");
            }
        }
        return Task.FromResult(written);
    }

    /// <summary>Remove every scrinia-managed artifact under <paramref name="workspaceRoot"/>.</summary>
    public static Task<int> UninstallAsync(
        string workspaceRoot,
        IReadOnlyList<IAgentArtifactInstaller>? installers = null)
    {
        installers ??= BuiltInInstallers;

        int removed = 0;
        foreach (var installer in installers)
        {
            // Skip artifacts that aren't ours/present so uninstall doesn't report phantom removals.
            if (installer.GetStatus(workspaceRoot) == HookStatus.NotInstalled)
                continue;

            if (installer.Uninstall(workspaceRoot))
            {
                AnsiConsole.MarkupLine($"[green]  Removed {Markup.Escape(installer.Name)}.[/]");
                removed++;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]  Failed to remove {Markup.Escape(installer.Name)}.[/]");
            }
        }
        return Task.FromResult(removed);
    }
}
