using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Plugin.Embeddings;

internal sealed class EmbeddingsSettingsUpdate
{
    public double? SemanticWeight { get; init; }
    public int? MaxBatchSize { get; init; }
}

/// <summary>
/// Embeddings plugin: adds semantic search via vector embeddings.
///
/// Implements three interfaces for the server path:
/// - <see cref="IMemoryOperationHook"/>: REST path hooks (via PluginPipeline)
/// - <see cref="ISearchScoreContributor"/>: supplemental search scores for both REST and MCP
/// - <see cref="IMemoryEventSink"/>: MCP path event notifications
///
/// No double-firing: REST uses hooks, MCP uses event sink. Both call the same internal methods.
///
/// For the CLI (trimmed single-file host), see <c>Scrinia.Plugin.Embeddings.Cli</c> which
/// runs as a child process communicating via stdin/stdout JSON.
/// </summary>
public sealed class EmbeddingsPlugin : ScriniaPluginBase,
    IMemoryOperationHook, ISearchScoreContributor, IMemoryEventSink
{
    public override string Name => "Embeddings";
    public override string Version => "1.0.0";

    private IEmbeddingProvider _provider = new NullEmbeddingProvider();
    private VectorStore? _vectorStore;
    private EmbeddingOptions _options = new();
    private ILogger _logger = null!;

    // ── IScriniaPlugin (server path) ─────────────────────────────────────────

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Scrinia:Embeddings");
        var options = new EmbeddingOptions();
        section.Bind(options);
        _options = options;

        services.AddSingleton(options);
        services.AddSingleton<ISearchScoreContributor>(this);
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

        _logger ??= sp?.GetService<ILoggerFactory>()?.CreateLogger<EmbeddingsPlugin>()
            ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<EmbeddingsPlugin>();

        string pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        string dataDir = Path.Combine(pluginsDir, "embeddings");
        Directory.CreateDirectory(dataDir);

        // Plugin files (models, caches) go in a folder named after the plugin, alongside the server.
        string modelsDir = Path.Combine(pluginsDir, "scri-plugin-embeddings");
        Directory.CreateDirectory(modelsDir);

        _vectorStore = new VectorStore(dataDir);
        _provider = EmbeddingProviderFactory.Create(_options, modelsDir, _logger);

        _logger.LogInformation("Embeddings plugin initialized: provider={Provider}, available={Available}",
            _options.Provider, _provider.IsAvailable);
    }

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/embeddings");

        group.MapGet("/status", (HttpContext ctx) =>
        {
            EnsureInitialized(ctx.RequestServices);
            return Results.Ok(new
            {
                provider = _options.Provider,
                hardware = _options.Hardware,
                available = _provider.IsAvailable,
                dimensions = _provider.Dimensions,
                semanticWeight = _options.SemanticWeight,
                vectorCount = _vectorStore?.TotalVectorCount() ?? 0,
            });
        });

        group.MapPost("/reindex", async (HttpContext ctx, CancellationToken ct) =>
        {
            EnsureInitialized(ctx.RequestServices);
            if (!_provider.IsAvailable)
                return Results.BadRequest(new { error = "Embedding provider is not available." });

            // Get the memory store from MemoryStoreContext (set by server middleware/MCP)
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
                provider = _options.Provider,
                hardware = _options.Hardware,
                semanticWeight = _options.SemanticWeight,
                maxBatchSize = _options.MaxBatchSize,
                ollamaBaseUrl = _options.OllamaBaseUrl,
                ollamaModel = _options.OllamaModel,
                openAiModel = _options.OpenAiModel,
                openAiBaseUrl = _options.OpenAiBaseUrl,
            });
        });

        group.MapPut("/settings", async (HttpContext ctx) =>
        {
            EnsureInitialized(ctx.RequestServices);
            var dto = await ctx.Request.ReadFromJsonAsync<EmbeddingsSettingsUpdate>(cancellationToken: ctx.RequestAborted);
            if (dto is null) return Results.BadRequest(new { error = "Invalid request body." });

            if (dto.SemanticWeight is not null)
            {
                if (dto.SemanticWeight.Value < 0 || dto.SemanticWeight.Value > 200)
                    return Results.BadRequest(new { error = "SemanticWeight must be between 0 and 200." });
                _options.SemanticWeight = dto.SemanticWeight.Value;
            }
            if (dto.MaxBatchSize is not null)
            {
                if (dto.MaxBatchSize.Value < 1 || dto.MaxBatchSize.Value > 64)
                    return Results.BadRequest(new { error = "MaxBatchSize must be between 1 and 64." });
                _options.MaxBatchSize = dto.MaxBatchSize.Value;
            }

            return Results.Ok(new { message = "Settings updated." });
        });
    }

    // ── ISearchScoreContributor (both REST + MCP) ────────────────────────────

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

        // Group candidates by scope to load vectors efficiently
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

                scores[key] = similarity * _options.SemanticWeight;
            }
        }

        return scores.Count > 0 ? scores : null;
    }

    // ── IMemoryOperationHook (REST path via PluginPipeline) ──────────────────

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

    // ── IMemoryEventSink (MCP path) ─────────────────────────────────────────

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, content, store, ct);
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, [content], store, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        await RemoveVectorsAsync(qualifiedName, wasDeleted, store, ct);
    }

    // ── Shared internal ─────────────────────────────────────────────────────

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

            // Collect all texts to embed in one batch
            var items = new List<(string text, int? chunkIndex)> { (joined, null) };

            if (content.Length > 1)
            {
                for (int i = 0; i < content.Length; i++)
                    if (!string.IsNullOrWhiteSpace(content[i]))
                        items.Add((content[i], i + 1));
            }

            // Single batched embed call
            var vectors = await _provider.EmbedBatchAsync(
                items.Select(x => x.text).ToList(), ct);
            if (vectors is null) return;

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

    /// <summary>Re-embeds all existing memories in a store.</summary>
    private async Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
    {
        var allItems = store.ListScoped();
        int count = 0;

        var batch = new List<(ScopedArtifact item, string qualifiedName, string text)>();

        foreach (var item in allItems)
        {
            try
            {
                string qualifiedName = item.Scope == "ephemeral"
                    ? $"~{item.Entry.Name}"
                    : store.FormatQualifiedName(item.Scope, item.Entry.Name);

                string artifact = await store.ResolveArtifactAsync(qualifiedName, ct);
                string decoded = System.Text.Encoding.UTF8.GetString(
                    new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact));

                batch.Add((item, qualifiedName, decoded));

                if (batch.Count >= _options.MaxBatchSize)
                {
                    count += await FlushReindexBatchAsync(batch, ct);
                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reindex '{Name}'", item.Entry.Name);
            }
        }

        if (batch.Count > 0)
            count += await FlushReindexBatchAsync(batch, ct);

        return count;
    }

    private async Task<int> FlushReindexBatchAsync(
        List<(ScopedArtifact item, string qualifiedName, string text)> batch,
        CancellationToken ct)
    {
        var texts = batch.Select(b => b.text).ToList();
        var vectors = await _provider.EmbedBatchAsync(texts, ct);
        if (vectors is null) return 0;

        int count = 0;
        for (int i = 0; i < vectors.Length; i++)
        {
            await _vectorStore!.UpsertAsync(
                batch[i].item.Scope, batch[i].item.Entry.Name, null, vectors[i], ct);
            count++;
        }
        return count;
    }
}
