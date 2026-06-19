using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// JSON config helpers shared verbatim by the Claude Code and Codex hook
/// installers (both manage a JSON settings file with a marked, Scrinia-owned
/// block). Flat static utility — no base class. The managed-block marker and
/// version are passed in so each installer keeps its own constants.
/// </summary>
internal static class HookInstallerJson
{
    public static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var fresh = new JsonObject();
        parent[key] = fresh;
        return fresh;
    }

    public static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var fresh = new JsonArray();
        parent[key] = fresh;
        return fresh;
    }

    public static JsonObject LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        string raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        var node = JsonNode.Parse(raw);
        return node as JsonObject ?? new JsonObject();
    }

    public static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts));
    }

    public static bool IsScriniaBlock(JsonObject block, string marker) =>
        block[marker] is JsonValue;

    public static JsonObject FindOrCreateManagedBlock(JsonArray events, string marker)
    {
        foreach (var node in events)
        {
            if (node is JsonObject obj && IsScriniaBlock(obj, marker)) return obj;
        }
        var fresh = new JsonObject();
        events.Add((JsonNode)fresh);
        return fresh;
    }

    public static void WriteManagedBlock(JsonObject block, string command, string marker, string markerVersion)
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
        block[marker] = markerVersion;
    }

    public static string? ExtractCommand(JsonObject block)
    {
        if (block["hooks"] is not JsonArray hooks) return null;
        if (hooks.Count == 0 || hooks[0] is not JsonObject first) return null;
        return first["command"]?.GetValue<string>();
    }
}
