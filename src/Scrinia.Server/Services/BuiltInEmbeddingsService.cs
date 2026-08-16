using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Server.Services;

/// <summary>
/// Built-in Model2Vec embeddings for the server. Implements the same three interfaces
/// as the Vulkan plugin so it can be cleanly overridden when the plugin is installed.
///
/// <para>Write/reindex paths delegate to <see cref="MemoryVectorIndexer"/> — the single
/// embed-and-index policy — and the read-side projection delegates to
/// <see cref="VectorScoreProjection"/>.</para>
/// </summary>
public sealed class BuiltInEmbeddingsService :
    ISearchScoreContributor, IMemoryEventSink, IMemoryOperationHook, IEmbeddingsAdmin
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _vectorStore;
    private readonly double _semanticWeight;
    private readonly ILogger _logger;
    private readonly MemoryVectorIndexer _indexer;

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
        _indexer = new MemoryVectorIndexer(provider, vectorStore, logger, options);
    }

    public bool IsAvailable => _provider.IsAvailable;
    public string ProviderName => _provider.GetType().Name;
    public int Dimensions => _provider.Dimensions;
    public double SemanticWeight => _semanticWeight;
    public int TotalVectorCount => _vectorStore.Count();
    public string Hardware => "CPU";

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
            VectorScoreProjection.Project(
                group.Key, topK.Select(t => (t.Entry, (double)t.Similarity)), _semanticWeight, scores);
        }

        return scores.Count > 0 ? scores : null;
    }

    // ── IMemoryEventSink (MCP path) ─────────────────────────────────────────

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        => await EmbedAndIndexAsync(qualifiedName, content, store, ct);

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
        => await ReembedFullMemoryAsync(qualifiedName, store, ct);

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        if (!wasDeleted) return;
        // (qualifiedName, store) overload owns the parse inside its own try.
        await _indexer.RemoveAsync(qualifiedName, store, ct);
    }

    // ── IMemoryOperationHook (REST path) ────────────────────────────────────

    public async Task OnAfterStoreAsync(AfterStoreContext ctx, CancellationToken ct)
        => await EmbedAndIndexAsync(ctx.QualifiedName, ctx.Content, ctx.Store, ct);

    public async Task OnAfterAppendAsync(AfterAppendContext ctx, CancellationToken ct)
        => await ReembedFullMemoryAsync(ctx.Name, ctx.Store, ct);

    public async Task OnAfterForgetAsync(AfterForgetContext ctx, CancellationToken ct)
    {
        if (!ctx.WasDeleted) return;
        // (qualifiedName, store) overload owns the parse inside its own try — PluginPipeline
        // awaits this hook with no surrounding try/catch on the REST path, so a throw here
        // would otherwise propagate into the forget request itself.
        await _indexer.RemoveAsync(ctx.Name, ctx.Store, ct);
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

                // Reconstruct the memory's content array from the artifact rather than passing
                // one decoded blob. Reindex must reproduce the layout a write produces — Task 7's
                // signature bump rebuilds every existing vector through this exact path, so a
                // reindex that collapsed a memory to element 1 would mean the element contract
                // held only for memories written after this branch.
                var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
                if (elements.Length == 0) continue;

                // Bulk loop: retryOnUnavailable:false lets VectorStoreUnavailableException reach
                // the catch ladder below instead of being retried for up to 42s per memory and
                // then swallowed to false.
                if (await _indexer.IndexAsync(item.Scope, item.Entry.Name, elements, ct, retryOnUnavailable: false))
                    count++;
            }
            catch (VectorStoreUnavailableException ex)
            {
                // Not counted as reindexed, so a later run retries once the file is readable.
                _logger.LogWarning("Skipped reindexing '{Name}': {Reason}", item.Entry.Name, ex.Message);
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
        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            await _indexer.IndexAsync(scope, subject, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }

    /// <summary>
    /// An append mutates the memory. Indexing only the appended fragment would be wrong twice
    /// over: the indexer replaces a memory's vectors atomically, so the rest of the memory would
    /// lose its vectors entirely, and the surviving element indices would not match the memory's
    /// real content array. Re-read and index the whole thing, as the plugin already does
    /// (EmbeddingsPlugin.cs).
    /// </summary>
    private async Task ReembedFullMemoryAsync(string qualifiedName, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            string artifact = await store.ReadArtifactAsync(subject, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return;

            var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
            if (elements.Length == 0) return;

            await _indexer.IndexAsync(scope, subject, elements, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-embed appended memory '{Name}'", qualifiedName);
        }
    }
}
