using Microsoft.Extensions.Logging;
using Scrinia.Core.Encoding;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Walks every persistent memory in a <see cref="IMemoryStore"/>, reconstructs each one's
/// content array from its stored artifact, and routes it through <see cref="MemoryVectorIndexer"/>
/// — the same embed-and-index policy a regular write uses. Called after a vector-file quarantine
/// (provider or chunk-config changed), after a <c>scri config Scrinia:Embeddings:*</c> write, or
/// explicitly via <c>scri reindex</c>.
///
/// <para>Ephemeral scope is skipped — those vectors are by definition session-bound and
/// can't be reproduced from sidecars. Per-memory failures are logged and the batch continues
/// so a single bad artifact doesn't abort the whole pass. Each memory is treated atomically —
/// see <see cref="MemoryVectorIndexer.IndexAsync"/> — so a shrink-replace (memory edited from
/// 8 chunks down to 5) leaves no orphans.</para>
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

        var items = store.ListScoped()
            .Where(i => !i.Scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            .ToList();
        int total = items.Count;
        int embedded = 0, skipped = 0, failed = 0;

        var indexer = new MemoryVectorIndexer(provider, vectorStore, logger, options);

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            try
            {
                // Resolve directly by (scope, subject) rather than round-tripping through
                // FormatQualifiedName -> ResolveArtifactAsync -> ParseQualifiedName: for a v1
                // namespaced topic scope (e.g. "local:memory/api"), FormatQualifiedName
                // intentionally strips the "memory/" prefix for display, so re-parsing that
                // string reconstructs the WRONG scope ("local:api") and the artifact can
                // never be found. item.Scope is already the correct scope.
                string artifact = await store.ReadArtifactAsync(item.Entry.Name, item.Scope, ct);
                if (string.IsNullOrWhiteSpace(artifact))
                {
                    skipped++;
                    continue;
                }

                // Reconstruct the memory's content array from the artifact rather than passing
                // one decoded blob. Reindex must reproduce the layout a write produces — Task 7's
                // signature bump rebuilds every existing vector through this exact path, so a
                // reindex that collapsed a memory to element 1 would mean the element contract
                // held only for memories written after this branch.
                var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
                if (elements.Length == 0)
                {
                    skipped++;
                    continue;
                }

                string joined = string.Concat(elements);
                if (string.IsNullOrWhiteSpace(joined))
                {
                    skipped++;
                    continue;
                }

                // Bulk loop: retryOnUnavailable:false lets VectorStoreUnavailableException
                // propagate to the catch ladder below instead of being retried for up to 42s
                // per memory and then swallowed to false, which would stall the whole run and
                // mis-count "vectors unavailable" as skipped rather than failed.
                if (await indexer.IndexAsync(item.Scope, item.Entry.Name, elements, ct, retryOnUnavailable: false))
                    embedded++;
                else
                    skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (VectorStoreUnavailableException ex)
            {
                // Failed rather than skipped: unlike a missing artifact this is recoverable.
                failed++;
                logger.LogWarning("Reindex: vectors unavailable for {Name} — {Reason}", item.Entry.Name, ex.Message);
            }
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
    /// Unconditional full reindex of every persistent memory through the active provider.
    /// Used by the <c>scri reindex</c> command for the case where the user wants to force a
    /// rebuild even when signatures match (e.g. recovering from a suspected corruption).
    /// Unlike <see cref="ReindexIfStaleAsync"/>, this never gates on quarantine state — it
    /// constructs a fresh signed <see cref="VectorStore"/> and runs <see cref="ReindexAsync"/>
    /// against it directly. Existing vectors are atomically replaced per memory via the
    /// Remove-then-Upsert sequence inside <see cref="ReindexAsync"/>.
    /// </summary>
    public static async Task<Result> ForceReindexAsync(
        IMemoryStore store,
        IEmbeddingProvider provider,
        string embeddingsDir,
        ILogger logger,
        Action<int, int>? progress,
        CancellationToken ct,
        EmbeddingOptions? options = null)
    {
        options ??= new EmbeddingOptions();
        string expectedSignature = ChunkedSignature.Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap);
        var vectorStore = new VectorStore(embeddingsDir, expectedSignature);
        return await ReindexAsync(store, provider, vectorStore, logger, progress, ct, options);
    }

    /// <summary>
    /// Sink-driven reindex for the plugin-owned embeddings case. When a plugin (e.g. Vulkan)
    /// owns the embedding pipeline out-of-process, the host has no in-process
    /// <see cref="IEmbeddingProvider"/> to batch-embed against — but it does have an
    /// <see cref="IMemoryEventSink"/> that the plugin subscribed to via MCP. Replaying every
    /// persistent memory through <see cref="IMemoryEventSink.OnStoredAsync"/> drives the
    /// plugin's <c>index</c> tool when the plugin offers it (or its legacy <c>upsert</c> tool
    /// otherwise — see <c>McpPluginHost.OnStoredAsync</c>'s negotiation) the same way a fresh
    /// write would, producing a clean rebuild without needing to expose the plugin's internal
    /// embed/store internals.
    ///
    /// <para>Pass the active <see cref="MemoryEventSinkContext.Default"/> (or a specific sink
    /// for tests). Each memory's content array is reconstructed from its artifact via
    /// <see cref="Nmp2ChunkedEncoder.DecodeAllChunksVerified"/> and handed to the sink whole —
    /// the same content array a regular write carries, not a single decoded blob.</para>
    /// </summary>
    public static async Task<Result> ReindexViaSinkAsync(
        IMemoryStore store,
        IMemoryEventSink sink,
        ILogger logger,
        Action<int, int>? progress,
        CancellationToken ct)
    {
        var items = store.ListScoped()
            .Where(i => !i.Scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            .ToList();
        int total = items.Count;
        int embedded = 0, skipped = 0, failed = 0;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            try
            {
                // See the matching comment in ReindexAsync above: resolve by (scope, subject)
                // directly rather than through the lossy FormatQualifiedName round trip.
                string artifact = await store.ReadArtifactAsync(item.Entry.Name, item.Scope, ct);
                if (string.IsNullOrWhiteSpace(artifact))
                {
                    skipped++;
                    continue;
                }

                // Reconstruct the memory's content array — a regular write carries the real
                // content array, so replaying with a single decoded element would produce a
                // different layout than the write this is supposed to reproduce.
                var elements = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks;
                if (elements.Length == 0)
                {
                    skipped++;
                    continue;
                }

                // The sink re-parses this string (possibly out-of-process, over MCP, for external
                // plugins) to decide which scope to store vectors under. Under v3
                // FormatQualifiedName is round-trip-safe for every scope, so this is safe.
                string qualifiedName = store.FormatQualifiedName(item.Scope, item.Entry.Name);
                await sink.OnStoredAsync(qualifiedName, elements, store, ct);
                embedded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException)
            {
                skipped++;
                logger.LogDebug("Reindex (sink): artifact missing for {Name}", item.Entry.Name);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Reindex (sink): failed for {Name}", item.Entry.Name);
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
