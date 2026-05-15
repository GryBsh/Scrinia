namespace Scrinia.Commands.Hooks;

/// <summary>
/// State of scrinia-managed hooks within a single agent CLI's config. Reported by
/// <see cref="IAgentHookInstaller.GetStatus"/>; drives the install/uninstall flow in
/// <see cref="AgentHookSetup"/>.
/// </summary>
public enum HookStatus
{
    /// <summary>CLI config file doesn't exist, or has no scrinia-managed blocks.</summary>
    NotInstalled,
    /// <summary>All managed hooks are present and reference the expected commands.</summary>
    Installed,
    /// <summary>Some managed hooks are present but their commands differ from current — the user (or another tool) modified them.</summary>
    Drift,
    /// <summary>Some of the events we manage have our hook, others don't. Re-install will fill in.</summary>
    Partial,
}

/// <summary>Where to install hooks — user-global config or workspace-local.</summary>
public enum HookScope { User, Project }

/// <summary>
/// One hook entry scrinia manages on behalf of an agent CLI. Hook installers map a
/// canonical event name (<see cref="EventName"/> — e.g. <c>SessionStart</c>) to a
/// concrete shell command (<see cref="Command"/>) for the active CLI's hook format.
/// </summary>
/// <param name="EventName">Canonical event name (cross-CLI). Each installer translates.</param>
/// <param name="Command">Shell command line to invoke when the event fires.</param>
public sealed record HookSpec(string EventName, string Command);

/// <summary>
/// Adapter that knows how to write scrinia-managed hooks into one agent CLI's native
/// config file. One implementation per supported CLI (Claude Code, Codex, Copilot). The
/// orchestrator (<see cref="AgentHookSetup"/>) probes each registered installer and
/// prompts the user per CLI on install.
/// </summary>
public interface IAgentHookInstaller
{
    /// <summary>Human-readable label shown in prompts and logs (e.g. "Claude Code").</summary>
    string CliName { get; }

    /// <summary>Cheap probe — does this CLI's exe exist on PATH (or in a known location)?</summary>
    bool IsCliInstalled();

    /// <summary>
    /// Install scrinia-managed hooks into the CLI's config at <paramref name="scope"/>.
    /// User content in the config is preserved; only scrinia-marked blocks are added or
    /// updated. Idempotent — re-running with the same specs is a no-op.
    /// </summary>
    /// <param name="scope">User-global or workspace-local.</param>
    /// <param name="workspaceRoot">Required for <see cref="HookScope.Project"/>; ignored otherwise.</param>
    /// <param name="specs">Canonical event-to-command mappings to install.</param>
    /// <returns>True if write succeeded (or no change needed); false on IO / parse failure.</returns>
    bool InstallHooks(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs);

    /// <summary>
    /// Remove only scrinia-managed blocks at <paramref name="scope"/>. User content is
    /// preserved. Returns true on success (including "nothing to remove").
    /// </summary>
    bool UninstallHooks(HookScope scope, string? workspaceRoot);

    /// <summary>
    /// Inspect the CLI's config at <paramref name="scope"/> and report whether our
    /// managed blocks are present, drifted, or missing — without making any writes.
    /// </summary>
    HookStatus GetStatus(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs);
}
