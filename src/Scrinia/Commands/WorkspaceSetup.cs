using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static PluginProcessHost? _pluginHost;

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
        string embeddingsExe = Path.Combine(pluginsDir, $"scri-plugin-embeddings{ext}");
        if (!File.Exists(embeddingsExe))
            return;

        // Vector data lives in the workspace-local .scrinia/ directory (per-project isolation).
        // Model cache is global (no need to re-download 87MB ONNX model per workspace).
        string dataDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scrinia", "plugins");
        Directory.CreateDirectory(modelsDir);

        try
        {
            var host = new PluginProcessHost();
            await host.StartAsync(embeddingsExe, dataDir, modelsDir, GetConfigValue, ct);

            SearchContributorContext.Default = host;
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
    /// Reads config from environment variables.
    /// Supports colon-separated keys (e.g. "Scrinia:Embeddings:Provider")
    /// mapped to double-underscore env vars (e.g. "SCRINIA__EMBEDDINGS__PROVIDER").
    /// </summary>
    private static string? GetConfigValue(string key)
    {
        // Try exact key as env var first (with colons replaced by double underscores)
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // Also try the .NET-standard double-underscore convention
        envKey = key.Replace(':', '_').Replace(".", "_").ToUpperInvariant();
        return Environment.GetEnvironmentVariable(envKey);
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
