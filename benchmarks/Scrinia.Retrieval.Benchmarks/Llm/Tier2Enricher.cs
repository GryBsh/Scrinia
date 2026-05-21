using System.Collections.Concurrent;
using Scrinia.Core.Llm;

namespace Scrinia.Retrieval.Benchmarks.Llm;

/// <summary>
/// Which Tier 2 operations to run during ingest. Default <c>facts,description</c>
/// covers the two ops that move R@K — facts seed BM25 term-frequencies; descriptions
/// give the field scorer a semantic surface. <see cref="Importance"/> moves NDCG@10
/// but not recall; <see cref="Summary"/> is only useful for long docs (the LlmConsolidator
/// uses it for compacted entries — bench haystacks don't compact).
/// </summary>
public sealed record EnrichOps(bool Facts, bool Description, bool Importance, bool Summary)
{
    public static EnrichOps Default { get; } = new(Facts: true, Description: true, Importance: false, Summary: false);

    public static EnrichOps All { get; } = new(Facts: true, Description: true, Importance: true, Summary: true);

    public static EnrichOps Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Default;
        var ops = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool facts = false, desc = false, imp = false, sum = false;
        foreach (string op in ops)
        {
            switch (op.ToLowerInvariant())
            {
                case "facts": facts = true; break;
                case "description": case "desc": desc = true; break;
                case "importance": case "imp": imp = true; break;
                case "summary": case "sum": sum = true; break;
                case "all": facts = desc = imp = sum = true; break;
                default:
                    throw new ArgumentException(
                        $"unknown --enrich-ops value '{op}'. Expected comma-separated subset of: facts, description, importance, summary (or 'all').");
            }
        }
        return new EnrichOps(facts, desc, imp, sum);
    }

    /// <summary>Comma-separated label for the result-file mode tag and RunMetadata.EnrichmentOps.</summary>
    public string Label()
    {
        var parts = new List<string>(4);
        if (Facts) parts.Add("facts");
        if (Description) parts.Add("description");
        if (Importance) parts.Add("importance");
        if (Summary) parts.Add("summary");
        return string.Join(",", parts);
    }
}

/// <summary>
/// Output of one enrichment pass over one ingest doc. Any field may be null —
/// either the op was disabled, or the LLM call failed/timed out. Null means
/// "leave the corresponding ArtifactEntry field at its Tier 1 default" (callers
/// must not overwrite with null).
/// </summary>
public sealed record EnrichResult(string? Description, string[]? Facts, int? Importance);

/// <summary>
/// Runs Tier 2 enrichment for the bench's <c>ScriniaRetriever</c> ingest path.
/// Wraps an <see cref="IBackgroundLlm"/> with an <see cref="EnrichmentCache"/> and
/// per-op timeout budgets that mirror <c>LlmConsolidator</c>.
///
/// <para>Enabled ops run concurrently per doc via <see cref="Task.WhenAll(Task[])"/>.
/// A single haystack doc with facts + description enabled costs one LLM round-trip
/// wall-time (the two calls go out in parallel) — that's a 2× speedup vs serial.
/// Cross-doc parallelism is intentionally NOT implemented in this commit; the bench
/// runner stays sequential. Add later if MemBench wall-time becomes the bottleneck.</para>
///
/// <para><b>Failure semantics:</b> matches <see cref="IBackgroundLlm"/>'s null-return
/// contract. Per-op timeout, parse failure, or any exception yields null for that
/// op; the other ops in the same pass are unaffected. A null op never overwrites
/// the ingest doc's Tier 1 state — the field is left at its baseline.</para>
/// </summary>
public sealed class Tier2Enricher
{
    // Match LlmConsolidator's per-task budgets — these are empirically tuned and
    // long enough that frontier-tier CLIs (Claude, Codex) finish a typical doc.
    private static readonly TimeSpan DescriptionBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SummaryBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FactsBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ImportanceBudget = TimeSpan.FromSeconds(15);

    private readonly IBackgroundLlm _llm;
    private readonly EnrichmentCache _cache;

    // Singleflight maps. When --ingest-concurrency > 1 sends N parallel ingests of
    // the same doc.Text, the first one's GetOrAdd wins; the other N−1 callers
    // receive the same Lazy<Task> and await its result. Without these, every
    // parallel cold-cache caller would dispatch its own LLM call before any of
    // them got around to writing the disk cache.
    //
    // Lazy<Task> rather than bare Task because ConcurrentDictionary.GetOrAdd's
    // factory MAY execute multiple times under contention; only the winning
    // value is stored. Lazy's ExecutionAndPublication mode ensures the
    // underlying Task is materialised exactly once per Lazy instance, and
    // bare Lazy refs are cheap to allocate even when discarded.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflightDescription = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflightSummary = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string[]?>>> _inflightFacts = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<int?>>> _inflightImportance = new();

    public EnrichOps Ops { get; }
    public string ProviderId { get; }
    public string ModelSlug { get; }
    public EnrichmentCache Cache => _cache;

    public Tier2Enricher(IBackgroundLlm llm, EnrichmentCache cache, EnrichOps ops, string providerId, string modelSlug)
    {
        _llm = llm;
        _cache = cache;
        Ops = ops;
        ProviderId = providerId;
        ModelSlug = modelSlug;
    }

