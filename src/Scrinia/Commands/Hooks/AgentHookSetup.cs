using Spectre.Console;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Orchestrates hook installation across the supported agent CLIs. Walks the registered
/// <see cref="IAgentHookInstaller"/>s, prompts the user per detected CLI, and writes the
/// canonical scrinia hook set (<see cref="DefaultHookSpecs"/>) to each. Lives in
/// <c>Scrinia.Commands.Hooks</c> rather than <c>WorkspaceSetup</c> so the hook surface
/// is a clean unit that can grow without bloating the embeddings/LLM-loading code path.
/// </summary>
public static class AgentHookSetup
{
    /// <summary>
    /// Canonical set of hooks scrinia manages. Each installer translates the canonical
    /// event names into the CLI-specific event name + config-file shape. Adding a new
    /// event here (e.g. <c>UserPromptSubmit</c> for the pre-send relevance hint) makes
    /// every adapter pick it up uniformly.
    ///
    /// <para>Commands embed the full path to the running <c>scri</c> executable rather
    /// than the bare name. Agent CLIs fire hooks in a child shell whose <c>PATH</c> may
    /// not contain scrinia's install directory (especially when the agent is launched
    /// from a desktop shortcut, a different user's terminal, or a CI runner), so relying
    /// on PATH resolution at hook-firing time is fragile. Embedding the absolute path is
    /// resolved once at install time from <see cref="Environment.ProcessPath"/> and
    /// quoted when needed.</para>
    /// </summary>
    public static IReadOnlyList<HookSpec> DefaultHookSpecs => BuildDefaultHookSpecs();

    private static IReadOnlyList<HookSpec> BuildDefaultHookSpecs()
    {
        string scri = ResolveScriExecutablePath();
        return
        [
            // SessionStart → restore. The `--hook` flag wraps the YAML payload in the
            // hookSpecificOutput.additionalContext JSON envelope all three CLIs understand,
            // with a `<scrinia-restored-memory>` tag + imperative framing line so the model
            // reads it as context to attend to rather than a status dump to ignore.
            new HookSpec("SessionStart", $"{scri} restore --hook"),
            // SessionEnd fires once at session termination — NOT after every assistant
            // response. Claude Code's per-turn event is called `Stop`; binding consolidate
            // there would burn an LLM-driven sweep on every turn. Codex has no SessionEnd
            // at all (only per-turn Stop), so its installer reports SupportsEvent
            // ("SessionEnd") = false and the orchestrator emits a one-line skip notice
            // rather than mis-wire to per-turn `Stop`. No --hook flag here: SessionEnd
            // stdout is debug-log-only on Claude Code (never reaches the model) and
            // ignored entirely on Codex / Copilot.
            new HookSpec("SessionEnd", $"{scri} consolidate --auto"),
            // UserPromptSubmit fires `scri hint` which reads the prompt from stdin (each CLI
            // pipes the user's input or a JSON envelope; ExtractPromptFromStdin handles both).
            // Sub-100ms BM25 lookup → emits the hook envelope (default) with an imperative
            // `<scrinia-hint>` payload directing the model to retrieve matching memories.
            new HookSpec("UserPromptSubmit", $"{scri} hint"),
        ];
    }

    /// <summary>
    /// Resolves the absolute path to the running <c>scri</c> executable for embedding in
    /// hook command strings. Uses <see cref="Environment.ProcessPath"/> as the primary
    /// source — that's the path the OS used to launch the current process, which works
    /// uniformly for published single-file deployments, multi-file deployments, and
    /// <c>dotnet tool</c> shims (each places a real exe shim on disk that ProcessPath
    /// reports correctly).
    ///
    /// <para>On Windows, backslashes are converted to forward slashes. Agent CLIs
    /// (notably Claude Code) often execute hook commands via git bash on Windows even
    /// when the user's interactive terminal is configured for PowerShell, and bash
    /// interprets <c>\</c> as an escape character — so a Windows-native path like
    /// <c>C:\Users\nickd\scri.exe</c> gets mangled into <c>C:Usersnickdscri.exe</c>.
    /// Forward slashes work uniformly: bash treats them as literal separators, PowerShell
    /// and cmd accept them too. The path is then shell-quoted when it contains
    /// whitespace — common on Windows under <c>C:/Program Files/</c> or user-profile
    /// paths with spaces — so the hook's command field doesn't tokenise mid-path.</para>
    ///
    /// <para>Falls back to the bare <c>scri</c> name if <see cref="Environment.ProcessPath"/>
    /// is somehow null (extremely unlikely on net6.0+) — preserves the previous behavior
    /// rather than crashing.</para>
    /// </summary>
    internal static string ResolveScriExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return "scri";

