using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static McpPluginHost? _pluginHost;

    internal static void Configure(string? workspaceRoot)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            root = workspaceRoot!;
        }
        else
        {
            // Walk up from cwd looking for an existing .scrinia/ directory,
            // like git walks up looking for .git/. This makes `scri serve`
            // work correctly regardless of which directory the MCP client
            // (Claude Code, Copilot, Cursor, etc.) launches the process from.
            root = FindWorkspaceRoot(Directory.GetCurrentDirectory())
                ?? Directory.GetCurrentDirectory();
        }

        ScriniaArtifactStore.Configure(root);
        MemoryStoreContext.Current = new FileMemoryStore(root);
    }

    /// <summary>
    /// Loads CLI plugins from the <c>plugins/</c> directory next to the executable.
    /// Discovers <c>scri-plugin-*</c> executables and launches them as child processes.
    /// Sets <see cref="SearchContributorContext"/> and <see cref="MemoryEventSinkContext"/>
    /// so MCP tools (and CLI commands using the store context) pick up plugin contributions.
    /// </summary>
    internal static async Task LoadPluginsAsync(CancellationToken ct = default)
    {
        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");

        if (!Directory.Exists(pluginsDir))
            return;

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string pluginName = GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
        string embeddingsExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");
        if (!File.Exists(embeddingsExe))
            return;

        // Vector data lives in the workspace-local .scrinia/ directory (per-project isolation).
        // Plugin files (models, caches) go in a folder named after the plugin executable,
        // alongside the plugin in the plugins/ directory.
        string dataDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string modelsDir = Path.Combine(pluginsDir, pluginName);
        Directory.CreateDirectory(modelsDir);

        try
        {
            var host = new McpPluginHost();
            await host.StartAsync(embeddingsExe, dataDir, modelsDir, GetConfigValue, ct);

            if (host.HasSearchCapability)
                SearchContributorContext.Default = host;
            if (host.HasEventSinkCapability)
                MemoryEventSinkContext.Default = host;

            _pluginHost = host;

            // Ensure plugin shuts down when the CLI exits
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                host.DisposeAsync().AsTask().Wait(3000);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to start embeddings plugin: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a plugin executable name from env var → config file → default.
    /// </summary>
    internal static string GetPluginName(string key, string defaultName)
    {
        // 1. Environment variable
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 2. Config file
        value = WorkspaceConfig.GetValue(ScriniaArtifactStore.WorkspaceRootPath, key);
        if (value is not null) return value;

        return defaultName;
    }

    /// <summary>
    /// Reads config from environment variables, then falls back to the workspace config file.
    /// Supports colon-separated keys (e.g. "Scrinia:Embeddings:Provider")
    /// mapped to double-underscore env vars (e.g. "SCRINIA__EMBEDDINGS__PROVIDER").
    /// </summary>
    private static string? GetConfigValue(string key)
    {
        // 1. Environment variable (highest priority)
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // Also try the .NET-standard double-underscore convention
        envKey = key.Replace(':', '_').Replace(".", "_").ToUpperInvariant();
        value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 2. Config file (workspace-scoped)
        return WorkspaceConfig.GetValue(ScriniaArtifactStore.WorkspaceRootPath, key);
    }

    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".scrinia")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