    /// <summary>
    /// Enriches one ingest doc. Runs enabled ops concurrently; returns when all
    /// awaited ops complete (success or failure). The result respects the cache:
    /// repeat calls on the same text hash never call the LLM again.
    /// </summary>
    public async Task<EnrichResult> EnrichAsync(string text, CancellationToken outerCt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new EnrichResult(null, null, null);

        string hash = EnrichmentCache.Hash(text);

        // Description and summary share the Description output slot. If both are
        // enabled, summary takes precedence — it's the longer, content-richer form.
        // In practice the bench enables one or the other, not both.
        Task<string?> descTask = Ops.Description
            ? GetOrComputeStringAsync("description", hash, text, DescriptionBudget, _llm.GenerateDescriptionAsync, _inflightDescription, outerCt)
            : Task.FromResult<string?>(null);

        Task<string?> sumTask = Ops.Summary
            ? GetOrComputeStringAsync("summary", hash, text, SummaryBudget, _llm.SummarizeAsync, _inflightSummary, outerCt)
            : Task.FromResult<string?>(null);

        Task<string[]?> factsTask = Ops.Facts
            ? GetOrComputeFactsAsync(hash, text, outerCt)
            : Task.FromResult<string[]?>(null);

        Task<int?> impTask = Ops.Importance
            ? GetOrComputeImportanceAsync(hash, text, outerCt)
            : Task.FromResult<int?>(null);

        await Task.WhenAll(descTask, sumTask, factsTask, impTask);

        string? description = sumTask.Result ?? descTask.Result;
        return new EnrichResult(description, factsTask.Result, impTask.Result);
    }

    private Task<string?> GetOrComputeStringAsync(
        string op, string hash, string text, TimeSpan budget,
        Func<string, CancellationToken, Task<string?>> call,
        ConcurrentDictionary<string, Lazy<Task<string?>>> inflight,
        CancellationToken outerCt)
    {
        // Disk-cache fast path — most calls past the first per hash hit this.
        if (_cache.TryGetString(op, hash, out string? cached))
            return Task.FromResult(cached);

        // Singleflight: only one parallel caller actually runs the LLM op per hash.
        // The Lazy wrapper lets us pass a value (not a factory) into GetOrAdd, which
        // sidesteps the documented "factory may run multiple times under contention"
        // hazard. Whichever Lazy is in the dict wins; the rest are discarded GC.
        var myLazy = new Lazy<Task<string?>>(
            () => RunStringOpAsync(op, hash, text, budget, call, inflight, outerCt),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = inflight.GetOrAdd(hash, myLazy);
        if (!ReferenceEquals(actual, myLazy))
            _cache.RecordInflightHit();
        return actual.Value;
    }

    private async Task<string?> RunStringOpAsync(
        string op, string hash, string text, TimeSpan budget,
        Func<string, CancellationToken, Task<string?>> call,
        ConcurrentDictionary<string, Lazy<Task<string?>>> inflight,
        CancellationToken outerCt)
    {
        try
        {
            string? result;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(budget);
                result = await call(text, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !outerCt.IsCancellationRequested)
            {
                // Per-op failure is non-fatal — surface a warning and cache null so we
                // don't retry every rerun. OperationCanceledException from the OUTER token
                // is fatal (the user cancelled) and rethrows.
                Console.Error.WriteLine($"[scrinia-bench] enrich:{op} failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
                result = null;
            }
            _cache.PutString(op, hash, result);
            return result;
        }
        finally
        {
            // Remove AFTER the disk cache is written. Concurrent callers arriving
            // between our PutString and TryRemove will see the disk hit instead and
            // skip the inflight check entirely. Removal is just GC hygiene.
            inflight.TryRemove(hash, out _);
        }
    }

    private Task<string[]?> GetOrComputeFactsAsync(string hash, string text, CancellationToken outerCt)
    {
        if (_cache.TryGetStringArray("facts", hash, out string[]? cached))
            return Task.FromResult(cached);

        var myLazy = new Lazy<Task<string[]?>>(
            () => RunFactsAsync(hash, text, outerCt),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = _inflightFacts.GetOrAdd(hash, myLazy);
        if (!ReferenceEquals(actual, myLazy))
            _cache.RecordInflightHit();
        return actual.Value;
    }

    private async Task<string[]?> RunFactsAsync(string hash, string text, CancellationToken outerCt)
    {
        try
        {
            string[]? result;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(FactsBudget);
                result = await _llm.ExtractFactsAsync(text, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !outerCt.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[scrinia-bench] enrich:facts failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
                result = null;
            }
            _cache.PutStringArray("facts", hash, result);
            return result;
        }
        finally
        {
            _inflightFacts.TryRemove(hash, out _);
        }
    }

    private Task<int?> GetOrComputeImportanceAsync(string hash, string text, CancellationToken outerCt)
    {
        if (_cache.TryGetInt("importance", hash, out int? cached))
            return Task.FromResult(cached);

        var myLazy = new Lazy<Task<int?>>(
            () => RunImportanceAsync(hash, text, outerCt),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = _inflightImportance.GetOrAdd(hash, myLazy);
        if (!ReferenceEquals(actual, myLazy))
            _cache.RecordInflightHit();
        return actual.Value;
    }

    private async Task<int?> RunImportanceAsync(string hash, string text, CancellationToken outerCt)
    {
        try
        {
            int? result;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(ImportanceBudget);
                result = await _llm.ScoreImportanceAsync(text, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !outerCt.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[scrinia-bench] enrich:importance failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
                result = null;
            }
            _cache.PutInt("importance", hash, result);
            return result;
        }
        finally
        {
            _inflightImportance.TryRemove(hash, out _);
        }
    }
}
