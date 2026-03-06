using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static McpPluginHost? _pluginHost;
    private static IEmbeddingProvider? _embeddingProvider;

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
    /// Initializes embeddings and loads optional plugins.
    ///
    /// Two-step initialization:
    /// 1. Built-in embeddings (in-process): Model2Vec or API provider from config.
    ///    Sets SearchContributorContext.Default and MemoryEventSinkContext.Default.
    /// 2. Optional Vulkan plugin (child-process): if found, overrides the built-in defaults.
    /// </summary>
    internal static async Task LoadPluginsAsync(CancellationToken ct = default)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Embeddings");

        string workspaceDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string embeddingsDir = Path.Combine(workspaceDir, "embeddings");
        string exeDir = AppContext.BaseDirectory;
        string modelsDir = Path.Combine(exeDir, "models");

        // Step 1: Built-in embeddings (in-process, zero native deps)
        try
        {
            var options = BuildEmbeddingOptions();
            var provider = EmbeddingProviderFactory.Create(options, modelsDir, logger);
            _embeddingProvider = provider;

            if (provider.IsAvailable)
            {
                var vectorStore = new VectorStore(embeddingsDir);
                var reranker = new HybridReranker(provider, vectorStore, options.SemanticWeight);
                var eventHandler = new CoreEmbeddingEventHandler(provider, vectorStore, logger);

                SearchContributorContext.Default = reranker;
                MemoryEventSinkContext.Default = eventHandler;

                Console.Error.WriteLine(
                    $"[scrinia:info] Built-in embeddings ready " +
                    $"(provider={provider.GetType().Name}, dims={provider.Dimensions})");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[scrinia:info] Built-in embeddings not available " +
                    $"(provider={provider.GetType().Name})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to initialize built-in embeddings: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // Step 2: Optional Vulkan plugin (child-process, overrides built-in if found)
        await TryLoadVulkanPluginAsync(ct);
    }

    private static async Task TryLoadVulkanPluginAsync(CancellationToken ct)
    {
        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");

        if (!Directory.Exists(pluginsDir))
            return;

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string pluginName = GetPluginName("plugins:embeddings", "scri-plugin-embeddings");

        // Look for plugin exe: first in subdirectory (multi-file publish), then flat (single-file)
        string embeddingsExe = Path.Combine(pluginsDir, pluginName, $"{pluginName}{ext}");
        if (!File.Exists(embeddingsExe))
        {
            embeddingsExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");
            if (!File.Exists(embeddingsExe))
                return;
        }

        // Vector data lives in the workspace-local .scrinia/ directory (per-project isolation).
        string dataDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string modelsDir = Path.Combine(pluginsDir, pluginName);
        Directory.CreateDirectory(modelsDir);

        try
        {
            var host = new McpPluginHost();
            await host.StartAsync(embeddingsExe, dataDir, modelsDir, GetConfigValue, ct);

            // If the plugin has no working provider, shut it down immediately
            // to avoid wasting a child process.
            if (!host.HasSearchCapability && !host.HasEventSinkCapability)
            {
                await host.DisposeAsync();
                return;
            }

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
            // Built-in embeddings remain active as fallback
        }
    }

    /// <summary>Builds EmbeddingOptions from config values.</summary>
    private static EmbeddingOptions BuildEmbeddingOptions()
    {
        var options = new EmbeddingOptions();

        string? provider = GetConfigValue("Scrinia:Embeddings:Provider");
        if (provider is not null) options.Provider = provider;

        string? weight = GetConfigValue("Scrinia:Embeddings:SemanticWeight");
        if (weight is not null && double.TryParse(weight, out double w)) options.SemanticWeight = w;

        string? ollamaUrl = GetConfigValue("Scrinia:Embeddings:OllamaBaseUrl");
        if (ollamaUrl is not null) options.OllamaBaseUrl = ollamaUrl;

        string? ollamaModel = GetConfigValue("Scrinia:Embeddings:OllamaModel");
        if (ollamaModel is not null) options.OllamaModel = ollamaModel;

        string? openAiKey = GetConfigValue("Scrinia:Embeddings:OpenAiApiKey");
        if (openAiKey is not null) options.OpenAiApiKey = openAiKey;

        string? openAiModel = GetConfigValue("Scrinia:Embeddings:OpenAiModel");
        if (openAiModel is not null) options.OpenAiModel = openAiModel;

        string? openAiUrl = GetConfigValue("Scrinia:Embeddings:OpenAiBaseUrl");
        if (openAiUrl is not null) options.OpenAiBaseUrl = openAiUrl;

        string? voyageKey = GetConfigValue("Scrinia:Embeddings:VoyageAiApiKey");
        if (voyageKey is not null) options.VoyageAiApiKey = voyageKey;

        string? voyageModel = GetConfigValue("Scrinia:Embeddings:VoyageAiModel");
        if (voyageModel is not null) options.VoyageAiModel = voyageModel;

        string? voyageUrl = GetConfigValue("Scrinia:Embeddings:VoyageAiBaseUrl");
        if (voyageUrl is not null) options.VoyageAiBaseUrl = voyageUrl;

        return options;
    }

    /// <summary>
    /// Resolves a plugin executable name from env var -> config file -> default.
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
    internal static string? GetConfigValue(string key)
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