        if (OperatingSystem.IsWindows())
            processPath = processPath.Replace('\\', '/');

        return processPath.Contains(' ', StringComparison.Ordinal)
            ? $"\"{processPath}\""
            : processPath;
    }

    /// <summary>
    /// Built-in installer set covering the three big-3 agent CLIs. Override via the
    /// optional parameter on <see cref="InstallAsync"/> for tests / future plugin
    /// registration.
    /// </summary>
    public static IReadOnlyList<IAgentHookInstaller> BuiltInInstallers =>
    [
        new ClaudeCodeHookInstaller(),
        new CodexHookInstaller(),
        new CopilotHookInstaller(),
    ];

    /// <summary>
    /// Detect each registered CLI, prompt the user per-detected-CLI, and write hooks to
    /// the chosen scope. Returns the count of CLIs that were configured (so the caller
    /// can report "Configured hooks for N agent CLI(s)").
    /// </summary>
    public static Task<int> InstallAsync(
        HookScope scope,
        string? workspaceRoot,
        bool nonInteractive = false,
        IReadOnlyList<IAgentHookInstaller>? installers = null,
        IReadOnlyList<HookSpec>? specs = null)
    {
        installers ??= BuiltInInstallers;
        specs ??= DefaultHookSpecs;

        int configured = 0;
        foreach (var installer in installers)
        {
            if (!installer.IsCliInstalled())
            {
                AnsiConsole.MarkupLine(
                    $"[dim]  {Markup.Escape(installer.CliName)} not on PATH — skipping.[/]");
                continue;
            }

            bool proceed = nonInteractive
                || AnsiConsole.Confirm(
                    $"Install scrinia hooks for [bold]{Markup.Escape(installer.CliName)}[/]?",
                    defaultValue: true);

            if (!proceed)
            {
                AnsiConsole.MarkupLine($"[yellow]  Skipped {Markup.Escape(installer.CliName)}.[/]");
                continue;
            }

            // Filter the universal spec set down to events this CLI actually supports.
            // Surface unsupported ones inline so the user knows why (e.g. Codex doesn't
            // have a SessionEnd event, so the consolidate hook is reported as skipped).
            var supportedSpecs = new List<HookSpec>(specs.Count);
            foreach (var spec in specs)
            {
                if (installer.SupportsEvent(spec.EventName))
                {
                    supportedSpecs.Add(spec);
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]  {Markup.Escape(installer.CliName)}: " +
                        $"{Markup.Escape(spec.EventName)} not supported by this CLI — skipping that hook.[/]");
                }
            }

            if (supportedSpecs.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]  {Markup.Escape(installer.CliName)}: no supported hooks; nothing to install.[/]");
                continue;
            }

            bool ok = installer.InstallHooks(scope, workspaceRoot, supportedSpecs);
            if (ok)
            {
                AnsiConsole.MarkupLine(
                    $"[green]  Installed hooks for {Markup.Escape(installer.CliName)} ({scope}).[/]");
                configured++;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]  Failed to write hooks for {Markup.Escape(installer.CliName)} — check file permissions.[/]");
            }
        }
        return Task.FromResult(configured);
    }

    /// <summary>
    /// Uninstall scrinia-managed hooks across every detected CLI at the given scope.
    /// User-authored hooks are preserved.
    /// </summary>
    public static Task<int> UninstallAsync(
        HookScope scope,
        string? workspaceRoot,
        IReadOnlyList<IAgentHookInstaller>? installers = null)
    {
        installers ??= BuiltInInstallers;

        int removed = 0;
        foreach (var installer in installers)
        {
            if (!installer.IsCliInstalled())
                continue;
            bool ok = installer.UninstallHooks(scope, workspaceRoot);
            if (ok)
            {
                AnsiConsole.MarkupLine(
                    $"[green]  Uninstalled hooks for {Markup.Escape(installer.CliName)} ({scope}).[/]");
                removed++;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]  Failed to remove hooks for {Markup.Escape(installer.CliName)}.[/]");
            }
        }
        return Task.FromResult(removed);
    }
}
