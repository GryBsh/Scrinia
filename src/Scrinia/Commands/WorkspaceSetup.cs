using System.Globalization;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static McpPluginHost? _pluginHost;
    private static IEmbeddingProvider? _embeddingProvider;
    private static readonly AsyncLocal<string?> _rootOverride = new();
    private static string? _root;

    /// <summary>
    /// The resolved workspace root for this process, cached by <see cref="Configure"/>.
    /// Respects test overrides set via <see cref="OverrideRoot"/> (checked first, via
    /// <see cref="AsyncLocal{T}"/> so parallel test classes don't clobber each other),
    /// falling back to the process-wide value <see cref="Configure"/> cached.
    /// Replaces the former static workspace-root accessor.
    /// </summary>
    internal static string Root =>
        _rootOverride.Value ?? _root ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Root.");

    /// <summary>Sets an async-local override for <see cref="Root"/>, scoped to the current
    /// async context. For tests that need root isolation under parallel execution; not
    /// wired into any production path.</summary>
    internal static void OverrideRoot(string? path) => _rootOverride.Value = path;

    /// <summary>
    /// The configured store for this process. Throws if <see cref="Configure"/> has not run.
    /// </summary>
    internal static IMemoryStore Store =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Store.");

    /// <summary>The configured store, narrowed to the local-filesystem contract. CLI commands
    /// that read or write workspace files use this. Throws when the process is in remote mode
    /// (<c>scri serve --remote</c>), which is the only configuration that installs a non-local
    /// store — see ScriniaCommands.Serve.</summary>
    internal static ILocalMemoryStore LocalStore =>
        MemoryStoreContext.Current as ILocalMemoryStore
        ?? throw new InvalidOperationException(
            "This command needs a local workspace; the configured store is remote.");

    internal static void Configure(string? workspaceRoot)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        _root = root;

        // Build ranker weights from config so a workspace can tune α_recency without
        // recompiling. Defaults (RankerOptions.Default) match the historical composition —
        // see RankerOptions docstring for the formula and calibration notes.
        var rankerOptions = RankerOptions.FromConfig(GetConfigValue);
        MemoryStoreContext.Current = new FileMemoryStore(root, rankerOptions);
    }

    /// <summary>
    /// Builds the message shown when a store exists somewhere resolution deliberately did not
    /// look. Separated from the write so the wording is testable without capturing the console.
    /// </summary>
    internal static string FormatOrphanWarning(string orphan, string root, IReadOnlyList<string> topics)
    {
        string orphanParent = Path.GetDirectoryName(orphan) ?? orphan;
        if (topics.Count > 0)
        {
            string topicList = string.Join(",", topics);
            return $"""
                scrinia: ignoring store at {orphan}
                         using {root}

                to migrate its memories:
                  scri bundle export {topicList} --workspace-root "{orphanParent}"
                  scri bundle import <file> --workspace-root "{root}"
                """;
        }

        // No topic list could be produced — either the store is empty, or it contains a
        // layout `scri bundle export` cannot target with a topic list (e.g. the v2
        // .scrinia/memories/ hierarchy). Guessing a list here would either be wrong or
        // silently incomplete, so point at `scri list` instead of printing one.
        return $"""
            scrinia: ignoring store at {orphan}
                     using {root}

            to migrate its memories, first see what's there:
              scri list --workspace-root "{orphanParent}"
            then:
              scri bundle export <topics> --workspace-root "{orphanParent}"
              scri bundle import <file> --workspace-root "{root}"
            """;
    }

    /// <summary>
    /// Reports stores that root resolution passed over. Called only from commands that already
    /// do real work — never from the per-prompt hint path.
    /// </summary>
    internal static void WarnOnOrphanStores()
    {
        try
        {
            string root = Root;
            string cwd = Directory.GetCurrentDirectory();
            foreach (string orphan in WorkspaceRoot.FindOrphanStores(root, cwd))
                Console.Error.WriteLine(FormatOrphanWarning(orphan, root, ListTopics(orphan)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Reporting an ignored store is advisory; a filesystem it cannot read (or a path
            // FindOrphanStores rejects via Path.GetFullPath/ArgumentException.ThrowIfNullOrWhiteSpace)
            // must not fail the caller's write.
        }
    }

    /// <summary>
    /// Namespace directory names used by the OLD (pre-v3) <c>.scrinia/topics/</c> layout that
    /// <see cref="ListTopics"/> below still has to interpret. Intentionally NOT
    /// <c>MemoryNaming</c> — that type dropped the "reserved namespace directory" concept
    /// entirely in Task 8, since <see cref="Scrinia.Core.FileMemoryStore"/> itself no longer
    /// reads through it. This scan targets an arbitrary ORPHANED/ignored store elsewhere on
    /// disk that this workstream's automatic migration has never touched (it may still be
    /// sitting in the pre-v3 layout), so the classification still needs to exist somewhere.
    /// </summary>
    private static readonly HashSet<string> s_legacyNamespaceDirs =
        new(StringComparer.OrdinalIgnoreCase) { "entity", "memory", "agent" };

    /// <summary>
    /// Topic names inside an orphaned store, for the migration command — the bare names
    /// <c>scri bundle export</c> actually accepts, not raw directory names. Descends into the
    /// <c>entity/</c> and <c>memory/</c> namespace directories for their real child topics
    /// instead of reporting the namespace directory itself as a topic (it isn't one).
    /// </summary>
    internal static IReadOnlyList<string> ListTopics(string storeDir)
    {
        try
        {
            // The v2 hierarchical layout (.scrinia/memories/<segment>/...) has no flat
            // topic-name equivalent that `scri bundle export --topics` can target. Reporting
            // only the v1 topics here would look like a complete migration command while
            // silently leaving v2 memories behind, so when v2 content exists, report no
            // topics at all — the caller falls back to `scri list` instead of a confidently
            // incomplete command.
            string memoriesDir = Path.Combine(storeDir, "memories");
            if (Directory.Exists(memoriesDir) && Directory.EnumerateFileSystemEntries(memoriesDir).Any())
                return [];

            string topicsDir = Path.Combine(storeDir, "topics");
            if (!Directory.Exists(topicsDir)) return [];

            var topics = new List<string>();
            var namespacedChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string nsDir in Directory.EnumerateDirectories(topicsDir))
            {
                string nsName = Path.GetFileName(nsDir) ?? "";
                if (!s_legacyNamespaceDirs.Contains(nsName)) continue;

                if (nsName.Equals("agent", StringComparison.OrdinalIgnoreCase))
                {
                    // agent is a single scope with no child topic directories.
                    topics.Add("agent");
                    continue;
                }

                foreach (string childDir in Directory.EnumerateDirectories(nsDir))
                {
                    string childName = Path.GetFileName(childDir) ?? "";
                    topics.Add(childName);
                    namespacedChildren.Add(childName);
                }
            }

            foreach (string dir in Directory.EnumerateDirectories(topicsDir))
            {
                string name = Path.GetFileName(dir) ?? "";
                if (s_legacyNamespaceDirs.Contains(name)) continue;
                if (namespacedChildren.Contains(name)) continue; // shadowed by a namespaced entry
                topics.Add(name);
            }

            return [.. topics.Distinct(StringComparer.OrdinalIgnoreCase).Order()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Initializes embeddings and loads optional plugins. Preference order: explicit HTTP
    /// provider → bundled Vulkan plugin → in-process fallback. The principle is "don't load
    /// the Vulkan rigamarole if the user already has an HTTP backend running" — Ollama /
    /// llama.cpp server / LM Studio are usually already up in dev setups, and spinning up
    /// a second model in our own VRAM is wasteful when we could just call out.
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
        var loggerFactory = LoggerFactory.Create(b => b
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Embeddings");

        string workspaceDir = Path.Combine(Root, ".scrinia");
        string embeddingsDir = Path.Combine(workspaceDir, "embeddings");
        string exeDir = AppContext.BaseDirectory;
        string modelsDir = Path.Combine(exeDir, "models");

        // Entity-graph signal is embedder-independent and on by default — set it once here,
        // unconditionally, so it's active regardless of which embeddings branch runs below
        // (or none). Scored as its own fusion source; inert until memories carry entity: keywords.
        EntityGraphScoreContext.Default = new EntityGraphScoreContributor();

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
            MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());
            Console.Error.WriteLine(
                "[scrinia:info] Embeddings plugin exe detected — skipping built-in Model2Vec.");
            await TryLoadVulkanPluginAsync(ct);
        }
        else
        {
            LoadBuiltInEmbeddings(embeddingOptions, embeddingsDir, modelsDir, logger);
        }
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
    /// Unconditional full reindex against the currently-loaded embedding pipeline. Used by the
    /// <c>scri reindex</c> command — bypasses the signature-mismatch gate that
    /// <see cref="MaybeReindexAfterModelSwitch"/> relies on so a user who wants to force a
    /// rebuild (suspected corruption, manual recovery) actually gets one. Must be called after
    /// <see cref="LoadPluginsAsync"/> so the active pipeline is wired.
    ///
    /// <para>Two paths, selected automatically:</para>
    /// <list type="bullet">
    ///   <item><b>In-process provider</b> (Ollama/OpenAI/Voyage HTTP or built-in Model2Vec):
    ///   uses <see cref="EmbeddingReindexer.ForceReindexAsync"/> which batch-embeds and writes
    ///   the host's <see cref="VectorStore"/> directly.</item>
    ///   <item><b>Plugin-owned</b> (Vulkan plugin via MCP): replays each memory through the
    ///   active <see cref="IMemoryEventSink"/> so the plugin's <c>upsert</c> tool rebuilds
    ///   its vector store. The host has no <see cref="IEmbeddingProvider"/> to batch-embed
    ///   in-process in this case.</item>
    /// </list>
    /// </summary>
    internal static async Task<EmbeddingReindexer.Result?> ForceReindexAsync(CancellationToken ct = default)
    {
        var store = MemoryStoreContext.Current;
        if (store is null)
        {
            Console.Error.WriteLine(
                "[scrinia:warn] Reindex skipped: workspace not configured. Run `scri setup` first.");
            return null;
        }

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

        var provider = _embeddingProvider;
        if (provider is { IsAvailable: true })
        {
            var options = BuildEmbeddingOptions();
            string embeddingsDir = Path.Combine(Root, ".scrinia", "embeddings");
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

        // Plugin path — the plugin owns embeddings out-of-process, so we drive it through
        // the event sink it subscribed to during startup. _pluginHost is the IMemoryEventSink
        // (and we registered it as MemoryEventSinkContext.Default's first sink).
        if (_pluginHost is { HasEventSinkCapability: true })
        {
            try
            {
                return await EmbeddingReindexer.ReindexViaSinkAsync(
                    store, _pluginHost, logger, OnProgress, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Forced reindex (plugin) failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        Console.Error.WriteLine(
            "[scrinia:warn] Reindex skipped: no embedding provider is available. " +
            "Run `scri setup` first.");
        return null;
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

                SearchContributorContext.Default = new CompositeSearchScoreContributor(reranker, new PathSegmentScoreContributor());
                MemoryEventSinkContext.InstallDefault(
                    eventHandler,
                    new EntityTypingEventHandler(provider),
                    new MaintenanceEventSink());

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
                SearchContributorContext.Default = new PathSegmentScoreContributor();
                MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to initialize HTTP embeddings ('{options.Provider}'): " +
                $"{ex.GetType().Name}: {ex.Message}");
            SearchContributorContext.Default = new PathSegmentScoreContributor();
            MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());
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

                SearchContributorContext.Default = new CompositeSearchScoreContributor(reranker, new PathSegmentScoreContributor());
                MemoryEventSinkContext.InstallDefault(
                    eventHandler,
                    new EntityTypingEventHandler(provider),
                    new MaintenanceEventSink());

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
                SearchContributorContext.Default ??= new PathSegmentScoreContributor();
                MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to initialize built-in embeddings: " +
                $"{ex.GetType().Name}: {ex.Message}");
            SearchContributorContext.Default ??= new PathSegmentScoreContributor();
            MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());
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
        string dataDir = Path.Combine(Root, ".scrinia");
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
                SearchContributorContext.Default = new CompositeSearchScoreContributor(host, new PathSegmentScoreContributor());
            if (host.HasEventSinkCapability)
                MemoryEventSinkContext.InstallDefault(host, new MaintenanceEventSink());

            _pluginHost = host;

            // Ensure plugin shuts down when the CLI exits.
            // An exception escaping a ProcessExit handler terminates the process, and the plugin
            // child is often already gone by this point (making disposal throw), so the wait is
            // guarded. Nothing useful remains to salvage at exit — log and let the process end.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    host.DisposeAsync().AsTask().Wait(3000);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[scrinia:warn] Embeddings plugin shutdown failed: {ex.GetType().Name}: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Failed to start embeddings plugin: " +
                $"{ex.GetType().Name}: {ex.Message}");
            // Plugin failed — built-in is skipped when the plugin is installed, so without
            // this the workspace would run with no contributor at all. Path scoring is
            // dependency-free, so at minimum we get path-prefix relevance lifts.
            SearchContributorContext.Default ??= new PathSegmentScoreContributor();
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
    /// Resolves a plugin executable name from env var -> config file -> default.
    /// </summary>
    internal static string GetPluginName(string key, string defaultName)
    {
        // 1. Environment variable
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 2. Config file
        value = WorkspaceConfig.GetValue(Root, key);
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
        return WorkspaceConfig.GetValue(Root, key);
    }
}
