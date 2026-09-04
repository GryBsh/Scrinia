using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Commands;

internal static class WorkspaceConfig
{
    internal static Dictionary<string, string> Load(string workspaceRoot) => CoreWorkspaceConfig.LoadValues(workspaceRoot);
    internal static void Save(string workspaceRoot, Dictionary<string, string> config) => CoreWorkspaceConfig.Save(workspaceRoot, config);
    internal static string? GetValue(string workspaceRoot, string key) => CoreWorkspaceConfig.Load(workspaceRoot).GetValue(key);

    internal static void SetValue(string workspaceRoot, string key, string value)
    {
        CoreWorkspaceConfig config = CoreWorkspaceConfig.Load(workspaceRoot);
        config.SetValue(key, value);
        config.Save();
    }

    internal static bool UnsetValue(string workspaceRoot, string key)
    {
        CoreWorkspaceConfig config = CoreWorkspaceConfig.Load(workspaceRoot);
        if (config.GetValue(key) is null)
            return false;
        config.UnsetValue(key);
        config.Save();
        return true;
    }
}
