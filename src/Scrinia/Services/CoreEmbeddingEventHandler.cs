using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Embeddings;

namespace Scrinia.Services;

/// <summary>
/// In-process <see cref="IMemoryEventSink"/> that embeds content and upserts vectors
/// using the built-in embedding provider. Used when no external plugin overrides it.
///
/// <para>Slices each memory's joined content via <see cref="TextChunker.SliceWindows"/>
/// so long memories produce multiple vectors (one per overlapping window), keeping every
/// part of the memory reachable via semantic search. Search-time dedup in
/// <c>WeightedFieldScorer.SearchAll</c> collapses chunk matches back to one result per
/// memory.</para>
/// </summary>
internal sealed class CoreEmbeddingEventHandler : IMemoryEventSink
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _store;
    private readonly ILogger _logger;
    private readonly EmbeddingOptions _options;

    public CoreEmbeddingEventHandler(IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
    {
        _provider = provider;
        _store = store;
        _logger = logger;
        _options = options ?? new EmbeddingOptions();
    }

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, content, memoryStore, ct);
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore memoryStore, CancellationToken ct)
    {
        await EmbedAndIndexAsync(qualifiedName, [content], memoryStore, ct);
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore memoryStore, CancellationToken ct)
    {
        if (!wasDeleted) return;

        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);
            await _store.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }

    private async Task EmbedAndIndexAsync(string qualifiedName, string[] content, IMemoryStore memoryStore, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return;

        try
        {
            var (scope, subject) = memoryStore.ParseQualifiedName(qualifiedName);

            string joined = string.Concat(content);
            if (string.IsNullOrWhiteSpace(joined))
                return;

            var chunks = TextChunker.SliceWindows(joined, _options.ChunkSize, _options.ChunkOverlap);
            if (chunks.Count == 0) return;

            if (chunks.Count > _options.MaxChunksPerMemory)
            {
                _logger.LogWarning(
                    "Embedding {Name}: {Count} chunks exceeds cap {Cap}; tail dropped from embed (BM25 still covers it).",
                    qualifiedName, chunks.Count, _options.MaxChunksPerMemory);
                chunks = [.. chunks.Take(_options.MaxChunksPerMemory)];
            }

            var texts = chunks.Select(c => c.Text).ToList();
            var vectors = await _provider.EmbedBatchAsync(texts, ct);
            if (vectors is null || vectors.Length != chunks.Count) return;

            // Atomic per-memory replace: drop any prior vectors (including stale higher
            // chunk indices from when this memory was longer) before writing the new set.
            await _store.RemoveAsync(scope, subject, ct);
            for (int i = 0; i < chunks.Count; i++)
                await _store.UpsertAsync(scope, subject, chunks[i].Index, vectors[i], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory '{Name}'", qualifiedName);
        }
    }
}
