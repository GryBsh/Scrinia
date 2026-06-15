using Scrinia.Core.Process;
using Scrinia.Mcp;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Writes the cross-tool <c>scrinia-memory/SKILL.md</c> Agent Skill — the autonomous-invocation
/// vehicle that travels across every SKILL.md-aware CLI nearly unchanged. Same content, multiple
/// destinations (unlike hooks, there is no per-client shape divergence): each detected CLI's skills
/// directory plus the cross-tool <c>~/.agents/skills/</c> path Codex (and Gemini) also read.
///
/// <para>The skill body is a condensed guide; the <b>description</b> in its frontmatter is the only
/// thing pre-loaded into the model's system prompt and is what drives autonomous loading — see
/// <c>prompts/scrinia-memory-skill.md</c>. Whole-file ownership: install overwrites, uninstall deletes.</para>
/// </summary>
public sealed class SkillWriter : IAgentArtifactInstaller
{
    private const string EmbeddedSkillPath = "prompts/scrinia-memory-skill.md";
    private readonly IProcessRunner _runner;

    public SkillWriter(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public string Name => "scrinia-memory SKILL.md";

    public bool Applies(string workspaceRoot) => Destinations(workspaceRoot).Count > 0;

    public bool Install(string workspaceRoot)
    {
        string content = EmbeddedPrompts.Load(EmbeddedSkillPath)
            ?? throw new InvalidOperationException(
                $"Embedded skill '{EmbeddedSkillPath}' not found — check Scrinia.Mcp resource globs.");

        try
        {
            foreach (string path in Destinations(workspaceRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Uninstall(string workspaceRoot)
    {
        try
        {
            foreach (string path in Destinations(workspaceRoot))
            {
                if (File.Exists(path)) File.Delete(path);
                // Tidy the now-empty scrinia-memory/ dir we created.
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public HookStatus GetStatus(string workspaceRoot)
    {
        var dests = Destinations(workspaceRoot);
        if (dests.Count == 0) return HookStatus.NotInstalled;

        int present = dests.Count(File.Exists);
        if (present == 0) return HookStatus.NotInstalled;
        return present == dests.Count ? HookStatus.Installed : HookStatus.Partial;
    }

    /// <summary>
    /// Destinations to write the skill to, conditioned on which CLIs are present. Project skills dirs
    /// for Claude and OpenCode (travel with the repo); the cross-tool <c>~/.agents/skills/</c> path
    /// for Codex (also read by Gemini). Copilot is omitted — its CLI skills-dir convention is
    /// unsettled; it gets the AGENTS.md baseline and the MCP <c>instructions</c> field instead.
    /// </summary>
    private List<string> Destinations(string workspaceRoot)
    {
        var dests = new List<string>();
        if (_runner.IsExecutableOnPath("claude"))
            dests.Add(Path.Combine(workspaceRoot, ".claude", "skills", "scrinia-memory", "SKILL.md"));
        if (_runner.IsExecutableOnPath("opencode"))
            dests.Add(Path.Combine(workspaceRoot, ".opencode", "skills", "scrinia-memory", "SKILL.md"));
        if (_runner.IsExecutableOnPath("codex"))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dests.Add(Path.Combine(home, ".agents", "skills", "scrinia-memory", "SKILL.md"));
        }
        return dests;
    }
}
