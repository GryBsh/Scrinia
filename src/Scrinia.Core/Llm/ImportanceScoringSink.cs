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
    {
        var llm = _llmAccessor();
        if (llm is null) return;

        try
        {
            var (scope, existing) = FindEntry(store, qualifiedName);
            if (existing is null) return;
            await ScoreAndUpdateAsync(llm, store, scope, existing, string.Concat(content), ct);
        }
        catch (Exception ex)
        {
            LogWarn(qualifiedName, ex);
        }
    }

    /// <summary>
    /// On append, rescore against the <b>full</b> memory (existing + appended), not the
    /// appendage alone. Scoring just the new chunk would clobber the previous score with
    /// one derived from a partial view — e.g. an "important architectural decision" memory
    /// with importance 9 would drop to 2 after a one-line "fixed typo" append.
    /// </summary>
    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        var llm = _llmAccessor();
        if (llm is null) return;

        try
        {
            var (scope, existing) = FindEntry(store, qualifiedName);
            if (existing is null) return;
            string full = await DecodeFullContentAsync(store, scope, existing, ct);
            await ScoreAndUpdateAsync(llm, store, scope, existing, full, ct);
        }
        catch (Exception ex)
        {
            LogWarn(qualifiedName, ex);
        }
    }

    /// <summary>Forgotten memories don't need rescoring; their sidecar is gone.</summary>
    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Locates the live <see cref="ArtifactEntry"/> for <paramref name="qualifiedName"/>.
    /// Returns (scope, null) when the memory was forgotten between the original write and
    /// the sink running — a tolerable race for fire-and-forget background work.
    /// </summary>
    private static (string Scope, ArtifactEntry? Entry) FindEntry(IMemoryStore store, string qualifiedName)
    {
        var (scope, subject) = store.ParseQualifiedName(qualifiedName);
        foreach (var e in store.LoadIndex(scope))
        {
            if (e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase))
                return (scope, e);
        }
        return (scope, null);
    }

    /// <summary>
    /// Sends content through the LLM and persists the score. Treats empty content and a
    /// null LLM response as "skip this memory" — leaves the sidecar untouched.
    /// </summary>
    private static async Task ScoreAndUpdateAsync(
        IBackgroundLlm llm, IMemoryStore store, string scope, ArtifactEntry existing,
        string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        int? score = await llm.ScoreImportanceAsync(content, ct);
        if (score is null) return;
        store.Upsert(existing with { Importance = score }, scope);
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

                string content = await DecodeFullContentAsync(store, scope, entry, ct);
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
    /// Reads the artifact and returns the decoded text. Uses the canonical
    /// <see cref="Nmp2Strategy.Decode(string)"/> which handles single- and multi-chunk
    /// artifacts identically to every other content-consumer (embeddings reindexer,
    /// LLM consolidator). Errors fall through as empty so callers treat them as skip.
    /// </summary>
    private static async Task<string> DecodeFullContentAsync(IMemoryStore store, string scope, ArtifactEntry entry, CancellationToken ct)
    {
        try
        {
            string artifact = await store.ReadArtifactAsync(entry.Name, scope, ct);
            if (string.IsNullOrWhiteSpace(artifact)) return string.Empty;
            byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogWarn(string qualifiedName, Exception ex) =>
        Console.Error.WriteLine(
            $"[scrinia:warn] ImportanceScoringSink error on '{qualifiedName}': " +
            $"{ex.GetType().Name}: {ex.Message}");
}
