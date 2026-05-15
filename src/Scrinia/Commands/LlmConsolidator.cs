using System.Security.Cryptography;
using Scrinia.Core.Encoding;
using Scrinia.Core.Llm;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Commands;

/// <summary>
/// Tier 2 consolidation: the LLM-driven "dreaming" pass that runs when
/// <c>scri consolidate --with-llm</c> is invoked. Three operations per memory:
///   1. Auto-description backfill — replace the first-200-chars fallback with a
///      model-written sentence when the existing description is the auto-fallback.
///   2. Session summarization — for entries that Tier 1 just compacted (long
///      session logs collapsed from N chunks to 1), produce a paragraph-length
///      summary that becomes the new description.
///   3. Atomic-fact extraction (Mem0-style) — pull 3–7 self-contained facts
///      onto the entry's <see cref="ArtifactEntry.Facts"/> field and seed the
///      TF dict so BM25 retrieves them.
///
/// <para>State is durable: a progress file in <c>.scrinia/.consolidate-progress.json</c>
/// keyed by qualified name + content hash makes the pass batch-resumable. A crash
/// or kill mid-run only re-processes memories whose work didn't commit.</para>
///
/// <para>Failure-tolerant by design — any per-memory error (LLM timeout, parse
/// failure, transient HTTP error) is logged via <paramref name="onWarning"/> and
/// the batch continues. The caller surfaces aggregate counts.</para>
/// </summary>
internal static class LlmConsolidator
{
    private const string ProgressFileName = ".consolidate-progress.json";
    private const int ProgressFileVersion = 1;

    // How often the progress file is flushed during a run. Writing after every entry hammers
    // file-replication agents (OneDrive, Dropbox, Synology Drive) and triggers transient
    // file-lock contention on Windows. Batching to every N entries keeps the skip-list usefully
    // fresh for resume scenarios while letting sync clients keep up. The final flush at the end
    // of the run guarantees a complete record even mid-batch.
    private const int ProgressFlushEveryNEntries = 5;

    // Per-task cancellation budgets. Descriptions are short, summaries medium,
    // fact extraction long. The outer CT still wins if the run is cancelled.
    private static readonly TimeSpan DescriptionBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SummaryBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FactsBudget = TimeSpan.FromSeconds(90);

    public sealed record Result(
        int Processed,
        int DescriptionsBackfilled,
        int SessionsSummarized,
        int FactsExtracted,
        int Skipped,
        int Failed);

