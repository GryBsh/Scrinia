namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// Narrow chat-completion surface the bench's agent harness consumes. Each concrete
/// implementation wraps one provider's transport (OpenAI-compat HTTP, Anthropic Messages,
/// Gemini generateContent, or an agent-CLI subprocess) and exposes a single generic
/// <see cref="CompleteAsync"/> primitive — the harness owns its own prompts and parsers
/// so adding a new agent op is a bench-only change, not a provider rebuild.
///
/// <para>Operations are tolerant of failure. Implementations return null on timeout,
/// non-2xx, parse failure, or any other transport-level issue the caller should treat
/// as "skip and continue". Callers (the agent harness) layer their own per-op timeout
/// budgets via cancellation tokens.</para>
/// </summary>
public interface IBenchLlmClient
{
    /// <summary>
    /// Cheap availability probe. True when the backend is reachable AND a model is
    /// loaded (or, for agent CLIs, the binary is on PATH). Used by the bench's
    /// agent-builder to fail fast on misconfiguration before the run starts.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Single-turn chat completion: send <paramref name="systemPrompt"/> + <paramref name="userPrompt"/>,
    /// receive the model's reply as plain text. Returns null on any failure the caller
    /// should treat as "skip this op and continue" (timeout, non-2xx, parse failure,
    /// empty response).
    ///
    /// <para><paramref name="jsonMode"/> requests wire-level JSON-object output on
    /// backends that support it (OpenAI-compat <c>response_format: {type: "json_object"}</c>,
    /// Gemini <c>responseMimeType: application/json</c>). Backends with no wire-level JSON
    /// mode (Anthropic Messages, agent CLIs) ignore the flag — JSON discipline falls to
    /// the system prompt in that case.</para>
    /// </summary>
    /// <param name="systemPrompt">Instruction-tier text. Backends that natively model a
    /// system role (OpenAI-compat, Anthropic, Gemini) place it there. Agent CLIs prepend
    /// it to the user prompt as a leading paragraph.</param>
    /// <param name="userPrompt">User-tier content the system prompt operates on.</param>
    /// <param name="maxTokens">Upper bound on response tokens. Forwarded to the backend
    /// as <c>max_tokens</c> / <c>max_output_tokens</c>; ignored by agent CLIs.</param>
    /// <param name="jsonMode">Request wire-level JSON-object output where supported. See
    /// remarks above for per-backend semantics.</param>
    /// <param name="ct">Per-call cancellation budget; if it fires, return null.</param>
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct);
}
