using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single embed-and-index policy. Owns availability check, windowing, the per-memory chunk
/// cap, batching, the vector-count guard, remove-then-upsert ordering, and write retry.
///
/// <para><b>The chunk model.</b> A memory's content array is its addressable unit: element
/// index, 1-based, the same unit <c>ChunkEntry.ChunkIndex</c> and the REST contract use. Each
/// element is sliced into overlapping windows by <see cref="TextChunker"/>; every window is
/// embedded and stored. Windows are an implementation detail — they never appear in a
/// supplemental score key, which is what <see cref="VectorScoreProjection"/> guarantees.</para>
///
/// <para><b>Why the stored chunk index is two-level.</b>
/// <see cref="VectorStore.UpsertAsync"/> deduplicates on <c>(name, chunkIndex)</c>, so two
/// windows of the same element sharing an index would silently collapse to whichever was
/// written last. The stored index is therefore
/// <c>elementIndex * <see cref="ChunkIndexStride"/> + windowOrdinal</c>, and
/// <see cref="ElementIndexOf"/> recovers the element by integer division.
/// <see cref="EmbeddingOptions.MaxChunksPerMemory"/> caps total windows per memory well below
/// the stride, which the constructor asserts. The alternative — a second field on
/// <see cref="Models.VectorEntry"/> — would change the SVF3 binary format, its op-log replay
/// and its compaction, which is a larger change than this branch should carry.</para>
///
/// <para>Replaces six divergent copies (finding H3): EmbeddingReindexer.ReindexAsync,
/// CoreEmbeddingEventHandler, BuiltInEmbeddingsService's write and reindex paths, and
/// EmbeddingsPlugin's write and reindex paths — the last two of which never windowed at all.</para>
/// </summary>
public sealed class MemoryVectorIndexer
{
    /// <summary>Multiplier separating the element index from the window ordinal in a stored chunk index.</summary>
    public const int ChunkIndexStride = 1000;

    private readonly IEmbeddingProvider _provider;
    private readonly VectorStore _store;
    private readonly ILogger _logger;
    private readonly EmbeddingOptions _options;

    public MemoryVectorIndexer(
        IEmbeddingProvider provider, VectorStore store, ILogger logger, EmbeddingOptions? options = null)
    {
        _provider = provider;
        _store = store;
        _logger = logger;
        _options = options ?? new EmbeddingOptions();

        if (_options.MaxChunksPerMemory >= ChunkIndexStride)
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MaxChunksPerMemory,
                $"MaxChunksPerMemory must be below ChunkIndexStride ({ChunkIndexStride}); "
                + "otherwise one element's windows could overflow into the next element's index space.");
    }

    /// <summary>Recovers the 1-based content-element index from a stored chunk index.</summary>
    public static int ElementIndexOf(int storedChunkIndex) => storedChunkIndex / ChunkIndexStride;

    /// <summary>
    /// Embeds <paramref name="content"/> and replaces every vector held for
    /// <paramref name="subject"/> in <paramref name="scope"/>. Returns false when nothing was
    /// written — provider unavailable, no embeddable text, or the count guard tripped.
    /// </summary>
    public async Task<bool> IndexAsync(
        string scope, string subject, string[] content, CancellationToken ct)
    {
        if (!_provider.IsAvailable) return false;
        if (content.Length == 0) return false;

        // (storedChunkIndex, text), in element order.
        var plan = new List<(int ChunkIndex, string Text)>();
        for (int i = 0; i < content.Length; i++)
        {
            string element = content[i];
            if (string.IsNullOrWhiteSpace(element)) continue;

            // Element index is 1-based and never renumbers around a blank element: it is the
            // memory's own addressing, and ChunkEntry numbers the same way.
            int elementIndex = i + 1;
            var windows = TextChunker.SliceWindows(element, _options.ChunkSize, _options.ChunkOverlap);
            foreach (var (ordinal, text) in windows)
                plan.Add((elementIndex * ChunkIndexStride + ordinal, text));
        }

        if (plan.Count == 0) return false;

        if (plan.Count > _options.MaxChunksPerMemory)
        {
            _logger.LogWarning(
                "Indexing {Scope}/{Name}: {Count} chunks exceeds cap {Cap}; tail dropped from embed "
                + "(BM25 still covers it).",
                scope, subject, plan.Count, _options.MaxChunksPerMemory);
            plan = [.. plan.Take(_options.MaxChunksPerMemory)];
        }

        var vectors = await _provider.EmbedBatchAsync([.. plan.Select(p => p.Text)], ct);

        // The guard must return BEFORE RemoveAsync. A provider returning the wrong count would
        // otherwise wipe this memory's vectors and write nothing back, leaving it unsearchable
        // with only a log line.
        if (vectors is null || vectors.Length != plan.Count)
        {
            if (vectors is not null)
                _logger.LogWarning(
                    "Embed returned {Got} vectors for {Want} inputs for '{Scope}/{Name}'; "
                    + "skipping to preserve existing vectors.",
                    vectors.Length, plan.Count, scope, subject);
            return false;
        }

        // Atomic per-memory replace: clearing first is what stops a shrunk memory keeping stale
        // higher chunk indices. If the upsert loop is interrupted the next index pass rebuilds
        // this memory cleanly.
        return await VectorWriteRetry.RunAsync(
            async () =>
            {
                await _store.RemoveAsync(scope, subject, ct);
                for (int i = 0; i < plan.Count; i++)
                    await _store.UpsertAsync(scope, subject, plan[i].ChunkIndex, vectors[i], ct);
            },
            _logger, $"{scope}/{subject}", ct);
    }

    /// <summary>Drops every vector held for <paramref name="subject"/> in <paramref name="scope"/>.</summary>
    public async Task RemoveAsync(string scope, string subject, CancellationToken ct)
    {
        try
        {
            await _store.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Scope}/{Name}'", scope, subject);
        }
    }
}
