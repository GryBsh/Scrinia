using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    IMemoryOperationHook, ISearchScoreContributor, IMemoryEventSink, IDisposable
{
    public override string Name => "Embeddings";
    public override string Version => "2.0.0";

    private IEmbeddingProvider _provider = new NullEmbeddingProvider();
    private volatile VectorStore? _vectorStore;
    private double _semanticWeight = 50.0;
    private ILogger _logger = null!;
    private bool _disposed;
    private readonly object _initLock = new();

    public void Dispose()
    {
        lock (_initLock)
        {
            if (_disposed) return;
            _disposed = true;
            (_provider as IDisposable)?.Dispose();
            (_vectorStore as IDisposable)?.Dispose();
            _vectorStore = null;
        }
    }

    // -- IScriniaPlugin (server path) --

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Scrinia:Embeddings");

        string? weightStr = section["SemanticWeight"];
        if (weightStr is not null && double.TryParse(weightStr, out double w))
            _semanticWeight = w;

        // Compose with PathSegmentScoreContributor so a memory's qualified path also
        // contributes to relevance — embeddings carry semantic similarity, the path
        // contributor adds a small lift when the agent filed the memory under a
        // path that overlaps the query (e.g. /findings/auth-refactor matched on "auth").
        services.AddSingleton<ISearchScoreContributor>(_ =>
            new CompositeSearchScoreContributor(this, new PathSegmentScoreContributor()));
        services.AddSingleton<IMemoryEventSink>(this);
        services.AddSingleton<IMemoryOperationHook>(this);
    }

    /// <summary>
    /// Lazy initialization — called once when we first need the provider.
    /// This allows the plugin to be registered even if the provider fails to initialize.
    /// </summary>
    private void EnsureInitialized(IServiceProvider? sp = null)
    {
        if (_vectorStore is not null) return;

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_vectorStore is not null) return;

            _logger ??= sp?.GetService<ILoggerFactory>()?.CreateLogger<EmbeddingsPlugin>()
                ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<EmbeddingsPlugin>();

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
            string? expectedSignature = ChunkedSignature.ForProvider(_provider, new EmbeddingOptions());
            var vectorStore = new VectorStore(dataDir, expectedSignature);

            _vectorStore = vectorStore; // assign LAST: it's the initialized sentinel
        }
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/embeddings");

        group.MapGet("/status", (HttpContext ctx) =>
        {
            EnsureInitialized(ctx.RequestServices);
            return Results.Ok(new
            {
                provider = _provider.GetType().Name,
                available = _provider.IsAvailable,
                dimensions = _provider.Dimensions,
                semanticWeight = _semanticWeight,
                vectorCount = _vectorStore?.Count() ?? 0,
            });
        });

        group.MapPost("/reindex", async (HttpContext ctx, CancellationToken ct) =>
        {
            EnsureInitialized(ctx.RequestServices);
            if (!_provider.IsAvailable)
                return Results.BadRequest(new { error = "Embedding provider is not available." });

            var store = MemoryStoreContext.Current;
            if (store is null)
                return Results.BadRequest(new { error = "No store context available." });

            int count = await ReindexStoreAsync(store, ct);
            return Results.Ok(new { message = $"Reindexed {count} memories." });
        });

        group.MapGet("/settings", (HttpContext ctx) =>
        {
            EnsureInitialized(ctx.RequestServices);
            return Results.Ok(new
            {
                provider = _provider.GetType().Name,
                semanticWeight = _semanticWeight,
            });
        });
    }

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
            foreach (var (entry, similarity) in topK)
            {
                string key = entry.ChunkIndex is not null
                    ? $"{group.Key}|{entry.Name}|{entry.ChunkIndex}"
                    : $"{group.Key}|{entry.Name}";

                scores[key] = similarity * _semanticWeight;
            }
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
        await EmbedAndIndexAsync(ctx.Name, [ctx.Content], ctx.Store, ct);
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
        EnsureInitialized();
        if (_vectorStore is null) return;

        // An append mutates the memory: re-embed the FULL current content, not just the appended
        // fragment (which would overwrite the document vector with just the fragment). Read the
        // current artifact and route it through the shared embed path (which now Removes stale
        // vectors first). ReadArtifactAsync returns the ENCODED nmp2 artifact — decode it the same
        // way EmbeddingReindexer does before embedding.
        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            string artifact = await store.ReadArtifactAsync(subject, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return;
            string fullText = System.Text.Encoding.UTF8.GetString(
                new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact));
            await EmbedAndIndexAsync(qualifiedName, [fullText], store, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to re-embed appended memory '{Name}'", qualifiedName);
        }
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        await RemoveVectorsAsync(qualifiedName, wasDeleted, store, ct);
    }

    // -- Shared internal --

    private async Task EmbedAndIndexAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        EnsureInitialized();
        if (_vectorStore is null)
            return;

        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);

            string joined = string.Concat(content);
            if (string.IsNullOrWhiteSpace(joined))
                return;

            var items = new List<(string text, int? chunkIndex)> { (joined, null) };

            if (content.Length > 1)
            {
                for (int i = 0; i < content.Length; i++)
                    if (!string.IsNullOrWhiteSpace(content[i]))
                        items.Add((content[i], i + 1));
            }

            var vectors = await _provider.EmbedBatchAsync(
                items.Select(x => x.text).ToList(), ct);
            if (vectors is null) return;

            // Atomic per-memory replace: clear prior vectors (including stale higher-index chunks
            // from a previous, larger chunk count) before upserting the new set — mirrors
            // EmbeddingReindexer. No-op when the name has no prior vectors, so first-time stores
            // are unaffected.
            await _vectorStore.RemoveAsync(scope, subject, ct);

            for (int i = 0; i < vectors.Length; i++)
                await _vectorStore.UpsertAsync(scope, subject, items[i].chunkIndex, vectors[i], ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }

    private async Task RemoveVectorsAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        if (!wasDeleted) return;

        EnsureInitialized();
        if (_vectorStore is null) return;

        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            await _vectorStore.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }

    internal async Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
    {
        var allItems = store.ListScoped();
        int count = 0;

        foreach (var item in allItems)
        {
            try
            {
                // Resolve directly by (scope, subject) rather than round-tripping through
                // FormatQualifiedName -> ResolveArtifactAsync -> ParseQualifiedName: for a v1
                // namespaced topic scope (e.g. "local:memory/api"), FormatQualifiedName
                // intentionally strips the "memory/" prefix for display, so re-parsing that
                // string reconstructs the WRONG scope and the artifact can never be found.
                // Mirrors EmbeddingReindexer.ReindexAsync's fix for the identical bug. Ephemeral
                // memories aren't backed by an on-disk artifact keyed by (scope, subject), so
                // they still resolve via the "~name" qualified-name path.
                string artifact = item.Scope == "ephemeral"
                    ? await store.ResolveArtifactAsync($"~{item.Entry.Name}", ct)
                    : await store.ReadArtifactAsync(item.Entry.Name, item.Scope, ct);
                string decoded = System.Text.Encoding.UTF8.GetString(
                    new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact));

                var vec = await _provider.EmbedAsync(decoded, ct);
                if (vec is not null)
                {
                    await _vectorStore!.UpsertAsync(item.Scope, item.Entry.Name, null, vec, ct);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reindex '{Name}'", item.Entry.Name);
            }
        }

        return count;
    }
}
