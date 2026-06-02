using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Concrete <see cref="IAgentHarness"/> backed by an <see cref="IBenchLlmClient"/>
/// transport and an <see cref="LlmResponseCache"/> for response deduplication.
///
/// <para><b>Transport:</b> dispatches the three agent ops (anchors, rewrite, HyDE)
/// through <see cref="IBenchLlmClient.CompleteAsync"/>, passing the per-op system
/// and user prompts from <see cref="AgentPrompts"/>. Each op layers its own per-call
/// timeout budget via cancellation tokens and parses the LLM's reply through the
/// matching <c>ParseX</c> helper.</para>
///
/// <para><b>Caching:</b> per-op keys hash the input (content for anchors, query
/// for rewrite/HyDE). The cache stores null results too so we don't re-attempt
/// hopeless inputs every rerun. Cache file naming (<c>agent-{provider}-{model}.ndjson</c>)
/// keeps different provider+model combinations from cross-contaminating.</para>
/// </summary>
public sealed class LlmAgentHarness : IAgentHarness
{
    // Op keys for the cache. Distinct strings so anchors / rewrite / HyDE don't
    // cross-contaminate even when keyed on the same input hash.
    private const string AnchorsOp = "anchors";
    private const string RewriteOp = "rewrite";
    private const string HydeOp = "hyde";
    private const string TypedKeywordsOp = "typed";

    // Per-op timeout budgets. Anchors get the largest budget because we're asking
    // for a multi-line list; rewrite/HyDE are short single-output ops.
    private static readonly TimeSpan AnchorsBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RewriteBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HydeBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TypedKeywordsBudget = TimeSpan.FromSeconds(60);

    // Max-token budgets. Sized to cover the prompt's expected output plus headroom.
    // Anchors: 5-7 lines × ~60 chars × 1.5 tokens/char ≈ 400. Rewrite: one line ≈ 100.
    // HyDE: 1-2 sentences ≈ 200.
    private const int AnchorsMaxTokens = 400;
    private const int RewriteMaxTokens = 100;
    private const int HydeMaxTokens = 200;
    private const int TypedKeywordsMaxTokens = 200;

    private readonly IBenchLlmClient _llm;
    private readonly LlmResponseCache _cache;
    private readonly AgentModes _modes;

    public string ProviderId { get; }
    public string ModelSlug { get; }
    public AgentModes Modes => _modes;
    public LlmResponseCache Cache => _cache;

    public LlmAgentHarness(
        IBenchLlmClient llm,
        LlmResponseCache cache,
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

        string hash = LlmResponseCache.Hash(content);
        if (_cache.TryGetStringArray(AnchorsOp, hash, out string[]? cached))
            return cached;  // may be null — null is cached to skip retries

        // Anchors are line-mode plain text (one anchor per line). jsonMode=false:
        // the prompt is engineered for line output, and small models stay more
        // consistent in line-mode than JSON-mode for short list completions.
        string? raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AnchorsBudget);
            raw = await _llm.CompleteAsync(
                AgentPrompts.AnchorsSystem,
                AgentPrompts.AnchorsUser(content),
                maxTokens: AnchorsMaxTokens,
                jsonMode: false,
                cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:anchors failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            raw = null;
        }

        string[] parsed = raw is null ? Array.Empty<string>() : AgentPrompts.ParseAnchors(raw);
        string[]? result = parsed.Length == 0 ? null : parsed;
        _cache.PutStringArray(AnchorsOp, hash, result);
        return result;
    }

    public async Task<string?> RewriteQueryAsync(string query, CancellationToken ct)
    {
        if (!_modes.Rewrite) return null;
        if (string.IsNullOrWhiteSpace(query)) return null;

        string hash = LlmResponseCache.Hash(query);
        if (_cache.TryGetString(RewriteOp, hash, out string? cached))
            return cached;

        string? raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RewriteBudget);
            raw = await _llm.CompleteAsync(
                AgentPrompts.RewriteSystem,
                AgentPrompts.RewriteUser(query),
                maxTokens: RewriteMaxTokens,
                jsonMode: false,
                cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:rewrite failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            raw = null;
        }

        string? parsed = AgentPrompts.ParseRewrite(raw);
        _cache.PutString(RewriteOp, hash, parsed);
        return parsed;
    }

    public async Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct)
    {
        if (!_modes.Hyde) return null;
        if (string.IsNullOrWhiteSpace(query)) return null;

        string hash = LlmResponseCache.Hash(query);
        if (_cache.TryGetString(HydeOp, hash, out string? cached))
            return cached;

        string? raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HydeBudget);
            raw = await _llm.CompleteAsync(
                AgentPrompts.HydeSystem,
                AgentPrompts.HydeUser(query),
                maxTokens: HydeMaxTokens,
                jsonMode: false,
                cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:hyde failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            raw = null;
        }

        string? parsed = AgentPrompts.ParseHyde(raw);
        _cache.PutString(HydeOp, hash, parsed);
        return parsed;
    }

    public async Task<IReadOnlyList<string>?> GenerateTypedKeywordsAsync(string content, CancellationToken ct)
    {
        if (!_modes.TypedKeywords) return null;
        if (string.IsNullOrWhiteSpace(content)) return null;

        string hash = LlmResponseCache.Hash(content);
        if (_cache.TryGetStringArray(TypedKeywordsOp, hash, out string[]? cached))
            return cached;  // may be null — null is cached to skip retries

        string? raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TypedKeywordsBudget);
            raw = await _llm.CompleteAsync(
                AgentPrompts.TypedKeywordsSystem,
                AgentPrompts.TypedKeywordsUser(content),
                maxTokens: TypedKeywordsMaxTokens,
                jsonMode: false,
                cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:typed failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            raw = null;
        }

        string[] parsed = raw is null ? Array.Empty<string>() : AgentPrompts.ParseTypedKeywords(raw);
        string[]? result = parsed.Length == 0 ? null : parsed;
        _cache.PutStringArray(TypedKeywordsOp, hash, result);
        return result;
    }
}
