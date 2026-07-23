using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Concrete <see cref="IAgentHarness"/> backed by an <see cref="IBenchLlmClient"/>
/// transport and an <see cref="LlmResponseCache"/> for response deduplication.
///
/// <para><b>Transport:</b> one JSON-mode <see cref="IBenchLlmClient.CompleteAsync"/>
/// call per doc, using <see cref="AgentPrompts.IngestSystem"/> /
/// <see cref="AgentPrompts.IngestUser"/>, parsed through
/// <see cref="AgentPrompts.ParseIngest"/>. A single call yields both the
/// <c>attributes</c> map and the <c>keywords</c> — exactly what a production agent
/// hands the <c>remember</c> tool in one shot.</para>
///
/// <para><b>Caching:</b> keyed on the document content hash. ONLY successful replies are
/// cached; a doc that never parsed is left uncached so a later rerun retries it. Cache file
/// naming (<c>agent-{provider}-{model}.ndjson</c>) keeps provider+model combinations from
/// cross-contaminating.</para>
///
/// <para><b>Tally:</b> the harness keeps whole-run counters (<see cref="Stats"/>) so a run
/// can reconcile docs-seen against attributes/keywords produced and confirm nothing was silently dropped
/// on the (by default silent) empty-reply path.</para>
/// </summary>
public sealed class LlmAgentHarness : IAgentHarness
{
    // Cache op key. Versioned: the key hashes document content only, NOT the prompt, so a
    // prompt change would otherwise silently reuse stale output. Bump on any prompt/schema
    // change. "ingest1" = the combined attributes+keywords op (replaced the split
    // anchors/typed/rewrite/hyde ops).
    private const string IngestOp = "ingest1";

    // One combined op: give it the larger of the old budgets since it now produces both
    // an attribute map and a keyword list in a single JSON reply.
    private static readonly TimeSpan IngestBudget = TimeSpan.FromSeconds(120);

    // max_tokens for the JSON reply: attribute map (up to 12 keys × a few values) plus a
    // keyword list (up to 24) with JSON punctuation ≈ a few hundred tokens; 512 leaves headroom.
    private const int IngestMaxTokens = 512;

    /// <summary>Default attempts per doc: 1, i.e. retries OFF. Retrying is an opt-in feature —
    /// raise <c>--agent-max-attempts</c> above 1 to enable it. Small models intermittently
    /// return an empty / unparseable / empty-stub reply; because sampling temperature is
    /// &gt; 0 a fresh attempt genuinely re-rolls, so a couple of retries recover most of them,
    /// at the cost of extra LLM calls — hence off by default.</summary>
    public const int DefaultMaxAttempts = 1;

    private readonly IBenchLlmClient _llm;
    private readonly LlmResponseCache _cache;
    private readonly int _maxAttempts;

    // Whole-run tally. Ingest runs under Parallel.ForEachAsync, so these are touched
    // concurrently — mutate only via Interlocked. Surfaced through Stats.
    private long _seen;          // GenerateIngestAsync calls (one per ingested doc)
    private long _emptyContent;  // skipped: blank content, no cache lookup, no LLM call
    private long _cacheHits;     // served from a prior successful reply in the cache
    private long _llmDocs;       // docs that made >=1 fresh LLM call this run
    private long _llmAttempts;   // total LLM attempts incl. retries
    private long _produced;      // returned usable attributes/keywords (cache hits + fresh successes)
    private long _failed;        // LLM-called docs that yielded no usable attributes/keywords (uncached)

    public string ProviderId { get; }
    public string ModelSlug { get; }
    public LlmResponseCache Cache => _cache;

    /// <summary>Whole-run ingest tally (see <see cref="AgentIngestStats"/>). Read after the run
    /// to reconcile: <c>Produced == CacheHits + (LlmDocs - Failed)</c>, and the cache should have
    /// gained exactly <c>LlmDocs - Failed</c> lines this run. <c>Failed == 0</c> means every doc
    /// that reached the model parsed cleanly.</summary>
    public AgentIngestStats Stats => new(
        Seen: Interlocked.Read(ref _seen),
        EmptyContent: Interlocked.Read(ref _emptyContent),
        CacheHits: Interlocked.Read(ref _cacheHits),
        LlmDocs: Interlocked.Read(ref _llmDocs),
        LlmAttempts: Interlocked.Read(ref _llmAttempts),
        Produced: Interlocked.Read(ref _produced),
        Failed: Interlocked.Read(ref _failed));

