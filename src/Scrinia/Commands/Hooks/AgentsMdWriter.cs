using Scrinia.Core.Process;
using Scrinia.Mcp;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Writes the canonical <c>AGENTS.md</c> at the workspace root — the always-on baseline read
/// natively by Codex, GitHub Copilot, and OpenCode (none of which read <c>CLAUDE.md</c>), and the
/// import target for the <see cref="ClaudeMdWriter"/> block. Owns a managed markdown region
/// (<see cref="ManagedMarkdownBlock"/>) so a user's own AGENTS.md content is preserved.
///
/// <para>Content is the shared <see cref="ScriniaPrimer.MarkdownBody"/> — short, pointing at
/// <c>guide()</c> rather than duplicating the full agent guide.</para>
/// </summary>
public sealed class AgentsMdWriter : IAgentArtifactInstaller
{
    private const string FileName = "AGENTS.md";
    private readonly IProcessRunner _runner;

    public AgentsMdWriter(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public string Name => "AGENTS.md";

    /// <summary>
    /// Applies whenever any supported agent CLI is present — AGENTS.md is the canonical baseline
    /// for the non-Claude CLIs and the import target Claude's CLAUDE.md points at.
    /// </summary>
    public bool Applies(string workspaceRoot) =>
        _runner.IsExecutableOnPath("codex")
        || _runner.IsExecutableOnPath("copilot")
        || _runner.IsExecutableOnPath("opencode")
        || _runner.IsExecutableOnPath("claude");

    public bool Install(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, FileName);
        try
        {
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!ManagedMarkdownBlock.TryUpsert(existing, ScriniaPrimer.MarkdownBody, out string updated))
                return false;
            File.WriteAllText(path, updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Uninstall(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, FileName);
        if (!File.Exists(path)) return true;
        try
        {
            if (!ManagedMarkdownBlock.TryRemove(File.ReadAllText(path), out string? remaining))
                return false;
            if (remaining is null) File.Delete(path);
            else File.WriteAllText(path, remaining);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public HookStatus GetStatus(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, FileName);
        if (!File.Exists(path)) return HookStatus.NotInstalled;

        string content = File.ReadAllText(path);
        string? body = ManagedMarkdownBlock.ExtractBody(content);
        if (body is null) return ManagedMarkdownBlock.Contains(content) ? HookStatus.Drift : HookStatus.NotInstalled;
        return body == ScriniaPrimer.MarkdownBody.Trim() ? HookStatus.Installed : HookStatus.Drift;
    }
}