    /// <summary>
    /// Runs Tier 2 over the provided entries. <paramref name="justCompacted"/> is the
    /// set of qualified names that Tier 1 compacted in this same run — those entries
    /// get summary treatment rather than the short-description treatment.
    /// </summary>
    public static async Task<Result> RunAsync(
        IBackgroundLlm llm,
        IReadOnlyList<ScopedArtifact> entries,
        IReadOnlySet<string> justCompacted,
        string scriniaDir,
        bool dryRun,
        Action<string>? onWarning,
        CancellationToken outerCt)
    {
        var progress = LoadProgress(scriniaDir);
        int processed = 0, descriptions = 0, summaries = 0, facts = 0, skipped = 0, failed = 0;
        int pendingProgressWrites = 0;

        foreach (var item in entries)
        {
            outerCt.ThrowIfCancellationRequested();

            string qualifiedName = item.Scope == "ephemeral"
                ? $"~{item.Entry.Name}"
                : ScriniaArtifactStore.FormatQualifiedName(item.Scope, item.Entry.Name);

            // Ephemeral memories are by definition transient — no value in producing
            // facts or descriptions that disappear with the process.
            if (item.Scope == "ephemeral") continue;

            string? content = TryDecodeArtifact(item, onWarning);
            if (content is null) { failed++; continue; }

            string hash = ComputeContentHash(content);
            bool isSession = justCompacted.Contains(qualifiedName);
            progress.Entries.TryGetValue(qualifiedName, out var prior);
            bool hashUnchanged = prior is not null && prior.ContentHash == hash;

            // Skip-list: process iff there's any work left to do for the current content.
            // Description doesn't need work if it isn't the auto-fallback OR if we already
            // wrote one at this hash. Session-summary needs a positive progress flag. Facts
            // need a positive progress flag at the matching hash — entry.Facts alone isn't
            // enough because content may have changed and old facts are now stale.
            bool descriptionSettled =
                !IsAutoFallbackDescription(item.Entry.Description, content)
                || (hashUnchanged && (prior!.DescriptionDone || prior.SummarizedFromSession));
            bool sessionSettled =
                !isSession || (hashUnchanged && prior!.SummarizedFromSession);
            bool factsSettled = hashUnchanged && prior!.FactsDone;

            if (descriptionSettled && sessionSettled && factsSettled)
            {
                skipped++;
                continue;
            }

            if (dryRun)
            {
                processed++;
                continue;
            }

            bool didDescription = false, didSummary = false, didFacts = false;
            var updated = item.Entry;

            // Description / summary. Sessions just-compacted by Tier 1 get the longer
            // summary form (paragraph); other auto-fallback descriptions get a sentence.
            // Real descriptions (set by user/agent) are left alone.
            if (isSession || IsAutoFallbackDescription(updated.Description, content))
            {
                string? text;
                try
                {
                    using var taskCts = LinkedCts(isSession ? SummaryBudget : DescriptionBudget, outerCt);
                    text = isSession
                        ? await llm.SummarizeAsync(content, taskCts.Token)
                        : await llm.GenerateDescriptionAsync(content, taskCts.Token);
                }
                catch (Exception ex)
                {
                    onWarning?.Invoke($"description/summary for {qualifiedName} failed: {ex.GetType().Name}");
                    text = null;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    updated = updated with { Description = text!, UpdatedAt = DateTimeOffset.UtcNow };
                    if (isSession) { summaries++; didSummary = true; }
                    else { descriptions++; didDescription = true; }
                }
            }

            // Fact extraction. Run for every memory not yet fact-extracted at this hash.
            if (!factsSettled)
            {
                string[]? extracted;
                try
                {
                    using var taskCts = LinkedCts(FactsBudget, outerCt);
                    extracted = await llm.ExtractFactsAsync(content, taskCts.Token);
                }
                catch (Exception ex)
                {
                    onWarning?.Invoke($"fact extraction for {qualifiedName} failed: {ex.GetType().Name}");
                    extracted = null;
                }

                if (extracted is { Length: > 0 })
                {
                    // Seed the TF dict so BM25 picks up fact terms. +2 weight per token
                    // matches the boost agent-merged keywords get in the Store path.
                    //
                    // Manual merge with case-insensitive comparer rather than the copy-ctor:
                    // sidecar JSON deserializes the source dict case-sensitively, so it can
                    // legitimately hold both "BM25" and "bm25" — the copy-ctor would throw.
                    // BM25 scoring already treats them as the same token, so summing here
                    // preserves the effective weight while normalizing the keys.
                    var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (updated.TermFrequencies is not null)
                    {
                        foreach (var (k, v) in updated.TermFrequencies)
                        {
                            tf.TryGetValue(k, out int existing);
                            tf[k] = existing + v;
                        }
                    }
                    foreach (string fact in extracted)
                    {
                        foreach (string token in TextAnalysis.Tokenize(fact))
                        {
                            tf.TryGetValue(token, out int count);
                            tf[token] = count + 2;
                        }
                    }
                    updated = updated with
                    {
                        Facts = extracted,
                        TermFrequencies = tf,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    facts++;
                    didFacts = true;
                }
            }

            if (didDescription || didSummary || didFacts)
            {
                ScriniaArtifactStore.Upsert(updated, item.Scope);
                processed++;
            }
            else
            {
                failed++;
            }

            // Update progress regardless of partial success — records hash so re-run skips
            // the operations that did complete. Persistence batched every N entries below.
            progress.Entries[qualifiedName] = new ConsolidateEntryProgress(
                ContentHash: hash,
                ProcessedAt: DateTimeOffset.UtcNow.ToString("o"),
                DescriptionDone: didDescription || (prior?.DescriptionDone ?? false),
                SummarizedFromSession: didSummary || (prior?.SummarizedFromSession ?? false),
                FactsDone: didFacts || (prior?.FactsDone ?? false));

            pendingProgressWrites++;
            if (pendingProgressWrites >= ProgressFlushEveryNEntries)
            {
                TrySaveProgress(scriniaDir, progress, onWarning);
                pendingProgressWrites = 0;
            }
        }

        // Final flush — captures any entries since the last batched write.
        if (pendingProgressWrites > 0)
            TrySaveProgress(scriniaDir, progress, onWarning);

        return new Result(processed, descriptions, summaries, facts, skipped, failed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects whether <paramref name="description"/> is the synthetic first-200-chars
    /// fallback set by <c>Store</c> when no description was provided. <c>Store</c> writes
    /// exactly <c>content[..Math.Min(200, content.Length)]</c>, so the detector requires
    /// both a length match and prefix equality — a prefix-only check was too loose and
    /// silently overwrote user-supplied short descriptions whose words happened to lead
    /// the content (e.g. <c>-d "OAuth API"</c> on a file starting "OAuth API documentation").
    /// </summary>
    private static bool IsAutoFallbackDescription(string description, string content)
    {
        if (string.IsNullOrWhiteSpace(description)) return true;
        int autoLength = Math.Min(200, content.Length);
        if (description.Length != autoLength) return false;
        return content.AsSpan(0, autoLength).Equals(description.AsSpan(), StringComparison.Ordinal);
    }

    private static string ComputeContentHash(string content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content), hash);
        return Convert.ToHexStringLower(hash);
    }

    private static string? TryDecodeArtifact(ScopedArtifact item, Action<string>? onWarning)
    {
        string path = ScriniaArtifactStore.FindArtifactPath(item.Entry.Name, item.Scope);
        if (!File.Exists(path))
        {
            onWarning?.Invoke($"artifact missing for {item.Entry.Name}: {path}");
            return null;
        }
        try
        {
            string artifact = File.ReadAllText(path);
            byte[] bytes = Nmp2Strategy.Instance.Decode(artifact);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            onWarning?.Invoke($"decode failed for {item.Entry.Name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static CancellationTokenSource LinkedCts(TimeSpan budget, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        return cts;
    }

    // ── Progress file ───────────────────────────────────────────────────────────

    private static ConsolidateProgressFile LoadProgress(string scriniaDir)
    {
        string path = Path.Combine(scriniaDir, ProgressFileName);
        if (!File.Exists(path))
            return new ConsolidateProgressFile(ProgressFileVersion, "", new Dictionary<string, ConsolidateEntryProgress>(StringComparer.OrdinalIgnoreCase));
        try
        {
            string json = File.ReadAllText(path);
            var parsed = System.Text.Json.JsonSerializer.Deserialize(
                json, CliJsonContext.Default.ConsolidateProgressFile);
            if (parsed is null)
                return new ConsolidateProgressFile(ProgressFileVersion, "", new Dictionary<string, ConsolidateEntryProgress>(StringComparer.OrdinalIgnoreCase));
            // Normalize map case-insensitively for safe key lookup
            var entries = new Dictionary<string, ConsolidateEntryProgress>(parsed.Entries, StringComparer.OrdinalIgnoreCase);
            return parsed with { Entries = entries };
        }
        catch
        {
            // Corrupt progress file → start fresh; the on-disk sidecar state is the source
            // of truth, the progress file is just a skip-list optimization.
            return new ConsolidateProgressFile(ProgressFileVersion, "", new Dictionary<string, ConsolidateEntryProgress>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Best-effort progress-file write. The progress file is a skip-list optimisation,
    /// not the source of truth — losing a write only forces re-processing on next run.
    /// Common cause of failure: file-replication agents (OneDrive, Synology Drive) holding
    /// transient handles during the atomic rename. The caller continues regardless.
    /// </summary>
    private static void TrySaveProgress(
        string scriniaDir, ConsolidateProgressFile progress, Action<string>? onWarning)
    {
        try
        {
            SaveProgress(scriniaDir, progress);
        }
        catch (Exception ex)
        {
            onWarning?.Invoke(
                $"progress file write failed ({ex.GetType().Name}: {ex.Message}) — " +
                "continuing without skip-list updates; this run will complete but a future " +
                "resume may re-process already-completed entries.");
        }
    }

    private static void SaveProgress(string scriniaDir, ConsolidateProgressFile progress)
    {
        Directory.CreateDirectory(scriniaDir);
        string path = Path.Combine(scriniaDir, ProgressFileName);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        var updated = progress with { LastUpdated = DateTimeOffset.UtcNow.ToString("o") };
        string json = System.Text.Json.JsonSerializer.Serialize(
            updated, CliJsonContext.Default.ConsolidateProgressFile);
        File.WriteAllText(tmp, json);

        // Atomic rename with retry. File.Move(overwrite: true) on Windows can throw
        // UnauthorizedAccessException when a sync client (Synology Drive, OneDrive, Dropbox)
        // briefly opens the destination for replication right after our previous write.
        // Brief backoff lets the agent close its handle; final attempt is unguarded so a
        // persistent failure surfaces to TrySaveProgress instead of being swallowed silently.
        int[] backoffsMs = [50, 100, 250];
        foreach (int delayMs in backoffsMs)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(delayMs);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Top-level shape of <c>.scrinia/.consolidate-progress.json</c>.</summary>
internal sealed record ConsolidateProgressFile(
    int Version,
    string LastUpdated,
    Dictionary<string, ConsolidateEntryProgress> Entries);

/// <summary>Per-memory record in the consolidate progress file.</summary>
internal sealed record ConsolidateEntryProgress(
    string ContentHash,
    string ProcessedAt,
    bool DescriptionDone,
    bool SummarizedFromSession,
    bool FactsDone);
