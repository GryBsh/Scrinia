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
    /// Two-step initialization with plugin-first detection:
    /// 1. If the Vulkan embeddings plugin exe is installed, skip the built-in init entirely
    ///    — the plugin will own embeddings. Saves ~50MB RAM and a model load on startup.
    /// 2. Otherwise, load built-in (in-process Model2Vec or API provider from config) as the
    ///    permanent embeddings backend.
    /// 3. Try the plugin subprocess. If it starts and reports a working provider, it claims
    ///    SearchContributorContext.Default and MemoryEventSinkContext.Default. If it fails
    ///    AND step 1 skipped the built-in, search degrades to BM25-only for this session.
    /// </summary>
    internal static async Task LoadPluginsAsync(CancellationToken ct = default)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Embeddings");

        string workspaceDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string embeddingsDir = Path.Combine(workspaceDir, "embeddings");
        string exeDir = AppContext.BaseDirectory;
        string modelsDir = Path.Combine(exeDir, "models");

        bool pluginExeInstalled = IsEmbeddingsPluginInstalled();

        // Step 1: Built-in embeddings — skipped when the Vulkan plugin will take over. The
        // plugin runs as a child process with its own model load; keeping a second copy of
        // Model2Vec in-process burns memory for no benefit since the plugin's defaults
        // override these anyway.
        if (!pluginExeInstalled)
        {
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
        }
        else
        {
            // Maintenance sink is independent of the embeddings provider — it handles things
            // like updating last-accessed timestamps. Wire it standalone so plugin-failure
            // does not leave maintenance hooks unsubscribed.
            MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);
            Console.Error.WriteLine(
                "[scrinia:info] Embeddings plugin exe detected — skipping built-in Model2Vec.");
        }

        // Step 2: Optional Vulkan plugin (child-process, overrides built-in if found)
        await TryLoadVulkanPluginAsync(ct);

        // Step 3: Background LLM for Tier 2 consolidation. Try the bundled plugin first
        // (subprocess via MCP stdio), then fall through to the OpenAI-compatible HTTP path
        // (Ollama, llama.cpp server, LM Studio, Docker Model Runner). Probe budget is short
        // so a missing backend never adds noticeable startup latency.
        await TryLoadBackgroundLlmAsync(loggerFactory.CreateLogger("Scrinia.Llm"), ct);
    }

    /// <summary>
    /// Checks whether the embeddings plugin exe is on disk. Used to decide whether to skip
    /// the built-in Model2Vec init — when the plugin is installed it will own embeddings.
    /// </summary>
    private static bool IsEmbeddingsPluginInstalled() => ResolveEmbeddingsPluginExe() is not null;

    private static string? ResolveEmbeddingsPluginExe()
    {
        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");
        if (!Directory.Exists(pluginsDir)) return null;

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string pluginName = GetPluginName("plugins:embeddings", "scri-plugin-embeddings");

        // Subdirectory layout (multi-file publish) first, then flat (single-file).
        string candidate = Path.Combine(pluginsDir, pluginName, $"{pluginName}{ext}");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(pluginsDir, $"{pluginName}{ext}");
        return File.Exists(candidate) ? candidate : null;
    }

    private static async Task TryLoadVulkanPluginAsync(CancellationToken ct)
    {
        string? embeddingsExe = ResolveEmbeddingsPluginExe();
        if (embeddingsExe is null) return;

        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");
        string pluginName = GetPluginName("plugins:embeddings", "scri-plugin-embeddings");

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
    /// Installs <see cref="BackgroundLlmContext.Default"/> for Tier 2 consolidation. Provider
    /// selection order (when <c>Scrinia:Llm:Provider</c> = "auto"):
    /// <list type="number">
    ///   <item>Bundled <c>scri-plugin-llm</c> subprocess if the exe is present.</item>
    ///   <item>OpenAI-compatible HTTP endpoint at <c>Scrinia:Llm:BaseUrl</c> if it responds.</item>
    ///   <item>None — <c>scri consolidate --with-llm</c> will print a setup hint.</item>
    /// </list>
    /// Explicit Provider values (<c>plugin</c>, <c>openai</c>, <c>none</c>) skip the
    /// other steps and surface a warning if the chosen backend is unavailable.
    /// </summary>
    private static async Task TryLoadBackgroundLlmAsync(ILogger logger, CancellationToken ct)
    {
        var options = BuildLlmOptions();
        if (options.Provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        bool forcePlugin = options.Provider.Equals("plugin", StringComparison.OrdinalIgnoreCase);
        bool forceHttp = options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase);

        // Step A: bundled plugin (unless explicitly forced to HTTP).
        if (!forceHttp)
        {
            if (await TryStartLlmPluginAsync(ct)) return;
            if (forcePlugin)
            {
                Console.Error.WriteLine(
                    "[scrinia:warn] Llm provider=plugin configured but scri-plugin-llm was not available.");
                return;
            }
        }

        // Step B: OpenAI-compatible HTTP endpoint (unless explicitly forced to plugin).
        if (!forcePlugin)
            await TryProbeOpenAiCompatibleAsync(options, forceHttp, ct);
    }

    private static async Task<bool> TryStartLlmPluginAsync(CancellationToken ct)
    {
        string exeDir = AppContext.BaseDirectory;
        string pluginsDir = Path.Combine(exeDir, "plugins");
        if (!Directory.Exists(pluginsDir)) return false;

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string pluginName = GetPluginName("plugins:llm", "scri-plugin-llm");

        // Subdirectory layout (multi-file publish) first, then flat (single-file).
        string llmExe = Path.Combine(pluginsDir, pluginName, $"{pluginName}{ext}");
        if (!File.Exists(llmExe))
        {
            llmExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");
            if (!File.Exists(llmExe)) return false;
        }

        string dataDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string modelsDir = Path.Combine(pluginsDir, pluginName);
        Directory.CreateDirectory(modelsDir);

        try
        {
            var host = new McpLlmPluginHost();
            await host.StartAsync(llmExe, dataDir, modelsDir, GetConfigValue, ct);

            if (!host.HasCompleteCapability)
            {
                await host.DisposeAsync();
                return false;
            }

            BackgroundLlmContext.Default = host;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                host.DisposeAsync().AsTask().Wait(3000);

            Console.Error.WriteLine(
                $"[scrinia:info] Background LLM ready (provider=plugin, arch={host.ModelArchitecture}, hardware={host.Hardware})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to start LLM plugin: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task TryProbeOpenAiCompatibleAsync(LlmOptions options, bool forceHttp, CancellationToken ct)
    {
        // Probe-failure messaging: when forceHttp is set the user explicitly chose this path
        // so they want loud failures; otherwise (auto mode) a single info line lets curious
        // users see what's happening without flooding the log on every startup.
        const int probeTimeoutSeconds = 2;
        try
        {
            var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(probeTimeoutSeconds) };
            var probe = new OpenAiCompatibleBackgroundLlm(options, probeHttp, ownsHttp: true);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(probeTimeoutSeconds));
            bool available = await probe.IsAvailableAsync(probeCts.Token);
            probe.Dispose();
            if (!available)
            {
                string severity = forceHttp ? "warn" : "info";
                Console.Error.WriteLine(
                    $"[scrinia:{severity}] LLM HTTP probe failed: {options.BaseUrl} did not respond at /models or /. " +
                    $"Tier 2 will be unavailable unless a plugin is loaded.");
                return;
            }
        }
        catch (Exception ex)
        {
            string severity = forceHttp ? "warn" : "info";
            Console.Error.WriteLine(
                $"[scrinia:{severity}] LLM HTTP probe error: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var llm = OpenAiCompatibleBackgroundLlm.Create(options);
        BackgroundLlmContext.Default = llm;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => llm.Dispose();

        Console.Error.WriteLine(
            $"[scrinia:info] Background LLM ready (provider=openai, model={options.Model}, baseUrl={options.BaseUrl})");
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
