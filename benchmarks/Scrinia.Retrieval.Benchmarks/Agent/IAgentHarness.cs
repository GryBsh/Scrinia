namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// Bench-side simulation of the production agent that curates memories as they are
/// ingested. Real users store memories through an agent that, given a memory's
/// content, supplies the <c>remember</c> tool a typed <c>attributes</c> map and
/// freeform <c>keywords</c>. The bench wires an <see cref="IAgentHarness"/> in front
/// of <c>ScriniaRetriever</c> so the published benchmarks measure that same
/// "retrieval + agent curation" surface end-to-end.
///
/// <para>The single op is opt-in via the CLI's bare <c>--agent</c> flag. When off,
/// the <see cref="NoOpAgentHarness"/> is wired and ingestion is purely mechanical.</para>
///
/// <para><b>Anti-data-leakage contract:</b> implementations MUST send only the
/// document content to the LLM. They MUST NOT receive the gold answer set, the
/// question dataset, or any other ground-truth signal. The whole point is to
/// simulate what a production agent could do given only what the user stored —
/// leaking gold answers would invalidate the measurement.</para>
/// </summary>
public interface IAgentHarness
{
    /// <summary>
    /// Generates the agent's ingest output for one doc — a typed
    /// <c>attributes</c> map and freeform <c>keywords</c>, exactly what a production
    /// agent hands the <c>remember</c> tool. The retriever merges the keywords with
    /// its mechanically-extracted keywords and writes the attributes to the entry's
    /// attribute map.
    /// <para>Null when the agent is disabled, the LLM call fails, or the output fails
    /// to parse — the retriever then falls back to purely mechanical ingestion. Null
    /// is a soft signal, not an error: the bench keeps running.</para>
    /// </summary>
    Task<AgentIngest?> GenerateIngestAsync(string content, CancellationToken ct);
}

/// <summary>
/// The attributes map and keywords an agent supplies at ingest, mirroring the <c>remember</c>
/// tool's <c>attributes</c> + <c>keywords</c> arguments. <see cref="Attributes"/> is a
/// typed facet map (e.g. <c>person → [maria]</c>); <see cref="Keywords"/> are freeform
/// search terms. Either may be empty, but a non-null result always carries at least one.
/// </summary>
public sealed record AgentIngest(
    IReadOnlyDictionary<string, string[]> Attributes,
    IReadOnlyList<string> Keywords);
