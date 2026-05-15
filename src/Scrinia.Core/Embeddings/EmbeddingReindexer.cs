using Microsoft.Extensions.Logging;
using Scrinia.Core.Encoding;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Walks every persistent memory in a <see cref="IMemoryStore"/>, slices each one's decoded
/// content into overlapping windows via <see cref="TextChunker.SliceWindows"/>, embeds the
/// windows in a single batch call, and upserts one vector per chunk into the target
/// <see cref="VectorStore"/>. Called after a vector-file quarantine (provider or chunk-config
/// changed), after a <c>scri config Scrinia:Embeddings:*</c> write, or explicitly via
/// <c>scri reindex</c>.
///
/// <para>Ephemeral scope is skipped — those vectors are by definition session-bound and
/// can't be reproduced from sidecars. Per-memory failures are logged and the batch continues
/// so a single bad artifact doesn't abort the whole pass. Each memory is treated atomically:
/// existing vectors for the memory are removed before the new chunks are upserted, so a
/// shrink-replace (memory edited from 8 chunks down to 5) leaves no orphans.</para>
/// </summary>
public static class EmbeddingReindexer
{
    public sealed record Result(int Total, int Embedded, int Skipped, int Failed);

    /// <summary>
    /// Reindex every persistent artifact through <paramref name="provider"/> into
    /// <paramref name="vectorStore"/>. Progress callback fires per memory with
    /// <c>(done, total)</c>; pass null when you don't need progress reporting.
    /// </summary>
    public static async Task<Result> ReindexAsync(
        IMemoryStore store,
        IEmbeddingProvider provider,
        VectorStore vectorStore,
        ILogger logger,
        Action<int, int>? progress,
        CancellationToken ct,
        EmbeddingOptions? options = null)
    {
        if (!provider.IsAvailable)
        {
            logger.LogWarning("Reindex skipped: embedding provider is not available.");
            return new Result(0, 0, 0, 0);
        }

        options ??= new EmbeddingOptions();
        int windowSize = options.ChunkSize;
        int overlap = options.ChunkOverlap;
        int maxChunks = options.MaxChunksPerMemory;

        var items = store.ListScoped()
            .Where(i => !i.Scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            .ToList();
        int total = items.Count;
        int embedded = 0, skipped = 0, failed = 0;

        var decoder = new Nmp2Strategy();

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            try
            {
                string qualifiedName = store.FormatQualifiedName(item.Scope, item.Entry.Name);
                string artifact = await store.ResolveArtifactAsync(qualifiedName, ct);
                if (string.IsNullOrWhiteSpace(artifact))
                {
                    skipped++;
                    continue;
                }

                string decoded = System.Text.Encoding.UTF8.GetString(decoder.Decode(artifact));
                if (string.IsNullOrWhiteSpace(decoded))
                {
                    skipped++;
                    continue;
                }

                var chunks = TextChunker.SliceWindows(decoded, windowSize, overlap);
                if (chunks.Count == 0)
                {
                    skipped++;
                    continue;
                }

                if (chunks.Count > maxChunks)
                {
                    logger.LogWarning(
                        "Reindex: {Name} would produce {Count} chunks; capping at {Cap}. " +
                        "Tail content embed-skipped (BM25 still indexes it).",
                        item.Entry.Name, chunks.Count, maxChunks);
                    chunks = [.. chunks.Take(maxChunks)];
                }

                var texts = chunks.Select(c => c.Text).ToList();
                var vectors = await provider.EmbedBatchAsync(texts, ct);
                if (vectors is null || vectors.Length != chunks.Count)
                {
                    failed++;
                    logger.LogDebug("Reindex: batch embed failed or returned mismatched count for {Name}", item.Entry.Name);
                    continue;
                }

                // Atomic per-memory replace: clear any prior vectors for this name (including
                // stale chunk indices from a previous, larger chunk count) before upserting
                // the new set. Order matters — if the upsert loop is interrupted mid-flight,
                // the next reindex will pick this memory back up cleanly.
                await vectorStore.RemoveAsync(item.Scope, item.Entry.Name, ct);
                for (int c = 0; c < chunks.Count; c++)
                    await vectorStore.UpsertAsync(item.Scope, item.Entry.Name, chunks[c].Index, vectors[c], ct);

                embedded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException)
            {
                // Sidecar exists but the .nmp2 artifact is missing on disk (manual deletion,
                // merge artifact, etc.). Skip rather than fail so the result summary reflects
                // "nothing we could do" vs "provider error."
                skipped++;
                logger.LogDebug("Reindex: artifact missing for {Name}", item.Entry.Name);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Reindex: failed to embed {Name}", item.Entry.Name);
            }

            progress?.Invoke(i + 1, total);
        }

        return new Result(total, embedded, skipped, failed);
    }

    /// <summary>
    /// Detect-and-reindex entry point used by <c>WorkspaceSetup</c> on startup and by the
    /// <c>scri config Scrinia:Embeddings:*</c> command after a settings write. Walks each
    /// scope directory under <paramref name="embeddingsDir"/> to force <c>LoadFromDisk</c>
    /// (which quarantines signature-mismatched files); if anything was quarantined, runs
    /// <see cref="ReindexAsync"/> against the same store. Returns the result when work
    /// happened, or <c>null</c> when everything was already in-sync.
    /// </summary>
    public static async Task<Result?> ReindexIfStaleAsync(
        IMemoryStore store,
        IEmbeddingProvider provider,
        string embeddingsDir,
        ILogger logger,
        Action<int, int>? progress,
        CancellationToken ct,
        EmbeddingOptions? options = null)
    {
        if (!Directory.Exists(embeddingsDir))
            return null;

        options ??= new EmbeddingOptions();
        string expectedSignature = ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap);

        // Force a header read on every scope by enumerating subdirectories. The
        // signature-mismatch logic in VectorStore.LoadFromDisk handles the quarantine
        // rename and records the scope in HasStaleQuarantines.
        var probeStore = new VectorStore(embeddingsDir, expectedSignature);
        foreach (string scopeDir in Directory.EnumerateDirectories(embeddingsDir))
        {
            string scope = Path.GetFileName(scopeDir);
            if (string.IsNullOrEmpty(scope)) continue;
            try { probeStore.GetVectors(scope); }
            catch (Exception ex) { logger.LogDebug(ex, "Probe failed for scope {Scope}", scope); }
        }

        if (!probeStore.HasStaleQuarantines)
            return null;

        logger.LogInformation(
            "Reindexing {Count} scope(s) after embedding config change: {Scopes}",
            probeStore.StaleQuarantineScopes.Count,
            string.Join(", ", probeStore.StaleQuarantineScopes));

        return await ReindexAsync(store, provider, probeStore, logger, progress, ct, options);
    }
}
