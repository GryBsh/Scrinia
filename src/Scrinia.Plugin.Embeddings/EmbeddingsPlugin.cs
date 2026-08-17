using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Vulkan GPU-accelerated embeddings plugin for the server path.
///
/// Implements three interfaces:
/// - <see cref="IMemoryOperationHook"/>: REST path hooks (via PluginPipeline)
/// - <see cref="ISearchScoreContributor"/>: supplemental search scores for both REST and MCP
/// - <see cref="IMemoryEventSink"/>: MCP path event notifications
///
/// For the CLI, built-in Model2Vec embeddings are used by default. This plugin
/// provides optional Vulkan GPU acceleration when installed.
/// </summary>
public sealed class EmbeddingsPlugin : ScriniaPluginBase,
    IMemoryOperationHook, ISearchScoreContributor, IMemoryEventSink, IEmbeddingsAdmin, IDisposable
{
    public override string Name => "Embeddings";
    public override string Version => "2.0.0";

    private IEmbeddingProvider _provider = new NullEmbeddingProvider();
    private EmbeddingOptions _options = new();
    private volatile MemoryVectorIndexer? _indexer;
    private volatile VectorStore? _vectorStore;
    private double _semanticWeight = 50.0;
    private ILogger _logger = null!;
    // Set only when EnsureInitialized had to self-create a fallback console logger (no real
    // ILoggerFactory was ever captured via ConfigureMiddleware — true for every test in this
    // project, which constructs the plugin directly instead of going through the server's DI
    // wiring). Owning it here means Dispose can shut it down instead of leaking it per instance.
    private ILoggerFactory? _ownedLoggerFactory;
    private bool _disposed;
    private readonly object _initLock = new();

    // -- IEmbeddingsAdmin --

    public string ProviderName { get { EnsureInitialized(); return _provider.GetType().Name; } }
    public bool IsAvailable { get { EnsureInitialized(); return _provider.IsAvailable; } }
    public int Dimensions { get { EnsureInitialized(); return _provider.Dimensions; } }
    public double SemanticWeight => _semanticWeight;
    public int TotalVectorCount { get { EnsureInitialized(); return _vectorStore?.Count() ?? 0; } }
    public string Hardware => "Vulkan GPU";

    public void Dispose()
    {
        lock (_initLock)
        {
            if (_disposed) return;
            _disposed = true;
            (_provider as IDisposable)?.Dispose();
            (_vectorStore as IDisposable)?.Dispose();
            _vectorStore = null;
            _indexer = null; // matched pair with _vectorStore everywhere else; don't leave it
                              // holding a reference to the disposed store.
            _ownedLoggerFactory?.Dispose();
            _ownedLoggerFactory = null;
        }
    }

    // -- IScriniaPlugin (server path) --

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Scrinia:Embeddings");

        try
        {
            _options = section.Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        }
        catch (InvalidOperationException)
        {
            // The binder throws InvalidOperationException (verified: Get<T>() on a section with
            // e.g. SemanticWeight: "not-a-number" throws "Failed to convert configuration value
            // ... to type 'System.Double'") for a single malformed value in the section. The old
            // code's manual double.TryParse silently kept the default in this situation instead
            // of crashing server startup; this preserves that resilience. Trade-off: because
            // Get<T>() validates the WHOLE section atomically, one bad property discards every
            // other property that would have bound fine too (e.g. a valid ChunkSize alongside a
            // malformed SemanticWeight) — falling back to full defaults rather than a
            // property-by-property merge. Acceptable: malformed embeddings config is rare and
            // "revert to defaults" is a safe, previously-established failure mode here.
            _options = new EmbeddingOptions();
        }

        // Guardrail mirroring WorkspaceSetup's: a cap at or above the chunk-index stride makes
        // MemoryVectorIndexer's constructor throw, and here that throw would escape from inside
        // EnsureInitialized AFTER VulkanEmbeddingProvider.Create succeeded but BEFORE _vectorStore
        // is assigned — so the init sentinel never latches and every retry leaks another GPU
        // context. Clamp and warn instead.
        int requestedCap = _options.MaxChunksPerMemory;
        if (_options.ClampChunkCapBelowStride())
        {
            // _logger is not wired until ConfigureMiddleware; stderr is the only channel that
            // exists this early, and it is where the CLI reports the identical clamp.
            Console.Error.WriteLine(
                $"[scrinia:warn] MaxChunksPerMemory ({requestedCap}) must be below " +
                $"{MemoryVectorIndexer.ChunkIndexStride}; clamping to {_options.MaxChunksPerMemory}.");
        }

        _semanticWeight = _options.SemanticWeight;

        // Compose with PathSegmentScoreContributor so a memory's qualified path also
        // contributes to relevance — embeddings carry semantic similarity, the path
        // contributor adds a small lift when the agent filed the memory under a
        // path that overlaps the query (e.g. /findings/auth-refactor matched on "auth").
        services.AddSingleton<ISearchScoreContributor>(_ =>
            new CompositeSearchScoreContributor(this, new PathSegmentScoreContributor()));
        services.AddSingleton<IMemoryEventSink>(this);
        services.AddSingleton<IMemoryOperationHook>(this);
        services.AddSingleton<IEmbeddingsAdmin>(this);
    }

    /// <summary>
    /// Captures the real, host-built <see cref="ILoggerFactory"/> — its actual configured sinks,
    /// levels and formatters — once one exists. <see cref="ConfigureServices"/> runs before
    /// <c>WebApplicationBuilder.Build()</c>, so it only has the DI graph's *description*
    /// (<see cref="IServiceCollection"/>), not a working container; building a throwaway provider
    /// there just to read a logger would mean owning and disposing an entire separate container
    /// for one service. <see cref="ConfigureMiddleware"/> runs once at startup, after
    /// <c>Build()</c>, with the actual root <see cref="IServiceProvider"/> the rest of the app
    /// uses — this is the first point a real one is available.
    /// </summary>
    public override void ConfigureMiddleware(IApplicationBuilder app)
    {
        _logger = app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger<EmbeddingsPlugin>()
            ?? _logger;
    }

    /// <summary>
    /// Lazy initialization — called once when we first need the provider.
    /// This allows the plugin to be registered even if the provider fails to initialize.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_vectorStore is not null) return;

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_vectorStore is not null) return;

            if (_logger is null)
            {
                // ConfigureMiddleware never ran (e.g. this plugin was constructed directly,
                // outside the server's DI wiring — every test in this project does this): fall
                // back to a private console logger and own its disposal (see Dispose).
                _ownedLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
                _logger = _ownedLoggerFactory.CreateLogger<EmbeddingsPlugin>();
            }

            string pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
            string dataDir = Path.Combine(pluginsDir, "embeddings");
            Directory.CreateDirectory(dataDir);

            string modelsDir = Path.Combine(pluginsDir, "scri-plugin-embeddings");
            Directory.CreateDirectory(modelsDir);

            // Initialize the provider FIRST so the store can be signed with its signature.
            try
            {
                if (VulkanModelManager.IsModelAvailable(modelsDir))
                {
                    string modelPath = VulkanModelManager.GetModelPath(modelsDir);
                    _provider = VulkanEmbeddingProvider.Create(modelPath, 384, _logger);
                }
                else
                {
                    _logger.LogWarning("Vulkan GGUF model not found at {Dir}. Plugin inactive.", modelsDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Vulkan embedding provider");
            }

            _logger.LogInformation("Embeddings plugin initialized: provider={Provider}, available={Available}",
                _provider.GetType().Name, _provider.IsAvailable);

            // Sign the store with the active provider's signature (null when unavailable — a transient
            // provider-load failure must not quarantine good vectors). On a real provider change, mismatched
            // files self-quarantine on first load and rebuild via reindex.
            string? expectedSignature = ChunkedSignature.ForProvider(_provider, _options);
            var vectorStore = new VectorStore(dataDir, expectedSignature);

            _indexer = new MemoryVectorIndexer(_provider, vectorStore, _logger, _options);
            _vectorStore = vectorStore; // assign LAST: it's the initialized sentinel
        }
    }

    /// <summary>
    /// Test-only seam: performs the SAME assignments <see cref="EnsureInitialized"/> does, in the
    /// SAME order (indexer built before the <c>_vectorStore</c> sentinel is assigned), so an
    /// injected provider/store can never drift from how production wires <see cref="_indexer"/>.
    /// Exists so tests stop hand-reimplementing this construction via reflection — a change to
    /// EnsureInitialized's shape now breaks this method (or callers) at compile time instead of
    /// leaving tests silently building a stale shape. Marking this <c>internal</c> rather than
    /// <c>public</c> keeps it out of the plugin's real surface;
    /// <c>InternalsVisibleTo("Scrinia.Plugin.Embeddings.Tests")</c> is already declared in
    /// AssemblyInfo.cs.
    /// </summary>
    /// <summary>
    /// Test-only seam: the options <see cref="ConfigureServices"/> actually bound. Exists because
    /// the only production consumer of them — the signature passed to <c>VectorStore</c> in
    /// <see cref="EnsureInitialized"/> — is null whenever the provider is unavailable, which is
    /// every test environment (no GGUF model). Without this, "ConfigureServices binds the whole
    /// section, not just SemanticWeight" is unobservable, and that binding is the fix for the
    /// configured chunk settings never reaching the signature.
    /// </summary>
    internal EmbeddingOptions BoundOptionsForTests => _options;

    internal void InitializeForTests(
        IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
    {
        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _provider = provider;
            _logger = logger;
            _options = options ?? _options;
            _indexer = new MemoryVectorIndexer(_provider, store, _logger, _options);
            _vectorStore = store; // assign LAST: it's the initialized sentinel
        }
    }

    // MapEndpoints override removed: the /status, /reindex and /settings routes it used to map
    // under an inline /embeddings group (with no {store} route value — finding H6) are now served
    // for both this plugin and the built-in service by Scrinia.Server.Endpoints.EmbeddingsEndpoints,
    // resolving IEmbeddingsAdmin from DI. ScriniaPluginBase.MapEndpoints is virtual with a no-op
    // default body, so no override is needed here at all.

    // -- ISearchScoreContributor (both REST + MCP) --

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        EnsureInitialized();
        if (_vectorStore is null)
            return null;

        var queryVec = await _provider.EmbedAsync(query, ct);
        if (queryVec is null)
            return null;

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var byScope = candidates.GroupBy(c => c.Scope, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byScope)
        {
            var vectors = _vectorStore.GetVectors(group.Key);
            if (vectors.Count == 0) continue;

            var topK = VectorIndex.Search(queryVec, vectors, vectors.Count);
            VectorScoreProjection.Project(
                group.Key, topK.Select(t => (t.Entry, (double)t.Similarity)), _semanticWeight, scores);
        }

        return scores.Count > 0 ? scores : null;
    }

    // -- IMemoryOperationHook (REST path via PluginPipeline) --

    public async Task OnAfterStoreAsync(AfterStoreContext ctx, CancellationToken ct)
    {
        await EmbedAndIndexAsync(ctx.QualifiedName, ctx.Content, ctx.Store, ct);
    }

    public async Task OnAfterAppendAsync(AfterAppendContext ctx, CancellationToken ct)
    {
        await ReembedFullMemoryAsync(ctx.Name, ctx.Store, ct);
    }

    public async Task OnAfterForgetAsync(AfterForgetContext ctx, CancellationToken ct)
    {
        await RemoveVectorsAsync(ctx.Name, ctx.WasDeleted, ctx.Store, ct);
    }

    // -- IMemoryEventSink (MCP path) --

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, content, store, ct);
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        await ReembedFullMemoryAsync(qualifiedName, store, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        await RemoveVectorsAsync(qualifiedName, wasDeleted, store, ct);
    }

    // -- Shared internal --

    private async Task EmbedAndIndexAsync(
        string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            // Inside the try: EnsureInitialized can throw (bad config reaching
            // MemoryVectorIndexer's ctor, a provider load failure), and these are detached
            // event-sink/hook callbacks — on the REST path PluginPipeline awaits them with no
            // guard of its own, so an escaping throw would land in the caller's request.
            EnsureInitialized();
            if (_indexer is null) return;

            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            await _indexer.IndexAsync(scope, subject, content, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }

    /// <summary>
    /// An append mutates the memory. Indexing only the appended fragment would be wrong twice
    /// over: the indexer replaces a memory's vectors atomically, so the rest of the memory would
    /// lose its vectors entirely, and the surviving element indices would not match the memory's
    /// real content array. Re-read and index the whole thing — shared by both the REST
    /// (<see cref="OnAfterAppendAsync"/>) and MCP (<see cref="OnAppendedAsync"/>) append paths, and
    /// mirrored by <c>BuiltInEmbeddingsService.ReembedFullMemoryAsync</c>.
    /// </summary>
    private async Task ReembedFullMemoryAsync(string qualifiedName, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            // Inside the try — see EmbedAndIndexAsync.
            EnsureInitialized();
            if (_indexer is null) return;

            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            // MemoryArtifactSource, not ReadArtifactAsync: ephemeral memories have no artifact
            // keyed by (scope, subject), so reading them directly throws FileNotFoundException
            // and this catch would silently turn an ephemeral append into a no-op (finding M-2).
            var elements = await MemoryArtifactSource.ReadContentElementsAsync(store, scope, subject, ct);
            if (elements.Length == 0) return;

            await _indexer.IndexAsync(scope, subject, elements, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to re-embed appended memory '{Name}'", qualifiedName);
        }
    }

    private async Task RemoveVectorsAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        if (!wasDeleted) return;

        try
        {
            // Inside the try — see EmbedAndIndexAsync. The (qualifiedName, store) overload below
            // already owns its own parse, but EnsureInitialized does not, and PluginPipeline
            // awaits OnAfterForgetAsync with no try/catch on the REST path, so a throw here
            // would otherwise propagate into the forget request itself.
            EnsureInitialized();
            if (_indexer is null) return;

            await _indexer.RemoveAsync(qualifiedName, store, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }

    public async Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
    {
        EnsureInitialized();
        var allItems = store.ListScoped();
        int count = 0;

        foreach (var item in allItems)
        {
            try
            {
                // MemoryArtifactSource owns both halves of this read: resolving by
                // (scope, subject) rather than through the lossy FormatQualifiedName round trip,
                // and taking the "~name" path for ephemeral memories. It also reconstructs the
                // memory's content array rather than one decoded blob, so reindex reproduces the
                // same per-element layout a write produces.
                var elements = await MemoryArtifactSource.ReadContentElementsAsync(
                    store, item.Scope, item.Entry.Name, ct);
                if (elements.Length == 0) continue;

                // Bulk loop: retryOnUnavailable:false lets VectorStoreUnavailableException reach
                // the catch ladder below instead of being retried for up to 42s per memory and
                // then swallowed to false.
                if (await _indexer!.IndexAsync(item.Scope, item.Entry.Name, elements, ct, retryOnUnavailable: false))
                    count++;
            }
            catch (VectorStoreUnavailableException ex)
            {
                // Not counted as reindexed, so a later run retries it once the file is readable.
                _logger?.LogWarning("Skipped reindexing '{Name}': {Reason}", item.Entry.Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reindex '{Name}'", item.Entry.Name);
            }
        }

        return count;
    }
}
