using System.Text.Json;
using System.Text.Json.Nodes;
using Scrinia.Core.Process;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Hook installer for GitHub Copilot CLI. Writes a dedicated <c>scrinia.json</c> hook
/// file inside Copilot's hooks directory — Copilot loads every <c>*.json</c> file in
/// the hooks dir, so having our own file means we never have to merge-with or sentinel
/// against user-authored content. Install creates / overwrites our file; uninstall
/// deletes it.
///
/// <para>Paths:</para>
/// <list type="bullet">
/// <item>User scope: <c>~/.copilot/hooks/scrinia.json</c> (or <c>$COPILOT_HOME/hooks/</c> if set).</item>
/// <item>Project scope: <c>&lt;workspace&gt;/.github/hooks/scrinia.json</c> — committable so teams share the integration.</item>
/// </list>
///
/// <para>Event-name mapping: Copilot uses camelCase (<c>sessionStart</c>,
/// <c>sessionEnd</c>) where Claude Code and Codex use PascalCase
/// (<c>SessionStart</c>, <c>Stop</c>). The canonical <see cref="HookSpec.EventName"/>
/// values (PascalCase) are translated per-event by <see cref="ToCopilotEvent"/>.</para>
/// </summary>
public sealed class CopilotHookInstaller : IAgentHookInstaller
{
    private const string FileName = "scrinia.json";
    private readonly IProcessRunner _runner;

    public CopilotHookInstaller(IProcessRunner? runner = null)
    {
        _runner = runner ?? new ProcessRunner();
    }

    public string CliName => "GitHub Copilot";

    public bool IsCliInstalled() => _runner.IsExecutableOnPath("copilot");

    public bool InstallHooks(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs)
    {
        string path = ResolveConfigPath(scope, workspaceRoot);
        try
        {
            var hooks = new JsonObject();
            foreach (var spec in specs)
            {
                string copilotEvent = ToCopilotEvent(spec.EventName);
                if (copilotEvent.Length == 0) continue;  // unmapped event — skip silently

                var hookEntry = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = spec.Command,
                };
                var hooksArr = new JsonArray();
                hooksArr.Add((JsonNode)hookEntry);

                var block = new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = hooksArr,
                };
                var blockArr = new JsonArray();
                blockArr.Add((JsonNode)block);
                hooks[copilotEvent] = blockArr;
            }

            var root = new JsonObject { ["hooks"] = hooks };
            Save(path, root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool UninstallHooks(HookScope scope, string? workspaceRoot)
    {
        string path = ResolveConfigPath(scope, workspaceRoot);
        if (!File.Exists(path)) return true;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public HookStatus GetStatus(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs)
    {
        string path = ResolveConfigPath(scope, workspaceRoot);
        if (!File.Exists(path)) return HookStatus.NotInstalled;

        try
        {
            string raw = File.ReadAllText(path);
            if (JsonNode.Parse(raw) is not JsonObject root) return HookStatus.NotInstalled;
            if (root["hooks"] is not JsonObject hooks) return HookStatus.NotInstalled;

            int present = 0, drifted = 0;
            foreach (var spec in specs)
            {
                string copilotEvent = ToCopilotEvent(spec.EventName);
                if (copilotEvent.Length == 0) continue;
                if (hooks[copilotEvent] is not JsonArray arr || arr.Count == 0) continue;

                present++;
                string? cmd = arr[0]?["hooks"]?[0]?["command"]?.GetValue<string>();
                if (!string.Equals(cmd, spec.Command, StringComparison.Ordinal))
                    drifted++;
            }

            if (present == 0) return HookStatus.NotInstalled;
            if (drifted > 0) return HookStatus.Drift;

            int mappable = specs.Count(s => ToCopilotEvent(s.EventName).Length > 0);
            return present == mappable ? HookStatus.Installed : HookStatus.Partial;
        }
        catch (JsonException)
        {
            return HookStatus.NotInstalled;
        }
    }

    /// <summary>
    /// Map canonical event names (<c>SessionStart</c>, <c>SessionEnd</c>,
    /// <c>UserPromptSubmit</c>) to Copilot's camelCase names (<c>sessionStart</c>,
    /// <c>sessionEnd</c>, <c>userPromptSubmitted</c>). Empty string means "not mapped
    /// — skip" so a future canonical event scrinia adds doesn't get silently misrouted
    /// before this adapter learns the mapping.
    /// </summary>
    internal static string ToCopilotEvent(string canonical) => canonical switch
    {
        "SessionStart" => "sessionStart",
        "SessionEnd" => "sessionEnd",
        "UserPromptSubmit" => "userPromptSubmitted",
        "PreToolUse" => "preToolUse",
        "PostToolUse" => "postToolUse",
        _ => "",
    };

    internal static string ResolveConfigPath(HookScope scope, string? workspaceRoot)
    {
        string hooksDir = scope switch
        {
            HookScope.User => Path.Combine(
                Environment.GetEnvironmentVariable("COPILOT_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"),
                "hooks"),
            HookScope.Project => Path.Combine(
                workspaceRoot ?? throw new ArgumentException("workspaceRoot required for project scope.", nameof(workspaceRoot)),
                ".github", "hooks"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return Path.Combine(hooksDir, FileName);
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts));
    }
}
