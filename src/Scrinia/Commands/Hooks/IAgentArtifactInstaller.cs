namespace Scrinia.Commands.Hooks;

/// <summary>
/// Adapter that writes one kind of scrinia-managed <em>static artifact</em> into a workspace —
/// a generated file the agent reads as always-on context (AGENTS.md, CLAUDE.md), or a generated
/// integration shim (the OpenCode plugin, SKILL.md). This is the second integration class
/// alongside <see cref="IAgentHookInstaller"/> (shell hooks): artifacts cover clients that have no
/// shell-hook channel (OpenCode) and the always-on baseline that a per-turn hook can't carry.
///
/// <para>Unlike hooks, artifacts are <b>workspace-rooted</b> — they live in the selected workspace,
/// where sharing follows the user's workflow — so the
/// install surface takes a workspace root rather than a user/project <see cref="HookScope"/>.</para>
///
/// <para>Reuses <see cref="HookStatus"/> for reporting. Writers that own a region of a shared file
/// (AGENTS.md, CLAUDE.md) use <see cref="ManagedMarkdownBlock"/> for idempotent insert/update that
/// preserves user content; writers that own a whole file (the plugin, SKILL.md) overwrite/delete.</para>
/// </summary>
public interface IAgentArtifactInstaller
{
    /// <summary>Human-readable label shown in prompts and logs (e.g. "AGENTS.md").</summary>
    string Name { get; }

    /// <summary>
    /// Whether this artifact is relevant given the CLIs present on the system / the workspace.
    /// E.g. the OpenCode plugin only applies when <c>opencode</c> is on PATH; AGENTS.md applies
    /// whenever any AGENTS.md-consuming CLI is present (it is also the import target for CLAUDE.md).
    /// </summary>
    bool Applies(string workspaceRoot);

    /// <summary>
    /// Write (or update) the artifact under <paramref name="workspaceRoot"/>. Idempotent: re-running
    /// updates the scrinia-managed content in place without disturbing user content. Returns true on
    /// success (including "no change needed"), false on IO failure.
    /// </summary>
    bool Install(string workspaceRoot);

    /// <summary>Remove only scrinia-managed content. Returns true on success (incl. "nothing to remove").</summary>
    bool Uninstall(string workspaceRoot);

    /// <summary>Report whether the scrinia-managed content is present, drifted, or missing — no writes.</summary>
    HookStatus GetStatus(string workspaceRoot);
}
