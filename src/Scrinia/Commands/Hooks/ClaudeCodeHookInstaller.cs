using System.Text.Json;
using System.Text.Json.Nodes;
using Scrinia.Core.Process;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// Hook installer for Claude Code. Writes to <c>~/.claude/settings.json</c> (user scope)
/// or <c>&lt;workspace&gt;/.claude/settings.json</c> (project scope), preserving any
/// user-authored hooks and other settings.
///
/// <para>Schema reminder — Claude Code's hook block:</para>
/// <code>
/// {
///   "hooks": {
///     "SessionStart": [
///       {
///         "matcher": "",
///         "hooks": [{ "type": "command", "command": "scri restore" }],
///         "_scriniaManaged": "v1"     // our sentinel
///       }
///     ],
///     "Stop": [ ... ]
///   }
/// }
/// </code>
///
/// <para>The <c>_scriniaManaged</c> marker key identifies blocks scrinia owns. Install
/// finds-or-creates the marked block; uninstall removes only those. Idempotent across
/// repeated <c>scri setup --hooks</c> runs.</para>
/// </summary>
public sealed class ClaudeCodeHookInstaller : IAgentHookInstaller
{
    private const string ConfigDirName = ".claude";
    private const string ConfigFileName = "settings.json";
    internal const string ManagedMarker = "_scriniaManaged";
    private const string MarkerVersion = "v1";

    private readonly IProcessRunner _runner;

    public ClaudeCodeHookInstaller(IProcessRunner? runner = null)
    {
        _runner = runner ?? new ProcessRunner();
    }

    public string CliName => "Claude Code";

    public bool IsCliInstalled() => _runner.IsExecutableOnPath("claude");

    public bool InstallHooks(HookScope scope, string? workspaceRoot, IReadOnlyList<HookSpec> specs)
    {
        string path = ResolveConfigPath(scope, workspaceRoot);
        try
        {
            JsonObject root = LoadOrCreate(path);
            JsonObject hooks = GetOrCreateObject(root, "hooks");
            foreach (var spec in specs)
            {
                JsonArray eventArr = GetOrCreateArray(hooks, spec.EventName);
                JsonObject managedBlock = FindOrCreateManagedBlock(eventArr);
                WriteManagedBlock(managedBlock, spec.Command);
            }
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
            JsonObject root = LoadOrCreate(path);
            if (root["hooks"] is not JsonObject hooks) return true;

            var emptyEvents = new List<string>();
            foreach (var (eventName, node) in hooks)
            {
                if (node is not JsonArray arr) continue;
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is JsonObject block && IsScriniaBlock(block))
                        arr.RemoveAt(i);
                }
                if (arr.Count == 0) emptyEvents.Add(eventName);
            }
            foreach (string e in emptyEvents) hooks.Remove(e);

            if (hooks.Count == 0) root.Remove("hooks");

            Save(path, root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
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
            JsonObject root = LoadOrCreate(path);
            if (root["hooks"] is not JsonObject hooks) return HookStatus.NotInstalled;

            int present = 0, drifted = 0;
            foreach (var spec in specs)
            {
                if (hooks[spec.EventName] is not JsonArray arr) continue;
                foreach (var item in arr)
                {
                    if (item is not JsonObject block || !IsScriniaBlock(block)) continue;
                    present++;
                    string? cmd = ExtractCommand(block);
                    if (!string.Equals(cmd, spec.Command, StringComparison.Ordinal))
                        drifted++;
                    break;
                }
            }

            if (present == 0) return HookStatus.NotInstalled;
            if (drifted > 0) return HookStatus.Drift;
            return present == specs.Count ? HookStatus.Installed : HookStatus.Partial;
        }
        catch (JsonException)
        {
            return HookStatus.NotInstalled;
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────

    internal static string ResolveConfigPath(HookScope scope, string? workspaceRoot)
    {
        string baseDir = scope switch
        {
            HookScope.User => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            HookScope.Project => workspaceRoot
                ?? throw new ArgumentException("workspaceRoot required for project scope.", nameof(workspaceRoot)),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return Path.Combine(baseDir, ConfigDirName, ConfigFileName);
    }

    private static JsonObject LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        string raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        var node = JsonNode.Parse(raw);
        return node as JsonObject ?? new JsonObject();
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string text = root.ToJsonString(opts);
        File.WriteAllText(path, text);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var fresh = new JsonObject();
        parent[key] = fresh;
        return fresh;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var fresh = new JsonArray();
        parent[key] = fresh;
        return fresh;
    }

    private static JsonObject FindOrCreateManagedBlock(JsonArray events)
    {
        foreach (var node in events)
        {
            if (node is JsonObject obj && IsScriniaBlock(obj)) return obj;
        }
        var fresh = new JsonObject();
        events.Add((JsonNode)fresh);
        return fresh;
    }

    private static bool IsScriniaBlock(JsonObject block) =>
        block[ManagedMarker] is JsonValue;

    private static void WriteManagedBlock(JsonObject block, string command)
    {
        block["matcher"] = "";
        var hookEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
        };
        var hooksArr = new JsonArray();
        hooksArr.Add((JsonNode)hookEntry);
        block["hooks"] = hooksArr;
        block[ManagedMarker] = MarkerVersion;
    }

    private static string? ExtractCommand(JsonObject block)
    {
        if (block["hooks"] is not JsonArray hooks) return null;
        if (hooks.Count == 0 || hooks[0] is not JsonObject first) return null;
        return first["command"]?.GetValue<string>();
    }
}
