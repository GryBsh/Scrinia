using System.Globalization;
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
    /// Initializes embeddings and loads optional plugins. Preference order (both embeddings
    /// and LLM): explicit HTTP provider → bundled Vulkan plugin → in-process fallback. The
    /// principle is "don't load the Vulkan rigamarole if the user already has an HTTP backend
    /// running" — Ollama / llama.cpp server / LM Studio are usually already up in dev setups,
    /// and spinning up a second model in our own VRAM is wasteful when we could just call out.
    ///
    /// Embeddings rules:
    /// 1. <c>Scrinia:Embeddings:Provider</c> set to anything HTTP-backed (<c>ollama</c>,
    ///    <c>openai</c>, <c>voyageai</c>, <c>azure</c>, <c>google</c>) → use it; skip plugin
    ///    and Model2Vec entirely.
    /// 2. Provider is <c>model2vec</c> (the default) or unset, and the Vulkan plugin exe is
    ///    installed → load the plugin, skip Model2Vec.
    /// 3. Otherwise → load in-process Model2Vec.
    /// </summary>
    internal static async Task LoadPluginsAsync(CancellationToken ct = default)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Embeddings");

        string workspaceDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string embeddingsDir = Path.Combine(workspaceDir, "embeddings");
        string exeDir = AppContext.BaseDirectory;
        string modelsDir = Path.Combine(exeDir, "models");

        var embeddingOptions = BuildEmbeddingOptions();
        bool explicitHttpEmbeddings = IsHttpEmbeddingsProvider(embeddingOptions.Provider);
        bool pluginExeInstalled = IsEmbeddingsPluginInstalled();

        if (explicitHttpEmbeddings)
        {
            // User picked Ollama/OpenAI/etc. — honor the explicit choice and skip the plugin
            // entirely. Loading both wastes VRAM and means the plugin's defaults shadow the
            // user's settings via SearchContributorContext override.
            LoadExplicitHttpEmbeddings(embeddingOptions, embeddingsDir, modelsDir, logger);
        }
        else if (pluginExeInstalled)
        {
            // Maintenance sink is independent of the embeddings provider — it handles things
            // like updating last-accessed timestamps. Wire it standalone so plugin-failure
            // does not leave maintenance hooks unsubscribed.
            MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);
            Console.Error.WriteLine(
                "[scrinia:info] Embeddings plugin exe detected — skipping built-in Model2Vec.");
            await TryLoadVulkanPluginAsync(ct);
        }
        else
        {
            LoadBuiltInEmbeddings(embeddingOptions, embeddingsDir, modelsDir, logger);
        }

        // Background LLM for Tier 2 consolidation. Auto mode probes HTTP first (Ollama et al.)
        // and only falls back to the bundled plugin if no HTTP backend responds — saves the
        // plugin subprocess + model load + VRAM when an existing endpoint is already up.
        await TryLoadBackgroundLlmAsync(loggerFactory.CreateLogger("Scrinia.Llm"), ct);
    }

    /// <summary>
    /// Runs <see cref="EmbeddingReindexer.ReindexIfStaleAsync"/> when the on-disk vectors were
    /// built with a different provider than the active one. Sync with stderr progress so the
    /// user can see what's happening. Triggered both at startup (here) and after
    /// <c>scri config Scrinia:Embeddings:*</c> writes (in the Config command).
    /// </summary>
    internal static void MaybeReindexAfterModelSwitch(
        IEmbeddingProvider provider, string embeddingsDir, ILogger logger, EmbeddingOptions options)
    {
        var store = MemoryStoreContext.Current;
        if (store is null) return;

        try
        {
            int lastDone = -1;
            void OnProgress(int done, int total)
            {
                if (done == lastDone) return;
                lastDone = done;
                Console.Error.Write($"\r[scrinia] reindexing {done}/{total} memories…");
                if (done == total) Console.Error.WriteLine();
            }

            // Sync wait — startup is a one-shot cost the user is already paying for.
            var result = EmbeddingReindexer.ReindexIfStaleAsync(
                store, provider, embeddingsDir, logger, OnProgress, CancellationToken.None, options)
                .GetAwaiter().GetResult();
            if (result is not null)
            {
                Console.Error.WriteLine(
                    $"[scrinia:info] Reindex complete: {result.Embedded}/{result.Total} embedded, " +
                    $"{result.Skipped} skipped, {result.Failed} failed.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Reindex after model change failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Unconditional full reindex against the currently-loaded embedding provider. Used by the
    /// <c>scri reindex</c> command — bypasses the signature-mismatch gate that
    /// <see cref="MaybeReindexAfterModelSwitch"/> relies on so a user who wants to force a
    /// rebuild (suspected corruption, manual recovery) actually gets one. Must be called after
    /// <see cref="LoadPluginsAsync"/> so the active provider is wired.
    /// </summary>
    internal static async Task<EmbeddingReindexer.Result?> ForceReindexAsync(CancellationToken ct = default)
    {
        var provider = _embeddingProvider;
        var store = MemoryStoreContext.Current;
        if (provider is null || !provider.IsAvailable || store is null)
        {
            Console.Error.WriteLine(
                "[scrinia:warn] Reindex skipped: no embedding provider is available. " +
                "Run `scri setup` first.");
            return null;
        }

        var options = BuildEmbeddingOptions();
        string embeddingsDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia", "embeddings");

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Reindex");

        int lastDone = -1;
        void OnProgress(int done, int total)
        {
            if (done == lastDone) return;
            lastDone = done;
            Console.Error.Write($"\r[scrinia] reindexing {done}/{total} memories…");
            if (done == total) Console.Error.WriteLine();
        }

        try
        {
            return await EmbeddingReindexer.ForceReindexAsync(
                store, provider, embeddingsDir, logger, OnProgress, ct, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Forced reindex failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when <paramref name="provider"/> is one of the network-backed providers
    /// — i.e. anything that talks to a remote/local HTTP endpoint rather than loading a
    /// model in-process. Used to decide whether the Vulkan plugin should be skipped.</summary>
    private static bool IsHttpEmbeddingsProvider(string provider) =>
        provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("voyageai", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("azure", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("google", StringComparison.OrdinalIgnoreCase);

    private static void LoadExplicitHttpEmbeddings(
        EmbeddingOptions options, string embeddingsDir, string modelsDir, ILogger logger)
    {
        try
        {
            var provider = EmbeddingProviderFactory.Create(options, modelsDir, logger);
            _embeddingProvider = provider;

            if (provider.IsAvailable)
            {
                string signature = ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap);
                var vectorStore = new VectorStore(embeddingsDir, signature);
                var reranker = new HybridReranker(provider, vectorStore, options.SemanticWeight);
                var eventHandler = new CoreEmbeddingEventHandler(provider, vectorStore, logger, options);

                SearchContributorContext.Default = reranker;
                MemoryEventSinkContext.Default = new CompositeEventSink([eventHandler, new MaintenanceEventSink()]);

                Console.Error.WriteLine(
                    $"[scrinia:info] HTTP embeddings ready " +
                    $"(provider={options.Provider}, type={provider.GetType().Name}, dims={provider.Dimensions})");

                MaybeReindexAfterModelSwitch(provider, embeddingsDir, logger, options);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] HTTP embeddings provider '{options.Provider}' not available — " +
                    $"check Scrinia:Embeddings:* config. Search degrades to BM25-only.");
                MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to initialize HTTP embeddings ('{options.Provider}'): " +
                $"{ex.GetType().Name}: {ex.Message}");
            MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);
        }
    }

    private static void LoadBuiltInEmbeddings(
        EmbeddingOptions options, string embeddingsDir, string modelsDir, ILogger logger)
    {
        try
        {
            var provider = EmbeddingProviderFactory.Create(options, modelsDir, logger);
            _embeddingProvider = provider;

            if (provider.IsAvailable)
            {
                string signature = ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap);
                var vectorStore = new VectorStore(embeddingsDir, signature);
                var reranker = new HybridReranker(provider, vectorStore, options.SemanticWeight);
                var eventHandler = new CoreEmbeddingEventHandler(provider, vectorStore, logger, options);

                SearchContributorContext.Default = reranker;
                MemoryEventSinkContext.Default = new CompositeEventSink([eventHandler, new MaintenanceEventSink()]);

                Console.Error.WriteLine(
                    $"[scrinia:info] Built-in embeddings ready " +
                    $"(provider={provider.GetType().Name}, dims={provider.Dimensions})");

                MaybeReindexAfterModelSwitch(provider, embeddingsDir, logger, options);
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
        if (weight is not null && double.TryParse(weight, NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
            options.SemanticWeight = w;

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

        string? chunkSize = GetConfigValue("Scrinia:Embeddings:ChunkSize");
        if (chunkSize is not null && int.TryParse(chunkSize, out int cs) && cs > 0) options.ChunkSize = cs;

        string? chunkOverlap = GetConfigValue("Scrinia:Embeddings:ChunkOverlap");
        if (chunkOverlap is not null && int.TryParse(chunkOverlap, out int co) && co >= 0) options.ChunkOverlap = co;

        string? maxChunks = GetConfigValue("Scrinia:Embeddings:MaxChunksPerMemory");
        if (maxChunks is not null && int.TryParse(maxChunks, out int mc) && mc > 0) options.MaxChunksPerMemory = mc;

        // Guardrail: overlap must be strictly less than chunk size, otherwise SliceWindows
        // throws. Snap silently to sane defaults rather than crash the daemon on bad config.
        if (options.ChunkOverlap >= options.ChunkSize)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] ChunkOverlap ({options.ChunkOverlap}) >= ChunkSize ({options.ChunkSize}); " +
                $"reverting to defaults {TextChunker.DefaultWindowSize}/{TextChunker.DefaultOverlap}.");
            options.ChunkSize = TextChunker.DefaultWindowSize;
            options.ChunkOverlap = TextChunker.DefaultOverlap;
        }

        return options;
    }

    /// <summary>
    /// Installs <see cref="BackgroundLlmContext.Default"/> for Tier 2 consolidation. Provider
    /// selection order (when <c>Scrinia:Llm:Provider</c> = "auto"):
    /// <list type="number">
    ///   <item>OpenAI-compatible HTTP endpoint at <c>Scrinia:Llm:BaseUrl</c> if it responds —
    ///         tried first because "if Ollama/llama.cpp/LM Studio is already running, there's
    ///         no point booting our own subprocess with a separate model copy in VRAM."</item>
    ///   <item>Agent CLIs (Claude Code, Codex, GitHub Copilot) in preference order if any are
    ///         on PATH — reuses the user's existing CLI auth, no API key needed.</item>
    ///   <item>Bundled <c>scri-plugin-llm</c> subprocess if the above fall through and the
    ///         plugin exe is present.</item>
    ///   <item>None — <c>scri consolidate --with-llm</c> will print a setup hint.</item>
    /// </list>
    /// Explicit Provider values (<c>plugin</c>, <c>openai</c>, <c>claude-cli</c>,
    /// <c>codex-cli</c>, <c>copilot-cli</c>, <c>none</c>) skip the other steps and surface a
    /// warning if the chosen backend is unavailable.
    /// </summary>
    private static async Task TryLoadBackgroundLlmAsync(ILogger logger, CancellationToken ct)
    {
        var options = BuildLlmOptions();
        if (options.Provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        bool forcePlugin = options.Provider.Equals("plugin", StringComparison.OrdinalIgnoreCase);
        bool forceHttp = options.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase);
        var explicitCliVariant = AgentCliVariant.TryFromId(options.Provider);

        // Explicit agent-CLI selection short-circuits everything.
        if (explicitCliVariant is not null)
        {
            if (await TryStartAgentCliAsync(explicitCliVariant, options, forced: true)) return;
            Console.Error.WriteLine(
                $"[scrinia:warn] Llm provider={options.Provider} configured but the {explicitCliVariant.DisplayName} CLI was not on PATH.");
            return;
        }

        // Step A: HTTP probe first (unless explicitly forced to plugin). When Ollama or
        // another OpenAI-compatible server is already running we prefer it — avoids
        // spinning up the plugin subprocess and loading a second model into VRAM.
        if (!forcePlugin)
        {
            if (await TryProbeOpenAiCompatibleAsync(options, forceHttp, ct)) return;
            if (forceHttp) return; // failure already logged by the probe
        }

        // Step B: try each agent CLI in preference order. Reuses the user's existing
        // subscription auth — no API key, no model download — and is preferred over the
        // bundled plugin because most users authenticated to claude/codex/copilot have a
        // larger/better model available than what the local plugin ships.
        if (!forcePlugin)
        {
            foreach (var variant in AgentCliVariant.AllInAutoOrder)
            {
                if (await TryStartAgentCliAsync(variant, options, forced: false)) return;
            }
        }

        // Step C: bundled plugin (unless explicitly forced to HTTP).
        if (!forceHttp)
        {
            if (await TryStartLlmPluginAsync(ct)) return;
            if (forcePlugin)
            {
                Console.Error.WriteLine(
                    "[scrinia:warn] Llm provider=plugin configured but scri-plugin-llm was not available.");
            }
        }
    }

    /// <summary>
    /// Probe the agent CLI by checking if its executable is on PATH; if so, install an
    /// <see cref="AgentCliBackgroundLlm"/> as the active background LLM. Returns true on
    /// success. Auto-mode failures are silent (info log only); explicit-mode failures are
    /// surfaced by the caller.
    /// </summary>
    private static Task<bool> TryStartAgentCliAsync(AgentCliVariant variant, LlmOptions options, bool forced)
    {
        var runner = new Scrinia.Core.Process.ProcessRunner();
        var llm = new AgentCliBackgroundLlm(variant, runner, options);
        return TryInstallAgentCliBackendAsync(llm, variant, forced);
    }

    private static async Task<bool> TryInstallAgentCliBackendAsync(AgentCliBackgroundLlm llm, AgentCliVariant variant, bool forced)
    {
        if (!await llm.IsAvailableAsync(CancellationToken.None))
        {
            if (forced)
                Console.Error.WriteLine(
                    $"[scrinia:warn] {variant.DisplayName} CLI not found on PATH (expected exe: {variant.Executable}).");
            return false;
        }
        BackgroundLlmContext.Default = llm;
        Console.Error.WriteLine(
            $"[scrinia:info] Background LLM ready (provider={variant.Id}, exe={variant.Executable})");
        return true;
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

    /// <summary>
    /// Probes the configured OpenAI-compatible endpoint and installs it as the background LLM
    /// if reachable. Returns true on success so the caller can short-circuit further fallback.
    /// Probe-failure logging differentiates auto vs explicit: when forceHttp is set the user
    /// chose this path so failure is a warn; in auto mode failure is just an info breadcrumb.
    /// </summary>
    private static async Task<bool> TryProbeOpenAiCompatibleAsync(LlmOptions options, bool forceHttp, CancellationToken ct)
    {
        // 5s rather than 2s: on Windows the IPv6→IPv4 fallback for `localhost` can add a
        // second or two on its own, and a freshly-started Ollama may stall briefly while
        // loading model metadata into its API surface. 2s was triggering false negatives on
        // working installs.
        const int probeTimeoutSeconds = 5;
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
                    (forceHttp ? "Tier 2 will be unavailable." : "Falling back to bundled plugin if installed."));
                return false;
            }
        }
        catch (Exception ex)
        {
            string severity = forceHttp ? "warn" : "info";
            Console.Error.WriteLine(
                $"[scrinia:{severity}] LLM HTTP probe error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var llm = OpenAiCompatibleBackgroundLlm.Create(options);
        BackgroundLlmContext.Default = llm;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => llm.Dispose();

        Console.Error.WriteLine(
            $"[scrinia:info] Background LLM ready (provider=openai, model={options.Model}, baseUrl={options.BaseUrl})");
        return true;
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
        // InvariantCulture so workspace config is portable across locales — de-DE / fr-FR
        // default decimal separator is "," which would silently fail the locale-sensitive
        // overload and leave Temperature pinned at its default.
        if (temp is not null && double.TryParse(temp, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
            options.Temperature = t;

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
