using Scrinia.Core.Process;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Writes the workspace-root <c>CLAUDE.md</c> for Claude Code. Rather than duplicate the baseline,
/// the managed block is a single <c>@AGENTS.md</c> import — Claude resolves relative imports, so the
/// canonical <see cref="AgentsMdWriter"/> content is the one source of truth. Owns a managed markdown
/// region so user CLAUDE.md content is preserved.
///
/// <para>Ordering contract: this must run <b>after</b> <see cref="AgentsMdWriter"/> so the import
/// target always exists — the orchestrator (<see cref="AgentArtifactSetup"/>) enforces it by order.</para>
/// </summary>
public sealed class ClaudeMdWriter : IAgentArtifactInstaller
{
    private const string FileName = "CLAUDE.md";

    /// <summary>The managed-block body: a relative import of the canonical AGENTS.md baseline.</summary>
    internal const string ImportBody = "@AGENTS.md";

    private readonly IProcessRunner _runner;

    public ClaudeMdWriter(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public string Name => "CLAUDE.md → AGENTS.md";

    public bool Applies(string workspaceRoot) => _runner.IsExecutableOnPath("claude");

    public bool Install(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, FileName);
        try
        {
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!ManagedMarkdownBlock.TryUpsert(existing, ImportBody, out string updated))
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
        return body == ImportBody ? HookStatus.Installed : HookStatus.Drift;
    }
}
