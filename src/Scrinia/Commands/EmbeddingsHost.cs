using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Services;

namespace Scrinia.Commands;

/// <summary>
/// Embeddings and plugin lifecycle for the CLI process. Extracted from the former
/// <c>WorkspaceSetup</c> composition root so that type owns only workspace identity.
/// All members take the workspace root explicitly rather than reading a process-wide
/// accessor, which keeps the host independently testable.
/// </summary>
internal static class EmbeddingsHost
{
    private static McpPluginHost? _pluginHost;
    private static IEmbeddingProvider? _embeddingProvider;
    private static readonly AsyncLocal<EmbeddingProviderOverride?> _embeddingProviderOverride = new();

    /// <summary>Temporarily replaces provider discovery for an isolated test flow.</summary>
    internal static IDisposable OverrideEmbeddingProviderForTest(IEmbeddingProvider? provider)
    {
        EmbeddingProviderOverride? prior = _embeddingProviderOverride.Value;
        _embeddingProviderOverride.Value = new EmbeddingProviderOverride(provider);
        return new RestoreEmbeddingProviderOverride(prior);
    }

    private sealed record EmbeddingProviderOverride(IEmbeddingProvider? Provider);

    private sealed class RestoreEmbeddingProviderOverride(EmbeddingProviderOverride? prior) : IDisposable
    {
        public void Dispose() => _embeddingProviderOverride.Value = prior;
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
    internal static async Task LoadPluginsAsync(string workspaceRoot, CancellationToken ct = default)
    {
        if (_embeddingProviderOverride.Value is { } providerOverride)
        {
            return;
        }

        var loggerFactory = LoggerFactory.Create(b => b
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("Scrinia.Embeddings");

        string workspaceDir = Path.Combine(workspaceRoot, ".scrinia");
        string embeddingsDir = Path.Combine(workspaceDir, "embeddings");
        // Models are mutable runtime state and live only under SCRINIA_HOME/models; the immutable
        // global tool package (AppContext.BaseDirectory) is never written at runtime.
        string modelsDir = Path.Combine(UserHome.Resolve(), "models");

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
            await TryLoadVulkanPluginAsync(workspaceRoot, ct);
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
    ///   active <see cref="IEmbeddingIndexCommand"/> so the plugin's <c>index</c> tool rebuilds
    ///   its vector store. The host has no <see cref="IEmbeddingProvider"/> to batch-embed
    ///   in-process in this case.</item>
    /// </list>
    /// </summary>
    internal static async Task<EmbeddingReindexer.Result?> ForceReindexAsync(string workspaceRoot, CancellationToken ct = default)
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

        EmbeddingProviderOverride? providerOverride = _embeddingProviderOverride.Value;
        var provider = providerOverride is null ? _embeddingProvider : providerOverride.Provider;
        if (provider is { IsAvailable: true })
        {
            var options = BuildEmbeddingOptions();
            string embeddingsDir = Path.Combine(workspaceRoot, ".scrinia", "embeddings");
            try
            {
                return await EmbeddingReindexer.ForceReindexAsync(
                    store, provider, embeddingsDir, logger, OnProgress, ct, options);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Forced reindex failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Keep the acknowledged administrative path even if the plugin has degraded:
        // its unavailable outcomes must be reported as failures for the requested memories.
        if (_pluginHost is not null)
        {
            try
            {
                return await EmbeddingReindexer.ReindexViaCommandAsync(
                    store, _pluginHost, logger, OnProgress, ct);
            }
            catch (OperationCanceledException) { throw; }
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
    internal static bool IsHttpEmbeddingsProvider(string provider) =>
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
                InstallEmbeddingEventSinks(eventHandler, new EntityTypingEventHandler(provider));

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
                InstallEmbeddingEventSinks(eventHandler, new EntityTypingEventHandler(provider));

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

    internal static void InstallEmbeddingEventSinks(
        IMemoryEventSink eventHandler, IMemoryEventSink entityTypingHandler)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        ArgumentNullException.ThrowIfNull(entityTypingHandler);

        MemoryEventSinkContext.InstallDefault(
            new MaintenanceEventSink(),
            entityTypingHandler,
            eventHandler);
    }

    /// <summary>
    /// Checks whether the embeddings plugin exe is on disk. Used to decide whether to skip
    /// the built-in Model2Vec init — when the plugin is installed it will own embeddings.
    /// </summary>
    internal static bool IsEmbeddingsPluginInstalled() => ResolveEmbeddingsPluginExe() is not null;

    /// <summary>
    /// Resolves the embeddings plugin executable through the mandatory
    /// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> hierarchy — never the
    /// executable-relative <c>AppContext.BaseDirectory/plugins</c> tree the pre-fix discovery scanned.
    /// Reads the configured plugin name and compatible version from config, then locates the plugin's
    /// own executable inside the resolved version/RID directory.
    /// </summary>
    internal static string? ResolveEmbeddingsPluginExe()
    {
        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
        string? version = WorkspaceSetup.GetConfigValue("plugins:embeddings:version");

        PluginDirectoryResult resolution = ResolvePluginDirectory(pluginName, version);
        if (resolution.PluginDir is null)
        {
            return null;
        }

        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string exe = Path.Combine(resolution.PluginDir, $"{pluginName}{ext}");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// The outcome of resolving the mandatory plugin discovery path: the resolved
    /// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> directory (or
    /// <c>null</c> when the plugin is absent or rejected), whether the resolution degraded because the
    /// current RID was unavailable, and a diagnostic naming the searched hierarchy and <c>SCRINIA_HOME</c>.
    /// </summary>
    internal sealed record PluginDirectoryResult(string? PluginDir, bool Degraded, string Diagnostics);

    /// <summary>
    /// Resolves the plugin executable directory through the mandatory
    /// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> hierarchy for the given
    /// plugin name and configured version, selecting the compatible version and current RID
    /// deterministically and proving containment inside <c>SCRINIA_HOME</c>.
    ///
    /// <para>There is no flat fallback: a <c>plugins/&lt;plugin&gt;&lt;ext&gt;</c>,
    /// <c>plugins/&lt;plugin&gt;/&lt;plugin&gt;</c>, or executable-relative path is never accepted.</para>
    /// </summary>
    internal static PluginDirectoryResult ResolvePluginDirectory(string pluginName, string? configuredVersion)
    {
        string home = UserHome.Resolve();
        string hierarchy = $"SCRINIA_HOME/plugins/{pluginName}/<version>/<rid>/";
        string pluginRoot = Path.Combine(home, "plugins", pluginName);
        string diagnostics =
            $"embeddings plugin not found under {hierarchy} (searched {pluginRoot}; SCRINIA_HOME={home})";

        if (!IsSafeSegment(pluginName) ||
            (configuredVersion is not null && configuredVersion.Length > 0 && !IsSafeSegment(configuredVersion)))
        {
            return new PluginDirectoryResult(null, false, diagnostics);
        }

        if (!Directory.Exists(pluginRoot))
        {
            return new PluginDirectoryResult(null, false, diagnostics);
        }

        string? version = SelectCompatibleVersion(pluginRoot, configuredVersion);
        if (version is null)
        {
            return new PluginDirectoryResult(null, false, diagnostics);
        }

        string versionDir = Path.Combine(pluginRoot, version);
        (string? ridDir, bool degraded) = SelectRidDirectory(versionDir);
        if (ridDir is null)
        {
            return new PluginDirectoryResult(null, false, diagnostics);
        }

        // Fail closed: the resolved directory must live inside SCRINIA_HOME — a plugin name or version
        // carrying a traversal segment can never escape the home into an arbitrary directory.
        if (!PathContainment.IsWithin(home, ridDir))
        {
            return new PluginDirectoryResult(null, false, diagnostics);
        }

        diagnostics = degraded
            ? $"embeddings plugin degraded to {ridDir} (current RID {CurrentRid()} absent under {versionDir}) " +
              $"under mandatory {hierarchy} at SCRINIA_HOME={home}"
            : $"embeddings plugin at {ridDir} under mandatory {hierarchy} at SCRINIA_HOME={home}";
        return new PluginDirectoryResult(ridDir, degraded, diagnostics);
    }

    private static bool IsSafeSegment(string segment) =>
        !string.IsNullOrEmpty(segment)
        && !segment.Contains(Path.DirectorySeparatorChar)
        && !segment.Contains(Path.AltDirectorySeparatorChar)
        && segment != "..";

    private static string CurrentRid() => EmbeddingsRuntimeIdentity.CurrentRid;

    /// <summary>
    /// Selects the version directory to use: the configured version when it is installed, otherwise the
    /// highest installed version directory (deterministic, semver-ordered). Returns <c>null</c> when no
    /// installable version directory exists.
    /// </summary>
    private static string? SelectCompatibleVersion(string pluginRoot, string? configuredVersion)
    {
        if (!string.IsNullOrEmpty(configuredVersion))
        {
            string configuredDir = Path.Combine(pluginRoot, configuredVersion);
            if (Directory.Exists(configuredDir))
            {
                return configuredVersion;
            }
        }

        Version? best = null;
        string? bestDir = null;
        foreach (string dir in Directory.EnumerateDirectories(pluginRoot))
        {
            string? name = Path.GetFileName(dir);
            if (name is null || !Version.TryParse(name, out Version? parsed) || parsed is null) continue;
            if (best is null || parsed.CompareTo(best) > 0)
            {
                best = parsed;
                bestDir = dir;
            }
        }

        return bestDir;
    }

    /// <summary>
    /// Selects the RID directory to use: the current platform RID when installed, otherwise the single
    /// available RID directory (degraded), otherwise <c>null</c>.
    /// </summary>
    private static (string? ridDir, bool degraded) SelectRidDirectory(string versionDir)
    {
        string currentRid = CurrentRid();
        string ridDir = Path.Combine(versionDir, currentRid);
        if (Directory.Exists(ridDir))
        {
            return (ridDir, false);
        }

        string[] ridDirs = Directory.EnumerateDirectories(versionDir).ToArray();
        return ridDirs.Length is 1 ? (ridDirs[0], true) : (null, false);
    }

    private static async Task TryLoadVulkanPluginAsync(string workspaceRoot, CancellationToken ct = default)
    {
        string? embeddingsExe = ResolveEmbeddingsPluginExe();
        if (embeddingsExe is null) return;

        // Vector data lives in the workspace-local .scrinia/ directory (per-project isolation).
        string dataDir = Path.Combine(workspaceRoot, ".scrinia");
        // Models are mutable runtime state and live only under SCRINIA_HOME/models; the immutable
        // global tool package (AppContext.BaseDirectory) is never written at runtime.
        string modelsDir = Path.Combine(UserHome.Resolve(), "models");
        VerifiedModelDownloader.PrepareModelDirectory(modelsDir);

        try
        {
            var host = new McpPluginHost();
            await host.StartAsync(embeddingsExe, dataDir, modelsDir, WorkspaceSetup.GetConfigValue, ct);

            // If the plugin has no working provider, shut it down immediately
            // to avoid wasting a child process.
            if (!host.HasSearchCapability && !host.HasEventSinkCapability)
            {
                await host.DisposeAsync();
                SearchContributorContext.Default ??= new PathSegmentScoreContributor();
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

        string? provider = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:Provider");
        if (provider is not null) options.Provider = provider;

        string? weight = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:SemanticWeight");
        if (weight is not null && double.TryParse(weight, NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
            options.SemanticWeight = w;

        string? ollamaUrl = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:OllamaBaseUrl");
        if (ollamaUrl is not null) options.OllamaBaseUrl = ollamaUrl;

        string? ollamaModel = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:OllamaModel");
        if (ollamaModel is not null) options.OllamaModel = ollamaModel;

        string? openAiKey = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:OpenAiApiKey");
        if (openAiKey is not null) options.OpenAiApiKey = openAiKey;

        string? openAiModel = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:OpenAiModel");
        if (openAiModel is not null) options.OpenAiModel = openAiModel;

        string? openAiUrl = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:OpenAiBaseUrl");
        if (openAiUrl is not null) options.OpenAiBaseUrl = openAiUrl;

        string? voyageKey = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:VoyageAiApiKey");
        if (voyageKey is not null) options.VoyageAiApiKey = voyageKey;

        string? voyageModel = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:VoyageAiModel");
        if (voyageModel is not null) options.VoyageAiModel = voyageModel;

        string? voyageUrl = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:VoyageAiBaseUrl");
        if (voyageUrl is not null) options.VoyageAiBaseUrl = voyageUrl;

        string? chunkSize = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:ChunkSize");
        if (chunkSize is not null && int.TryParse(chunkSize, out int cs) && cs > 0) options.ChunkSize = cs;

        string? chunkOverlap = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:ChunkOverlap");
        if (chunkOverlap is not null && int.TryParse(chunkOverlap, out int co) && co >= 0) options.ChunkOverlap = co;

        string? maxChunks = WorkspaceSetup.GetConfigValue("Scrinia:Embeddings:MaxChunksPerMemory");
        if (maxChunks is not null && int.TryParse(maxChunks, out int mc) && mc > 0) options.MaxChunksPerMemory = mc;

        // NormalizeGuardrails is the one shared rule for every out-of-range EmbeddingOptions
        // value (ChunkOverlap >= ChunkSize, a non-positive MaxChunksPerMemory, a cap at or above
        // the chunk-index stride) — this host reports each correction to stderr. See
        // EmbeddingOptions.NormalizeGuardrails for why each one matters.
        foreach (string message in options.NormalizeGuardrails())
            Console.Error.WriteLine($"[scrinia:warn] {message}");

        return options;
    }
}
