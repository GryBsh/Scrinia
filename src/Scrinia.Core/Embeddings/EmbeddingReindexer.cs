using Microsoft.Extensions.Logging;
using Scrinia.Core.Encoding;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Walks every persistent memory in a <see cref="IMemoryStore"/>, re-embeds the decoded
/// content via the active <see cref="IEmbeddingProvider"/>, and writes vectors into the
/// target <see cref="VectorStore"/>. Called after a vector-file quarantine (provider
/// changed), after a <c>scri config Scrinia:Embeddings:*</c> write, or explicitly via
/// <c>scri reindex</c>.
///
/// <para>Ephemeral scope is skipped — those vectors are by definition session-bound and
/// can't be reproduced from sidecars. Failures on individual items are logged and the
/// batch continues so a single bad artifact doesn't abort the whole pass.</para>
/// </summary>
public static class EmbeddingReindexer
{
    /// <summary>
    /// Default character cap on text sent to an embedding provider. ~6000 chars maps to
    /// roughly 1500 tokens — comfortably inside the 2048-token default context of Ollama's
    /// nomic-embed-text and larger windows of every other supported provider. Long memories
    /// get their prefix embedded; the BM25 path still indexes the full text. The limit can
    /// be overridden via <c>Scrinia:Embeddings:MaxInputChars</c> for users on models with
    /// bigger context budgets (mxbai-embed-large at 512, text-embedding-3-large at 8192, etc.).
    /// </summary>
    public const int DefaultMaxInputChars = 6000;

    public sealed record Result(int Total, int Embedded, int Skipped, int Failed);

    /// <summary>
    /// Reindex every persistent artifact through <paramref name="provider"/> into
    /// <paramref name="vectorStore"/>. Progress callback fires per item with
    /// <c>(done, total)</c>; pass null when you don't need progress reporting.
    /// </summary>
    public static async Task<Result> ReindexAsync(
        IMemoryStore store,
        IEmbeddingProvider provider,
        VectorStore vectorStore,
        ILogger logger,
        Action<int, int>? progress,
        CancellationToken ct,
        int maxInputChars = DefaultMaxInputChars)
    {
        if (!provider.IsAvailable)
        {
            logger.LogWarning("Reindex skipped: embedding provider is not available.");
            return new Result(0, 0, 0, 0);
        }

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

                // Truncate before embedding to keep within the provider's context. nomic-embed-text
                // and the other small models cap around 2048 tokens; we send ~1500-token prefix
                // which captures the high-signal head of session logs / docs. BM25 still indexes
                // the full content for keyword recall on terms past the cutoff.
                string toEmbed = decoded.Length > maxInputChars ? decoded[..maxInputChars] : decoded;

                var vec = await provider.EmbedAsync(toEmbed, ct);
                if (vec is null)
                {
                    failed++;
                    logger.LogDebug("Reindex: empty embedding for {Name}", item.Entry.Name);
                }
                else
                {
                    await vectorStore.UpsertAsync(item.Scope, item.Entry.Name, null, vec, ct);
                    embedded++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException)
            {
                // Sidecar exists but the .nmp2 artifact is missing on disk (manual deletion,
                // merge artifact, etc.). Count as skipped rather than failed so the result
                // summary reflects "nothing we could do" vs "provider error."
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
        int maxInputChars = DefaultMaxInputChars)
    {
        if (!Directory.Exists(embeddingsDir))
            return null;

        // Force a header read on every scope by enumerating subdirectories. The
        // signature-mismatch logic in VectorStore.LoadFromDisk handles the quarantine
        // rename and records the scope in HasStaleQuarantines.
        var probeStore = new VectorStore(embeddingsDir, provider.Signature);
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
            "Reindexing {Count} scope(s) after embedding model change: {Scopes}",
            probeStore.StaleQuarantineScopes.Count,
            string.Join(", ", probeStore.StaleQuarantineScopes));

        return await ReindexAsync(store, provider, probeStore, logger, progress, ct, maxInputChars);
    }
}
