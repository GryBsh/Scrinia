using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Vulkan GPU-accelerated embeddings plugin.
///
/// Implements three interfaces:
/// - <see cref="IMemoryOperationHook"/>: indexes memories after store mutations
/// - <see cref="ISearchScoreContributor"/>: supplies supplemental search scores
/// - <see cref="IMemoryEventSink"/>: receives store event notifications
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
    // ILoggerFactory was ever captured during plugin setup — true for every test in this project,
    // which constructs the plugin directly instead of going through host wiring. Owning it here
    // means Dispose can shut it down instead of leaking it per instance.
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

    /// <summary>
    /// Binds <c>Scrinia:Embeddings</c> into this plugin's options and applies the guardrails.
    ///
    /// <para>This method binds configuration without requiring a host-specific service container.
    /// A plugin reaches Scrinia by implementing its contracts, not by being handed over through a
    /// service collection.</para>
    ///
    /// <para>Optional: a plugin constructed without it runs on <see cref="EmbeddingOptions"/>
    /// defaults.</para>
    /// </summary>
    public void Configure(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
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
            // of failing application startup; this preserves that resilience. Trade-off: because
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
            // _logger is not wired until EnsureInitialized builds one; stderr is the only channel
            // that exists this early, and it is where the CLI reports the identical clamp.
            Console.Error.WriteLine(
                $"[scrinia:warn] MaxChunksPerMemory ({requestedCap}) must be below " +
                $"{MemoryVectorIndexer.ChunkIndexStride}; clamping to {_options.MaxChunksPerMemory}.");
        }

        _semanticWeight = _options.SemanticWeight;
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
                // No logger has been handed in. Build a private console logger and own its
                // disposal (see Dispose).
                _ownedLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
                _logger = _ownedLoggerFactory.CreateLogger<EmbeddingsPlugin>();
            }

            string scriniaHome = UserHome.Resolve();
            string dataDir = Path.Combine(scriniaHome, "plugins", "scri-plugin-embeddings", "data");
            Directory.CreateDirectory(dataDir);

            string modelsDir = Path.Combine(scriniaHome, "models");
            VerifiedModelDownloader.PrepareModelDirectory(modelsDir);

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
                _provider = new NullEmbeddingProvider(); // reset so a failed create never leaves a half-bound provider for the next region
            }

            _logger.LogInformation("Embeddings plugin initialized: provider={Provider}, available={Available}",
                _provider.GetType().Name, _provider.IsAvailable);

            // The provider is now owned by this plugin. If wiring the store below fails, this region
            // can only FAIL CLOSED by returning the sentinel to null — and it disposes the created
            // provider (releasing its llama GPU context) before resetting, so a retry never leaks a
            // second GPU context.
            try
            {
                // Sign the store with the active provider's signature (null when unavailable — a transient
                // provider-load failure must not quarantine good vectors). On a real provider change, mismatched
                // files self-quarantine on first load and rebuild via reindex.
                string? expectedSignature = ChunkedSignature.ForProvider(_provider, _options);
                var vectorStore = new VectorStore(dataDir, expectedSignature);

                _indexer = new MemoryVectorIndexer(_provider, vectorStore, _logger, _options);
                _vectorStore = vectorStore; // assign LAST: it's the initialized sentinel
            }
            catch (Exception ex)
            {
                // A provider was created successfully but store/indexer wiring failed. Dispose the
                // (GPU-backed) provider so its llama context is released before re-entry, then reset the
                // sentinel and indexer so the next EnsureInitialized retries from a clean NullEmbeddingProvider.
                (_provider as IDisposable)?.Dispose();
                _provider = new NullEmbeddingProvider();
                _indexer = null;
                _logger.LogWarning(ex, "Failed to wire embeddings store after provider creation; disposing provider");
            }
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
    /// Test-only seam: the options <see cref="Configure"/> actually bound. Exists because
    /// the only production consumer of them — the signature passed to <c>VectorStore</c> in
    /// <see cref="EnsureInitialized"/> — is null whenever the provider is unavailable, which is
    /// every test environment (no GGUF model). Without this, "Configure binds the whole
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

    // IEmbeddingsAdmin remains the provider administration contract; the plugin host discovers
    // this implementation through its current plugin interfaces.

    // -- ISearchScoreContributor --

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

    // -- IMemoryOperationHook --

    public async Task OnAfterStoreAsync(AfterStoreContext ctx, CancellationToken ct)
    {
        await EmbedAndIndexAsync(ctx.Address, ctx.Content, ctx.Store, ct);
    }

    public async Task OnAfterAppendAsync(AfterAppendContext ctx, CancellationToken ct)
    {
        await ReembedFullMemoryAsync(ctx.Address, ctx.Store, ct);
    }

    public async Task OnAfterForgetAsync(AfterForgetContext ctx, CancellationToken ct)
    {
        await RemoveVectorsAsync(ctx.Address, ctx.WasDeleted, ctx.Store, ct);
    }

    // -- IMemoryEventSink (MCP path) --

    public async Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
    {
        await EmbedAndIndexAsync(address, content, store, ct);
    }

    public async Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
    {
        await ReembedFullMemoryAsync(address, store, ct);
    }

    public async Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
    {
        await RemoveVectorsAsync(address, wasDeleted, store, ct);
    }

    // -- Shared internal --

    private async Task EmbedAndIndexAsync(
        MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
    {
        try
        {
            // Inside the try: EnsureInitialized can throw (bad config reaching
            // MemoryVectorIndexer's ctor, a provider load failure), and these are detached
            // event-sink/hook callbacks, so an escaping throw would land in the caller's operation.
            EnsureInitialized();
            if (_indexer is null) return;

            await _indexer.IndexAsync(address, store, content, ct);
        }
        catch (Exception ex)
        {
            LogObserverFailure("store", ex);
        }
    }

    /// <summary>
    /// An append mutates the memory. Indexing only the appended fragment would be wrong twice
    /// over: the indexer replaces a memory's vectors atomically, so the rest of the memory would
    /// lose its vectors entirely, and the surviving element indices would not match the memory's
    /// real content array. Re-read and index the whole thing — shared by both append hooks and
    /// mirrored by the shared core embedding event handler.
    /// </summary>
    private async Task ReembedFullMemoryAsync(MemoryAddress address, IPluginStore store, CancellationToken ct)
    {
        try
        {
            // Inside the try — see EmbedAndIndexAsync.
            EnsureInitialized();
            if (_indexer is null) return;

            // ReadForAppendAsync, not ParseQualifiedName + ReadContentElementsAsync: ephemeral
            // memories have no artifact keyed by (scope, subject) — ParseQualifiedName itself
            // parses "~note" to SCOPE "local" (it has no "~" branch), so a scope-keyed ephemeral
            // check can never see it. Reading via the scope-keyed path throws FileNotFoundException
            // and this catch would silently turn an ephemeral append into a no-op (finding M-2).
            // ReadForAppendAsync checks ephemerality on the NAME instead, and still returns the
            // ParseQualifiedName-derived (scope, subject) pair, so the index write below lands on
            // exactly the same key the store path used.
            var elements = await MemoryArtifactSource.ReadForAppendAsync(store, address, ct);
            if (elements.Length == 0) return;

            await _indexer.IndexAsync(address, store, elements, ct);
        }
        catch (Exception ex)
        {
            LogObserverFailure("append", ex);
        }
    }

    private async Task RemoveVectorsAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
    {
        if (!wasDeleted) return;

        try
        {
            // Inside the try — see EmbedAndIndexAsync. The (qualifiedName, store) overload below
            // already owns its own parse, but EnsureInitialized does not, so a throw here would
            // otherwise propagate into the forget operation itself.
            EnsureInitialized();
            if (_indexer is null) return;

            await _indexer.RemoveAsync(address, store, ct);
        }
        catch (Exception ex)
        {
            LogObserverFailure("forget", ex);
        }
    }

    private void LogObserverFailure(string operation, Exception exception) =>
        _logger?.LogWarning("Embedding {Operation} observer failed: {Detail}", operation,
            MultiMemoryStableDetail.SanitizeOrFallback(exception.Message));

    public async Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
    {
        EnsureInitialized();
        var allItems = store.ListScoped()
            .Where(item => !MemoryArtifactSource.IsEphemeral(item.Scope))
            .DistinctBy(item => item.Entry.Address)
            .ToArray();
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
                    store, item.Entry.Address, ct);
                if (elements.Length == 0) continue;

                // Bulk loop: retryOnUnavailable:false lets VectorStoreUnavailableException reach
                // the catch ladder below instead of being retried for up to 42s per memory and
                // then swallowed to false.
                if (await _indexer!.IndexAsync(item.Entry.Address, store, elements, ct, retryOnUnavailable: false))
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
