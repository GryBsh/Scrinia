using System.Text.Json;

namespace Scrinia.Commands;

internal static class WorkspaceConfig
{
    private const string ConfigFileName = "config.json";

    private static string GetConfigPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".scrinia", ConfigFileName);

    internal static Dictionary<string, string> Load(string workspaceRoot)
    {
        string path = GetConfigPath(workspaceRoot);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.DictionaryStringString);
        if (raw is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
    }

    internal static void Save(string workspaceRoot, Dictionary<string, string> config)
    {
        string path = GetConfigPath(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.DictionaryStringString);
        File.WriteAllText(path, json);
    }

    internal static string? GetValue(string workspaceRoot, string key)
    {
        var config = Load(workspaceRoot);
        return config.TryGetValue(key, out var value) ? value : null;
    }

    internal static void SetValue(string workspaceRoot, string key, string value)
    {
        var config = Load(workspaceRoot);
        config[key] = value;
        Save(workspaceRoot, config);
    }

    internal static bool UnsetValue(string workspaceRoot, string key)
    {
        var config = Load(workspaceRoot);
        if (!config.Remove(key))
            return false;
        Save(workspaceRoot, config);
        return true;
    }
}
