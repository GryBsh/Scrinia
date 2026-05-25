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
}

/// <summary>
/// Per-op feature flags for <see cref="LlmAgentHarness"/>. Set true to enable an op;
/// disabled ops return null from the harness without calling the LLM.
/// </summary>
public readonly record struct AgentModes(bool Anchors, bool Rewrite, bool Hyde)
{
    public static AgentModes Off { get; } = new(false, false, false);
    public static AgentModes AnchorsOnly { get; } = new(true, false, false);
    public static AgentModes RewriteOnly { get; } = new(false, true, false);
    public static AgentModes HydeOnly { get; } = new(false, false, true);
    public static AgentModes All { get; } = new(true, true, true);

    public bool AnyEnabled => Anchors || Rewrite || Hyde;

    public string Label() => (Anchors, Rewrite, Hyde) switch
    {
        (false, false, false) => "off",
        (true, false, false) => "anchors",
        (false, true, false) => "rewrite",
        (false, false, true) => "hyde",
        (true, true, true) => "all",
        _ => string.Join("+",
            new[] { Anchors ? "anchors" : null, Rewrite ? "rewrite" : null, Hyde ? "hyde" : null }
                .Where(s => s is not null)),
    };
}
