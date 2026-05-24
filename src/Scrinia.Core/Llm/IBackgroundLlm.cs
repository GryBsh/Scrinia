namespace Scrinia.Core.Llm;

/// <summary>
/// Narrow request-response surface for Tier 2 consolidation tasks ("Dreaming").
/// Backends implement this directly; callers in <c>Scrinia.Core</c> own the prompt
/// templates so adding a new tier-2 capability is a Scrinia.Core change, not a
/// backend/plugin rebuild.
///
/// <para>Two production implementations live alongside this interface:
/// <see cref="OpenAiCompatibleBackgroundLlm"/> for HTTP runtimes (Ollama, llama.cpp
/// server, LM Studio, Docker Model Runner) and <c>PluginBackgroundLlm</c> for the
/// bundled <c>scri-plugin-llm</c> subprocess. Both share the same per-task prompts
/// from <c>LlmPrompts</c>.</para>
///
/// <para>Operations are batch-oriented and tolerant of timeouts — Tier 2 is not on
/// the search hot path. Callers should impose per-call cancellation budgets
/// appropriate to the task (descriptions: short, summaries: medium, fact
/// extraction: long).</para>
/// </summary>
public interface IBackgroundLlm
{
    /// <summary>
    /// Generates a short, model-written description for a memory whose existing
    /// description is the auto-fallback (first N chars of content). Returns null
    /// on timeout, empty response, or any failure the caller should treat as
    /// "skip this memory and continue the batch."
    /// </summary>
    /// <param name="content">Decoded memory body the description should describe.</param>
    /// <param name="ct">Per-call cancellation budget; if it fires, return null.</param>
    Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct);

    /// <summary>
    /// Summarizes a longer text (e.g. a compacted session log) into a single paragraph.
    /// Returns null on any failure the caller should treat as skip-and-continue.
    /// </summary>
    Task<string?> SummarizeAsync(string text, CancellationToken ct);

    /// <summary>
    /// Extracts 3–7 atomic facts from a memory's content as a string array (Mem0-style).
    /// Each fact is a single self-contained claim suitable for keyword-style indexing.
    /// Returns null on parse failure, empty list, or timeout — caller skips the memory.
    /// </summary>
    Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct);

    /// <summary>
    /// Rates a memory's importance on a 1–10 scale (Generative Agents "poignancy").
    /// Used by <c>ImportanceScoringSink</c> after each successful Upsert to populate
    /// <c>ArtifactEntry.Importance</c> asynchronously. Returns null on parse failure,
    /// out-of-range output, timeout, or any other failure — the sink treats null as
    /// "leave the field unscored" and the ranker falls back to its neutral midpoint.
    /// </summary>
    /// <param name="content">Decoded memory body to evaluate.</param>
    /// <param name="ct">Per-call cancellation budget; if it fires, return null.</param>
    Task<int?> ScoreImportanceAsync(string content, CancellationToken ct);

    /// <summary>
    /// Cheap availability probe. True when the backend is reachable AND a model is
    /// loaded AND a trivial completion would likely succeed. Used by the consolidate
    /// command's pre-flight to decide whether to attempt Tier 2 at all.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Combined description + facts in a single LLM round-trip, returned as a JSON
    /// object. Halves the per-doc Tier 2 call volume vs invoking
    /// <see cref="GenerateDescriptionAsync"/> and <see cref="ExtractFactsAsync"/> separately,
    /// and lets the model ground facts in the same context that produced the description.
    ///
    /// <para>Returns null when the backend can't honor wire-level JSON-mode (agent CLIs
    /// pipe through stdio with no <c>response_format</c> constraint), or when the model's
    /// output fails to parse as the expected JSON shape. Callers must fall back to the
    /// per-op methods on null — they're guaranteed to remain functional.</para>
    ///
    /// <para>Either field of the result may be null independently: an empty/missing
    /// description string and an empty/missing facts array are both treated as null
    /// per the existing per-op null semantics. A non-null result with both fields null
    /// is permitted but rare — typically indicates content with nothing extractable.</para>
    ///
    /// <para>Default implementation returns null so backends that don't support combined
    /// generation don't need to opt out explicitly.</para>
    /// </summary>
    /// <param name="content">Decoded memory body to describe and extract facts from.</param>
    /// <param name="ct">Per-call cancellation budget; if it fires, return null.</param>
    Task<CombinedEnrichment?> EnrichCombinedAsync(string content, CancellationToken ct) =>
        Task.FromResult<CombinedEnrichment?>(null);
}

/// <summary>
/// Result of a single combined Tier 2 call. Either field may be null independently.
/// Construct via <see cref="IBackgroundLlm.EnrichCombinedAsync"/>; consume via
/// <c>LlmConsolidator</c> or the bench's <c>Tier2Enricher</c>, which split the result
/// back into per-op writes against the existing storage and cache surfaces.
/// </summary>
public sealed record CombinedEnrichment(string? Description, string[]? Facts);
