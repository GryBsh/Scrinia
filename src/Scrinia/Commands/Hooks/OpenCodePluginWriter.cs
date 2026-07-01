using Scrinia.Core.Process;
using Scrinia.Mcp;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Generates the OpenCode plugin (<c>.opencode/plugin/scrinia.ts</c>) that delivers the per-turn
/// memory hint. OpenCode has no shell-hook channel, so this is its only path to the hint — but it
/// natively reads AGENTS.md (the baseline) and speaks MCP, so the plugin stays thin: it shells out
/// to <c>scri hint --client opencode</c> via the prompt-aware <c>experimental.chat.messages.transform</c>
/// hook and injects the bare payload. It does <b>not</b> reimplement restore (AGENTS.md covers the baseline).
///
/// <para>The plugin body lives in the embedded <c>plugins/opencode/scrinia.ts</c> template so it can be
/// edited as real TypeScript, independently of this installer. The template carries a
/// <c>$$__SCRI__$$</c> placeholder that install replaces with the absolute <c>scri</c> path (the plugin
/// runs in a child process whose PATH may not contain scrinia).</para>
///
/// <para>The plugin is a scrinia-owned whole file (overwrite on install, delete on uninstall). The
/// hook body is wrapped in <c>try/catch</c> and the API is experimental — a future OpenCode change
/// degrades to "no hint", never a crashed session.</para>
/// </summary>
public sealed class OpenCodePluginWriter : IAgentArtifactInstaller
{
    private const string MarkerLine = "// scrinia-managed: per-turn memory hint plugin";
    private const string EmbeddedPluginPath = "plugins/opencode/scrinia.ts";
    private const string ScriPlaceholder = "$$__SCRI__$$";
    private readonly IProcessRunner _runner;

    public OpenCodePluginWriter(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public string Name => "OpenCode plugin";

    public bool Applies(string workspaceRoot) => _runner.IsExecutableOnPath("opencode");

    public bool Install(string workspaceRoot)
    {
        string path = PluginPath(workspaceRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildPlugin());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Uninstall(string workspaceRoot)
    {
        string path = PluginPath(workspaceRoot);
        if (!File.Exists(path)) return true;
        try
        {
            // Only delete if it's our generated file — never eat a user-authored scrinia.ts.
            if (File.ReadAllText(path).Contains(MarkerLine, StringComparison.Ordinal))
                File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public HookStatus GetStatus(string workspaceRoot)
    {
        string path = PluginPath(workspaceRoot);
        if (!File.Exists(path)) return HookStatus.NotInstalled;

        string content = File.ReadAllText(path);
        if (!content.Contains(MarkerLine, StringComparison.Ordinal)) return HookStatus.NotInstalled;
        // Drift if the baked scri path no longer matches the current executable.
        return content.Contains(ScriPathEscaped(), StringComparison.Ordinal)
            ? HookStatus.Installed : HookStatus.Drift;
    }

    private static string PluginPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".opencode", "plugin", "scrinia.ts");

    /// <summary>
    /// The <c>scri</c> path escaped for embedding inside the template's double-quoted JS string
    /// literal. The plugin calls the exe directly (no shell), so we want the unquoted forward-slash
    /// path that <see cref="AgentHookSetup.ResolveScriExecutablePath"/> produces, with backslashes and
    /// quotes escaped for safe literal embedding (hand-rolled to stay trim-safe — no reflection-based
    /// JSON). The surrounding quotes live in the template, not here.
    /// </summary>
    private static string ScriPathEscaped() =>
        AgentHookSetup.ResolveScriExecutablePath().Trim('"')
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

    /// <summary>
    /// Loads the embedded plugin template and substitutes the <c>$$__SCRI__$$</c> placeholder with the
    /// resolved executable path. The template (<c>plugins/opencode/scrinia.ts</c>) carries the marker
    /// line and all hint logic so it can be edited as real TypeScript, independent of this installer.
    /// </summary>
    private static string BuildPlugin()
    {
        string template = EmbeddedPrompts.Load(EmbeddedPluginPath)
            ?? throw new InvalidOperationException(
                $"Embedded plugin '{EmbeddedPluginPath}' not found — check Scrinia.Mcp resource globs.");
        return template.Replace(ScriPlaceholder, ScriPathEscaped(), StringComparison.Ordinal);
    }
}