    public LlmAgentHarness(
        IBenchLlmClient llm,
        LlmResponseCache cache,
        string providerId,
        string modelSlug,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSlug);
        _llm = llm;
        _cache = cache;
        ProviderId = providerId;
        ModelSlug = modelSlug;
        _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
    }

    public async Task<AgentIngest?> GenerateIngestAsync(string content, CancellationToken ct)
    {
        Interlocked.Increment(ref _seen);
        if (string.IsNullOrWhiteSpace(content))
        {
            Interlocked.Increment(ref _emptyContent);
            return null;
        }

        string hash = LlmResponseCache.Hash(content);
        if (_cache.TryGetString(IngestOp, hash, out string? cachedRaw))
        {
            Interlocked.Increment(ref _cacheHits);
            var cachedParsed = AgentPrompts.ParseIngest(cachedRaw);  // only successful replies are cached
            if (cachedParsed is not null) Interlocked.Increment(ref _produced);
            return cachedParsed;
        }

        // Retry empty/unparseable replies up to _maxAttempts. Each attempt is a fresh LLM
        // call (temperature > 0 → a real re-roll). ONLY a usable reply is cached — a doc that
        // never produced attributes/keywords is left uncached so a later rerun retries it too, mirroring the
        // agentic-ingest arm's "never cache a failed episode" rule.
        Interlocked.Increment(ref _llmDocs);
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            Interlocked.Increment(ref _llmAttempts);
            string? raw;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(IngestBudget);
                raw = await _llm.CompleteAsync(
                    AgentPrompts.IngestSystem,
                    AgentPrompts.IngestUser(content),
                    maxTokens: IngestMaxTokens,
                    jsonMode: true,
                    cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A per-attempt timeout (the linked cts, not ct) or a transport blip lands here
                // and simply burns one attempt — the loop re-rolls.
                Console.Error.WriteLine(
                    $"[scrinia-bench] agent:ingest attempt {attempt}/{_maxAttempts} failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
                raw = null;
            }

            var parsed = AgentPrompts.ParseIngest(raw);
            if (parsed is not null)
            {
                // Cache the raw reply (string-only, like the other cached ops) so reruns replay
                // the parse without a re-call, and a prompt-parser tweak can re-derive them.
                _cache.PutString(IngestOp, hash, raw);
                Interlocked.Increment(ref _produced);
                return parsed;
            }
        }

        // Exhausted every attempt without a usable reply. Deliberately NOT cached — a rerun
        // gets fresh attempts. Only log when retries are enabled (maxAttempts > 1): with
        // retries off, an occasional empty reply is expected and logging every one just spams
        // the console; when the user opted into retries, a doc still failing is worth surfacing.
        Interlocked.Increment(ref _failed);
        if (_maxAttempts > 1)
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:ingest empty/unparseable for hash {hash[..8]} after {_maxAttempts} attempt(s); no attributes/keywords stored for this doc.");
        return null;
    }
}

/// <summary>
/// Whole-run ingest tally for <see cref="LlmAgentHarness"/>. Invariants:
/// <c>Seen == EmptyContent + CacheHits + LlmDocs</c>,
/// <c>Produced == CacheHits + (LlmDocs - Failed)</c>, and this run appended exactly
/// <c>LlmDocs - Failed</c> lines to the response cache. <see cref="Failed"/> is the number
/// of docs that reached the model but yielded no usable attributes/keywords — <c>0</c> means
/// every doc that was sent parsed cleanly (nothing silently dropped on the default-silent empty path).
/// </summary>
public sealed record AgentIngestStats(
    long Seen,
    long EmptyContent,
    long CacheHits,
    long LlmDocs,
    long LlmAttempts,
    long Produced,
    long Failed);
