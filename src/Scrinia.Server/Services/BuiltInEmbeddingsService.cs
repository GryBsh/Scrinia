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
    private readonly EmbeddingOptions _options;

    public BuiltInEmbeddingsService(
        IEmbeddingProvider provider,
        VectorStore vectorStore,
        double semanticWeight,
        ILogger<BuiltInEmbeddingsService> logger,
        EmbeddingOptions? options = null)
    {
        _provider = provider;
        _vectorStore = vectorStore;
        _semanticWeight = semanticWeight;
        _logger = logger;
        _options = options ?? new EmbeddingOptions();
    }

    public bool IsAvailable => _provider.IsAvailable;
    public string ProviderName => _provider.GetType().Name;
    public int Dimensions => _provider.Dimensions;
    public double SemanticWeight => _semanticWeight;
    public int TotalVectorCount => _vectorStore.Count();

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
                // Dedupe-by-memory for chunked embeddings: max-aggregate per-chunk scores
                // under the {scope}|{name} key the whole-memory scorer pass looks up. Keep
                // the chunked key too so any future per-chunk scoring path can use it.
                double score = similarity * _semanticWeight;
                string entryKey = $"{group.Key}|{entry.Name}";
                if (!scores.TryGetValue(entryKey, out double existing) || score > existing)
                    scores[entryKey] = score;

                if (entry.ChunkIndex is not null)
                    scores[$"{group.Key}|{entry.Name}|{entry.ChunkIndex}"] = score;
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

                var chunks = TextChunker.SliceWindows(decoded, _options.ChunkSize, _options.ChunkOverlap);
                if (chunks.Count == 0) continue;
                if (chunks.Count > _options.MaxChunksPerMemory)
                    chunks = [.. chunks.Take(_options.MaxChunksPerMemory)];

                var vectors = await _provider.EmbedBatchAsync(chunks.Select(c => c.Text).ToList(), ct);
                if (vectors is null || vectors.Length != chunks.Count) continue;

                await _vectorStore.RemoveAsync(item.Scope, item.Entry.Name, ct);
                for (int i = 0; i < chunks.Count; i++)
                    await _vectorStore.UpsertAsync(item.Scope, item.Entry.Name, chunks[i].Index, vectors[i], ct);
                count++;
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

            var chunks = TextChunker.SliceWindows(joined, _options.ChunkSize, _options.ChunkOverlap);
            if (chunks.Count == 0) return;
            if (chunks.Count > _options.MaxChunksPerMemory)
                chunks = [.. chunks.Take(_options.MaxChunksPerMemory)];

            var vectors = await _provider.EmbedBatchAsync(chunks.Select(c => c.Text).ToList(), ct);
            if (vectors is null || vectors.Length != chunks.Count) return;

            await _vectorStore.RemoveAsync(scope, subject, ct);
            for (int i = 0; i < chunks.Count; i++)
                await _vectorStore.UpsertAsync(scope, subject, chunks[i].Index, vectors[i], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }
}
