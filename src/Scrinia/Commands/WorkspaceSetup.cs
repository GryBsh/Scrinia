using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Llm;
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
                MemoryEventSinkContext.Default = new CompositeEventSink([eventHandler, new MaintenanceEventSink()]);

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

        // Step 3: Background LLM for Tier 2 consolidation. The bundled plugin is Phase 4 —
        // for now we only try the OpenAI-compatible HTTP path (Ollama, llama.cpp server,
        // LM Studio, Docker Model Runner, etc.). Probe is short-budget; failure means
        // `scri consolidate --tier2` will exit with a setup hint, everything else works.
        await TryLoadBackgroundLlmAsync(loggerFactory.CreateLogger("Scrinia.Llm"), ct);
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
                MemoryEventSinkContext.Default = new CompositeEventSink([host, new MaintenanceEventSink()]);

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
    /// Probes for an OpenAI-compatible chat-completions endpoint (Ollama by default)
    /// and installs <see cref="OpenAiCompatibleBackgroundLlm"/> as <see cref="BackgroundLlmContext.Default"/>
    /// when one responds. Provider=plugin is handled later in Phase 4; Provider=none
    /// short-circuits the probe entirely. Probe timeout is short (2s) so we don't add
    /// noticeable startup latency in the common no-backend case.
    /// </summary>
    private static async Task TryLoadBackgroundLlmAsync(ILogger logger, CancellationToken ct)
    {
        var options = BuildLlmOptions();
        if (options.Provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        // Plugin provider is wired in Phase 4 — for now, "plugin" falls through to the HTTP
        // probe (which will fail unless the user also has an HTTP endpoint up).
        bool forceHttp = options.Provider.Equals("openai-compat", StringComparison.OrdinalIgnoreCase);

        try
        {
            var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var probe = new OpenAiCompatibleBackgroundLlm(options, probeHttp, ownsHttp: true);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(2));
            bool available = await probe.IsAvailableAsync(probeCts.Token);
            probe.Dispose();
            if (!available)
            {
                if (forceHttp)
                    Console.Error.WriteLine(
                        $"[scrinia:warn] Llm provider=openai-compat configured but {options.BaseUrl} did not respond.");
                return;
            }
        }
        catch (Exception ex)
        {
            if (forceHttp)
                Console.Error.WriteLine($"[scrinia:warn] Llm probe failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Install a long-lived instance for actual use. The probe instance was throwaway.
        var llm = OpenAiCompatibleBackgroundLlm.Create(options);
        BackgroundLlmContext.Default = llm;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => llm.Dispose();

        Console.Error.WriteLine(
            $"[scrinia:info] Background LLM ready (provider=openai-compat, model={options.Model}, baseUrl={options.BaseUrl})");
    }

    private static LlmOptions BuildLlmOptions()
    {
        var options = new LlmOptions();

        string? provider = GetConfigValue("Scrinia:Llm:Provider");
        if (provider is not null) options.Provider = provider;

        string? baseUrl = GetConfigValue("Scrinia:Llm:BaseUrl");
        if (baseUrl is not null) options.BaseUrl = baseUrl;

        string? model = GetConfigValue("Scrinia:Llm:Model");
        if (model is not null) options.Model = model;

        string? apiKey = GetConfigValue("Scrinia:Llm:ApiKey");
        if (apiKey is not null) options.ApiKey = apiKey;

        string? temp = GetConfigValue("Scrinia:Llm:Temperature");
        if (temp is not null && double.TryParse(temp, out double t)) options.Temperature = t;

        string? timeout = GetConfigValue("Scrinia:Llm:RequestTimeoutSeconds");
        if (timeout is not null && int.TryParse(timeout, out int s)) options.RequestTimeoutSeconds = s;

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
