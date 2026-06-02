namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Bench-side simulation of the production agent surface around scrinia. Real users
/// call scrinia through an agent that supplies retrieval anchors at ingest and may
/// rewrite queries or generate hypotheticals at search; the bench wires an
/// <see cref="IAgentHarness"/> in front of <c>ScriniaRetriever</c> so the published
/// benchmarks compare apples to the same apples ("retrieval + agent") that LongMemEval,
/// LoCoMo and friends actually measure end-to-end.
///
/// <para>All three operations are independently opt-in via <see cref="AgentModes"/>.
/// Each returns null when the op is disabled, the LLM call fails, or the output
/// fails to parse — the caller falls back to the un-agented path (raw query, no
/// anchors). Null is a soft signal, not an error: the bench keeps running.</para>
///
/// <para><b>Anti-data-leakage contract:</b> implementations MUST send only the
/// document content (for anchors) or only the user query (for rewrite/HyDE) to the
/// LLM. They MUST NOT receive the gold answer set, the question dataset, or any
/// other ground-truth signal. The whole point of the harness is to simulate what a
/// production agent could do given only what the user actually says — leaking gold
/// answers would invalidate the measurement.</para>
/// </summary>
public interface IAgentHarness
{
    /// <summary>
    /// Generates retrieval anchors for one doc at ingest time. Anchors are short
    /// query-vocabulary phrases that a user might search for — they bridge the
    /// vocabulary gap between content and query and feed BM25 via TF seeding plus
    /// the field scorer's anchor-contains signal.
    /// <para>Null when the agent is disabled for anchors or the LLM call fails;
    /// the retriever then falls back to the doc's text-derived term frequencies
    /// only.</para>
    /// </summary>
    Task<IReadOnlyList<string>?> GenerateAnchorsAsync(string content, CancellationToken ct);

    /// <summary>
    /// Rewrites a query before retrieval — typically into a more search-friendly
    /// form with synonyms and category-level terms.
    /// <para>Null when disabled or the call fails; caller falls back to the
    /// original query.</para>
    /// </summary>
    Task<string?> RewriteQueryAsync(string query, CancellationToken ct);

    /// <summary>
    /// Generates a hypothetical document that would answer the query (HyDE). The
    /// caller typically concatenates this with the original query so the embedding-
    /// based retriever sees both surfaces.
    /// <para>Null when disabled or the call fails.</para>
    /// </summary>
    Task<string?> GenerateHypotheticalAsync(string query, CancellationToken ct);

    /// <summary>
    /// Generates typed retrieval keywords (`entity:<name>` / `topic:<theme>`) for one
    /// doc at ingest. These are agent-curated content-descriptor tags whose values are
    /// indexed into BM25 (and whose full `prefix:value` form feeds the field scorer).
    /// <para>Default implementation returns null (op disabled); the LLM-backed harness
    /// overrides it. Null when disabled or the call fails.</para>
    /// </summary>
    Task<IReadOnlyList<string>?> GenerateTypedKeywordsAsync(string content, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}

/// <summary>
/// Per-op feature flags for <see cref="LlmAgentHarness"/>. Set true to enable an op;
/// disabled ops return null from the harness without calling the LLM.
/// </summary>
public readonly record struct AgentModes(bool Anchors, bool Rewrite, bool Hyde, bool TypedKeywords)
{
    public static AgentModes Off { get; } = new(false, false, false, false);
    public static AgentModes AnchorsOnly { get; } = new(true, false, false, false);
    public static AgentModes RewriteOnly { get; } = new(false, true, false, false);
    public static AgentModes HydeOnly { get; } = new(false, false, true, false);
    public static AgentModes TypedKeywordsOnly { get; } = new(false, false, false, true);
    public static AgentModes All { get; } = new(true, true, true, true);

    public bool AnyEnabled => Anchors || Rewrite || Hyde || TypedKeywords;

    public string Label() => (Anchors, Rewrite, Hyde, TypedKeywords) switch
    {
        (false, false, false, false) => "off",
        (true, false, false, false) => "anchors",
        (false, true, false, false) => "rewrite",
        (false, false, true, false) => "hyde",
        (false, false, false, true) => "typed",
        (true, true, true, true) => "all",
        _ => string.Join("+",
            new[]
            {
                Anchors ? "anchors" : null,
                Rewrite ? "rewrite" : null,
                Hyde ? "hyde" : null,
                TypedKeywords ? "typed" : null,
            }.Where(s => s is not null)),
    };

    /// <summary>
    /// Parses an <c>--agent</c> value: <c>off</c>/<c>none</c>/empty → <see cref="Off"/>,
    /// <c>all</c> → <see cref="All"/>, otherwise a comma-separated list of components
    /// (<c>anchors</c>, <c>typed</c>, <c>rewrite</c>, <c>hyde</c>) combined into one mode.
    /// Throws <see cref="ArgumentException"/> on an unknown component so misconfiguration
    /// fails fast at startup rather than silently running the wrong mode.
    /// </summary>
    public static AgentModes Parse(string? raw)
    {
        string s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (s is "" or "off" or "none" or "false") return Off;
        if (s == "all") return All;

        bool anchors = false, rewrite = false, hyde = false, typed = false;
        foreach (string part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case "anchors": anchors = true; break;
                case "rewrite": rewrite = true; break;
                case "hyde": hyde = true; break;
                case "typed" or "typed-keywords" or "keywords": typed = true; break;
                default:
                    throw new ArgumentException(
                        $"unknown --agent component '{part}'. Expected: off | anchors | typed | rewrite | hyde | all " +
                        "(comma-separated combos allowed, e.g. 'anchors,typed').");
            }
        }
        return new AgentModes(anchors, rewrite, hyde, typed);
    }
}
