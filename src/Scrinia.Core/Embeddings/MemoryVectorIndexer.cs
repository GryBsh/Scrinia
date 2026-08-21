using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The single embed-and-index policy. Owns availability check, windowing, the per-memory chunk
/// cap, batching, the vector-count guard, the atomic-per-memory batch replace, and (for detached
/// callers) write retry.
///
/// <para><b>Retry is opt-in per call, not unconditional.</b> <see cref="IndexAsync"/> defaults
/// <c>retryOnUnavailable</c> to <c>true</c> for event-sink/write paths, which run detached and
/// can afford <see cref="VectorWriteRetry"/>'s up-to-42s backoff. Bulk callers — reindex loops —
/// pass <c>false</c>: <see cref="VectorWriteRetry"/>'s own doc comment says it is "not for bulk
/// reindex loops, where a long wait per item would stall the whole run," and its
/// <c>RunAsync</c> also catches <see cref="VectorStoreUnavailableException"/> and returns
/// <c>false</c> — which would make an unreadable vector file indistinguishable from "nothing to
/// embed" to a caller like <see cref="EmbeddingReindexer.ReindexAsync"/> whose
/// <c>catch (VectorStoreUnavailableException)</c> ladder depends on seeing the exception to
/// count the memory as failed rather than skipped. With <c>retryOnUnavailable: false</c> the
/// batch replace runs once, unwrapped, and lets that exception propagate to the caller's own
/// catch ladder.</para>
///
/// <para><b>The chunk model.</b> A memory's content array is its addressable unit: element
/// index, 1-based, the same unit <c>ChunkEntry.ChunkIndex</c> and the REST contract use. Each
/// element is sliced into overlapping windows by <see cref="TextChunker"/>; every window is
/// embedded and stored. Windows are an implementation detail — they never appear in a
/// supplemental score key, which is what <see cref="VectorScoreProjection"/> guarantees.</para>
///
/// <para><b>Why the stored chunk index is two-level.</b>
/// <see cref="VectorStore.ReplaceMemoryVectorsAsync"/> (like <see cref="VectorStore.UpsertAsync"/>)
/// de-dupes on <c>(name, chunkIndex)</c>, last write wins, so two windows of the same element
/// sharing an index would silently collapse to whichever was written last. The stored index is
/// therefore
/// <c>elementIndex * <see cref="ChunkIndexStride"/> + windowOrdinal</c>, and
/// <see cref="ElementIndexOf"/> recovers the element by integer division.
/// <see cref="EmbeddingOptions.MaxChunksPerMemory"/> caps total windows per memory well below
/// the stride, which the constructor asserts. The alternative — a second field on
/// <see cref="Models.VectorEntry"/> — would change the SVF3 binary format, its op-log replay
/// and its compaction, which is a larger change than this branch should carry.</para>
///
/// <para>Replaces six divergent copies (finding H3): EmbeddingReindexer.ReindexAsync,
/// CoreEmbeddingEventHandler, the reindex paths, and
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
    ///
    /// <para><paramref name="retryOnUnavailable"/> defaults to <c>true</c> (detached event-sink
    /// callers). Bulk reindex loops must pass <c>false</c> so a <see cref="VectorStoreUnavailableException"/>
    /// propagates to their own catch ladder instead of being retried for up to 42s and then
    /// swallowed to <c>false</c> — see the class doc comment.</para>
    /// </summary>
    public async Task<bool> IndexAsync(
        string scope, string subject, string[] content, CancellationToken ct,
        bool retryOnUnavailable = true)
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

        // The guard must return BEFORE the batch replace below. A provider returning the wrong
        // count would otherwise wipe this memory's vectors and write nothing back, leaving it
        // unsearchable with only a log line.
        if (vectors is null || vectors.Length != plan.Count)
        {
            if (vectors is not null)
                _logger.LogWarning(
                    "Embed returned {Got} vectors for {Want} inputs for '{Scope}/{Name}'; "
                    + "skipping to preserve existing vectors.",
                    vectors.Length, plan.Count, scope, subject);
            return false;
        }

        // One locked file operation for the whole memory, not one per vector — the previous
        // RemoveAsync-then-loop took 1 + K locks and up to P + 2K appends. The in-memory replace
        // is now genuinely atomic (one reference swap under the scope lock; a concurrent
        // GetVectors can no longer observe the old-removed/new-not-yet-added gap the loop could
        // expose). The on-disk append is narrower but not atomic — a crash mid-batch can still
        // leave this memory half-replaced on disk; if that happens, the next index pass rebuilds
        // it cleanly.
        //
        // A RETRY after a partial flush is a different failure and does not recover that way:
        // FileRetry.RunAsync re-runs the whole write delegate, which reopens in FileMode.Append,
        // so the retry's ops land after any torn fragment the first attempt left — and the op-log
        // reader tolerates a torn record only at the tail, not in the middle. See
        // VectorStore.ReplaceMemoryVectorsAsync's remarks; called out there, not fixed here.
        Func<Task> write = async () =>
        {
            var batch = new (int? ChunkIndex, float[] Vector)[plan.Count];
            for (int i = 0; i < plan.Count; i++)
                batch[i] = (plan[i].ChunkIndex, vectors[i]);
            await _store.ReplaceMemoryVectorsAsync(scope, subject, batch, ct);
        };

        if (retryOnUnavailable)
            return await VectorWriteRetry.RunAsync(write, _logger, $"{scope}/{subject}", ct);

        // Bulk callers get the exception, not a retried-then-swallowed false — see the class and
        // parameter doc comments.
        await write();
        return true;
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

    /// <summary>
    /// Parses <paramref name="qualifiedName"/> and drops every vector held for the resulting
    /// (scope, subject). Owns the parse inside its own try so a throw from
    /// <see cref="IPluginStore.ParseQualifiedName"/> cannot escape into a caller with no guard
    /// of its own — e.g. <c>PluginPipeline.ForgetAsync</c> awaits
    /// <c>IMemoryOperationHook.OnAfterForgetAsync</c> on the REST path with no surrounding
    /// try/catch, so a throw there would propagate into the forget request itself.
    /// </summary>
    public async Task RemoveAsync(string qualifiedName, IPluginStore store, CancellationToken ct)
    {
        try
        {
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            await _store.RemoveAsync(scope, subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove vectors for '{Name}'", qualifiedName);
        }
    }
}
