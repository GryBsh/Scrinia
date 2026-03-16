using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Server.Services;

/// <summary>
/// Built-in Model2Vec embeddings for the server. Implements the same three interfaces
/// as the Vulkan plugin so it can be cleanly overridden when the plugin is installed.
/// </summary>
public sealed class BuiltInEmbeddingsService :
    ISearchScoreContributor, IMemoryEventSink, IMemoryOperationHook
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _vectorStore;
    private readonly double _semanticWeight;
    private readonly ILogger _logger;

    public BuiltInEmbeddingsService(
        IEmbeddingProvider provider,
        VectorStore vectorStore,
        double semanticWeight,
        ILogger<BuiltInEmbeddingsService> logger)
    {
        _provider = provider;
        _vectorStore = vectorStore;
        _semanticWeight = semanticWeight;
        _logger = logger;
    }

    public bool IsAvailable => _provider.IsAvailable;
    public string ProviderName => _provider.GetType().Name;
    public int Dimensions => _provider.Dimensions;
    public double SemanticWeight => _semanticWeight;
    public int TotalVectorCount => _vectorStore.TotalVectorCount();

    // ── ISearchScoreContributor ──────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return null;

        var queryVec = await _provider.EmbedAsync(query, ct);
        if (queryVec is null) return null;

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

    // ── IMemoryEventSink (MCP path) ─────────────────────────────────────────

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        => await EmbedAndIndexAsync(qualifiedName, content, store, ct);

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
        => await EmbedAndIndexAsync(qualifiedName, [content], store, ct);

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        if (!wasDeleted) return;
        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            await _vectorStore.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }

    // ── IMemoryOperationHook (REST path) ────────────────────────────────────

    public async Task OnAfterStoreAsync(AfterStoreContext ctx, CancellationToken ct)
        => await EmbedAndIndexAsync(ctx.QualifiedName, ctx.Content, ctx.Store, ct);

    public async Task OnAfterAppendAsync(AfterAppendContext ctx, CancellationToken ct)
        => await EmbedAndIndexAsync(ctx.Name, [ctx.Content], ctx.Store, ct);

    public async Task OnAfterForgetAsync(AfterForgetContext ctx, CancellationToken ct)
    {
        if (!ctx.WasDeleted) return;
        try
        {
            var (scope, subject) = ctx.Store.ParseQualifiedName(ctx.Name);
            await _vectorStore.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Name}'", ctx.Name);
        }
    }

    // ── Reindex ─────────────────────────────────────────────────────────────

    public async Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
    {
        var allItems = store.ListScoped();
        int count = 0;

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

                var vec = await _provider.EmbedAsync(decoded, ct);
                if (vec is not null)
                {
                    await _vectorStore.UpsertAsync(item.Scope, item.Entry.Name, null, vec, ct);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reindex '{Name}'", item.Entry.Name);
            }
        }

        return count;
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private async Task EmbedAndIndexAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return;

        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);

            string joined = string.Concat(content);
            if (string.IsNullOrWhiteSpace(joined)) return;

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

            for (int i = 0; i < vectors.Length; i++)
                await _vectorStore.UpsertAsync(scope, subject, items[i].chunkIndex, vectors[i], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }
}
