using Scrinia.Core.Llm;
using Scrinia.Retrieval.Benchmarks.Llm;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Concrete <see cref="IAgentHarness"/> backed by an <see cref="IBackgroundLlm"/>
/// transport and an <see cref="EnrichmentCache"/> for response deduplication.
///
/// <para><b>Transport choice (Phase 3 hack):</b> we do NOT introduce a generic
/// <c>CompleteAsync</c> method on <see cref="IBackgroundLlm"/> — that would ripple
/// changes through <c>src/</c>, which Phase 3 explicitly avoids. Instead this
/// harness PIGGYBACKS on the existing enrichment methods as a transport: anchor
/// generation reuses <see cref="IBackgroundLlm.ExtractFactsAsync"/> (string[] in, string[] out),
/// query rewrite and HyDE reuse <see cref="IBackgroundLlm.GenerateDescriptionAsync"/>
/// (string in, string out). Our agent prompts ride inside the <c>content</c>
/// argument; the model sees the existing system prompt plus our embedded
/// instructions. Hacky, but works for Phase 3. Phase 4 moves the LLM clients into
/// bench and adds a proper <c>CompleteAsync</c> primitive — at which point this
/// transport indirection disappears.</para>
///
/// <para><b>Caching:</b> per-op keys hash the input (content for anchors, query
/// for rewrite/HyDE). The cache stores null results too so we don't re-attempt
/// hopeless inputs every rerun. Cache file naming
/// (<c>agent-{provider}-{model}.ndjson</c>) is distinct from the Tier 2 enrichment
/// cache so the two systems don't pollute each other's caches.</para>
/// </summary>
public sealed class LlmAgentHarness : IAgentHarness
{
    // Op keys for the cache. Distinct from Tier 2's "facts" / "description" / etc.
    // so cache files don't cross-contaminate even if the same cache path were
    // accidentally shared.
    private const string AnchorsOp = "anchors";
    private const string RewriteOp = "rewrite";
    private const string HydeOp = "hyde";

    // Per-op timeout budgets. Anchors get the largest budget because we're asking
    // for a multi-line list; rewrite/HyDE are short single-output ops.
    private static readonly TimeSpan AnchorsBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RewriteBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HydeBudget = TimeSpan.FromSeconds(30);

    private readonly IBackgroundLlm _llm;
    private readonly EnrichmentCache _cache;
    private readonly AgentModes _modes;

    public string ProviderId { get; }
    public string ModelSlug { get; }
    public AgentModes Modes => _modes;
    public EnrichmentCache Cache => _cache;

    public LlmAgentHarness(
        IBackgroundLlm llm,
        EnrichmentCache cache,
        string providerId,
        string modelSlug,
        AgentModes enabledOps)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSlug);
        _llm = llm;
        _cache = cache;
        ProviderId = providerId;
        ModelSlug = modelSlug;
        _modes = enabledOps;
    }

    public async Task<IReadOnlyList<string>?> GenerateAnchorsAsync(string content, CancellationToken ct)
    {
        if (!_modes.Anchors) return null;
        if (string.IsNullOrWhiteSpace(content)) return null;

        string hash = EnrichmentCache.Hash(content);
        if (_cache.TryGetStringArray(AnchorsOp, hash, out string[]? cached))
            return cached;  // may be null — null is cached to skip retries

        // HACK: piggyback on ExtractFactsAsync as the LLM transport. The model
        // receives the "Gather up to 7 facts..." system prompt PLUS our embedded
        // anchor instructions in the content slot. The output gets parsed as
        // anchors via AgentPrompts.ParseAnchors. Will be replaced in Phase 4 when
        // a proper CompleteAsync(system, user) primitive lands in the bench-owned
        // LLM client.
        string syntheticContent =
            AgentPrompts.AnchorsSystem +
            "\n\n" + AgentPrompts.AnchorsUser(content);

        string[]? rawArray;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnchorsBudget);
            rawArray = await _llm.ExtractFactsAsync(syntheticContent, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:anchors failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            rawArray = null;
        }

        // The transport returned facts (already line-parsed by LlmPrompts.ParseFacts).
        // We re-run AgentPrompts.ParseAnchors over the joined output so our stricter
        // length bounds + dedup + cap kick in. Empty → null (don't persist empties).
        string[] parsed = rawArray is null
            ? Array.Empty<string>()
            : AgentPrompts.ParseAnchors(string.Join('\n', rawArray));
        string[]? result = parsed.Length == 0 ? null : parsed;
        _cache.PutStringArray(AnchorsOp, hash, result);
        return result;
    }

    public async Task<string?> RewriteQueryAsync(string query, CancellationToken ct)
    {
        if (!_modes.Rewrite) return null;
        if (string.IsNullOrWhiteSpace(query)) return null;

        string hash = EnrichmentCache.Hash(query);
        if (_cache.TryGetString(RewriteOp, hash, out string? cached))
            return cached;

        // HACK: piggyback on GenerateDescriptionAsync. The transport's system
        // prompt is "Summarize in 120 chars" — we override semantics via the
        // content slot, asking the model to rewrite our query instead. Output
        // is parsed as a single line of text.
        string syntheticContent =
            AgentPrompts.RewriteSystem +
            "\n\n" + AgentPrompts.RewriteUser(query);

        string? rawResult;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RewriteBudget);
            rawResult = await _llm.GenerateDescriptionAsync(syntheticContent, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:rewrite failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            rawResult = null;
        }

        string? parsed = AgentPrompts.ParseRewrite(rawResult);
        _cache.PutString(RewriteOp, hash, parsed);
        return parsed;
    }

    public async Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct)
    {
        if (!_modes.Hyde) return null;
        if (string.IsNullOrWhiteSpace(query)) return null;

        string hash = EnrichmentCache.Hash(query);
        if (_cache.TryGetString(HydeOp, hash, out string? cached))
            return cached;

        // HACK: piggyback on GenerateDescriptionAsync. Same transport as rewrite;
        // the embedded HydeSystem prompt asks for a hypothetical answer instead
        // of a rewrite. Output is free-form 1-2 sentences.
        string syntheticContent =
            AgentPrompts.HydeSystem +
            "\n\n" + AgentPrompts.HydeUser(query);

        string? rawResult;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HydeBudget);
            rawResult = await _llm.GenerateDescriptionAsync(syntheticContent, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:hyde failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            rawResult = null;
        }

        string? parsed = AgentPrompts.ParseHyde(rawResult);
        _cache.PutString(HydeOp, hash, parsed);
        return parsed;
    }
}
