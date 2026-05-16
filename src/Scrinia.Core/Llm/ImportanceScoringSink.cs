using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Core.Llm;

/// <summary>
/// Asynchronous event sink that scores each newly-stored memory's importance via the
/// Tier 2 LLM and rewrites the sidecar with the result. Mirrors the embedding-sink
/// pattern: fires after Upsert via <c>MemoryTools.Core.FireEventSinkAsync</c>, never
/// blocks the user-facing response, and degrades cleanly when no LLM is configured.
///
/// <para>The sink reads <see cref="BackgroundLlmContext.Current"/> lazily on each call
/// (not at construction) so it picks up a Tier 2 backend that's configured after sink
/// registration — important during workspace bootstrap where the order is sometimes
/// "register sinks, then load LLM".</para>
///
/// <para>If <c>ScoreImportanceAsync</c> returns null (unavailable, parse failure,
/// timeout), the sidecar is left untouched and the ranker falls back to the neutral
/// importance midpoint via <see cref="Search.RankerOptions.NeutralImportance"/>.</para>
/// </summary>
public sealed class ImportanceScoringSink : IMemoryEventSink
{
    private readonly Func<IBackgroundLlm?> _llmAccessor;

    /// <summary>
    /// Default constructor reads from <see cref="BackgroundLlmContext.Current"/>.
    /// Production wires this; tests use the alternate constructor to inject a fake LLM.
    /// </summary>
    public ImportanceScoringSink() : this(() => BackgroundLlmContext.Current) { }

    /// <summary>Test-friendly constructor — caller supplies the LLM resolver directly.</summary>
    public ImportanceScoringSink(Func<IBackgroundLlm?> llmAccessor)
    {
        _llmAccessor = llmAccessor;
    }

    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        => await ScoreAndPersistAsync(qualifiedName, content, store, ct);

    /// <summary>
    /// On append, rescore: the memory's payload has changed materially. The ranker
    /// only ever sees the latest stored value, so re-running the LLM is the only way
    /// the score stays representative of current content.
    /// </summary>
    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
        => await ScoreAndPersistAsync(qualifiedName, [content], store, ct);

    /// <summary>Forgotten memories don't need rescoring; their sidecar is gone.</summary>
    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        => Task.CompletedTask;

    private async Task ScoreAndPersistAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        var llm = _llmAccessor();
        if (llm is null) return;

        try
        {
            string joined = string.Concat(content);
            if (string.IsNullOrWhiteSpace(joined)) return;

            int? score = await llm.ScoreImportanceAsync(joined, ct);
            if (score is null) return;

            // Re-load the entry from the store to get the freshest copy — the sink runs
            // on a background Task, so by the time we get here the user could have written
            // the same memory again with different metadata. We only update Importance;
            // every other field stays as-stored.
            var (scope, subject) = store.ParseQualifiedName(qualifiedName);
            var entries = store.LoadIndex(scope);
            ArtifactEntry? existing = null;
            foreach (var e in entries)
            {
                if (e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase))
                {
                    existing = e;
                    break;
                }
            }

            // Memory may have been deleted between Store and sink invocation.
            if (existing is null) return;

            var updated = existing with { Importance = score };
            store.Upsert(updated, scope);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] ImportanceScoringSink error on '{qualifiedName}': " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Result of a backfill pass — counts mirror the embedding reindex result shape so the
    /// CLI command can report them uniformly.
    /// </summary>
    public sealed record BackfillResult(int Total, int Scored, int Skipped, int Failed);

    /// <summary>
    /// Walks every memory in the store and scores any that don't already have an Importance.
    /// Returns counts of scored / skipped (already-scored or empty) / failed (LLM returned
    /// null). Progress is reported via <paramref name="onProgress"/> as (done, total) pairs.
    /// </summary>
    public static async Task<BackfillResult> BackfillAsync(
        IMemoryStore store,
        IBackgroundLlm llm,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(llm);

        var all = store.ListScoped(null);
        int total = all.Count;
        int scored = 0, skipped = 0, failed = 0;
        int done = 0;

        foreach (var (scope, entry) in all.Select(sa => (sa.Scope, sa.Entry)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.Importance is not null)
                {
                    skipped++;
                    continue;
                }

                string content = await DecodeMemoryContentAsync(store, scope, entry, ct);
                if (string.IsNullOrWhiteSpace(content))
                {
                    skipped++;
                    continue;
                }

                int? score = await llm.ScoreImportanceAsync(content, ct);
                if (score is null)
                {
                    failed++;
                    continue;
                }

                store.Upsert(entry with { Importance = score }, scope);
                scored++;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                failed++;
            }
            finally
            {
                done++;
                onProgress?.Invoke(done, total);
            }
        }

        return new BackfillResult(total, scored, skipped, failed);
    }

    /// <summary>
    /// Decodes the full content of a memory by concatenating every chunk. Errors fall
    /// through as empty so the backfill caller treats them as "skip and continue."
    /// </summary>
    private static async Task<string> DecodeMemoryContentAsync(IMemoryStore store, string scope, ArtifactEntry entry, CancellationToken ct)
    {
        try
        {
            string artifact = await store.ReadArtifactAsync(entry.Name, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return string.Empty;

            int chunks = Math.Max(1, entry.ChunkCount);
            if (chunks == 1)
                return Nmp2ChunkedEncoder.DecodeChunk(artifact, 1);

            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= chunks; i++)
            {
                if (i > 1) sb.Append('\n');
                sb.Append(Nmp2ChunkedEncoder.DecodeChunk(artifact, i));
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
