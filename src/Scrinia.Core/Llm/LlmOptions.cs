namespace Scrinia.Core.Llm;

/// <summary>
/// Configuration POCO for the background LLM. Read from <c>Scrinia:Llm:*</c> config keys
/// in <c>WorkspaceSetup</c>. Mirrors <see cref="Embeddings.EmbeddingOptions"/>.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Backend selector. "auto" cycles through HTTP probe → agent-CLI preference order →
    /// bundled plugin. "openai" forces OpenAI-compat HTTP (Ollama, llama.cpp, LM Studio,
    /// vLLM, OpenAI itself). "anthropic" / "gemini" force the corresponding native HTTP
    /// API. "claude-cli" / "codex-cli" / "copilot-cli" force the user's installed agent
    /// CLI (no API key needed — reuses subscription auth). "plugin" forces the bundled
    /// subprocess. "none" disables Tier 2.
    /// </summary>
    public string Provider { get; set; } = "auto";

    /// <summary>
    /// Base URL for an OpenAI-compatible chat-completions endpoint. The default matches
    /// Ollama's bundled endpoint; llama.cpp server, LM Studio, and Docker Model Runner
    /// all expose the same surface on different ports.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>
    /// Model name passed in the chat-completions request body. Default matches
    /// <c>OllamaSetup.DefaultCompletionModel</c> — the LFM2.5-Instruct family on Ollama.
    /// Override with <c>scri config Scrinia:Llm:Model &lt;name&gt;</c>; the thinking variant
    /// is intentionally not the default because it burns the token budget on reasoning
    /// blocks for Tier 2 tasks that want terse output.
    /// </summary>
    public string Model { get; set; } = "lfm2:1.2b";

    /// <summary>Optional API key, sent as <c>Authorization: Bearer ...</c> when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Sampling temperature. Tier 2 favours deterministic output (descriptions and
    /// fact lists need to be reproducible across runs) so the default is low.
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// HTTP request timeout applied per call. Tier 2 sets stricter per-task budgets
    /// via CancellationToken — this is the outer ceiling.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    // ── Anthropic (provider=anthropic) ─────────────────────────────────────
    // Native Messages API at /v1/messages. Auth via x-api-key + anthropic-version header.
    // Uses Options.Model for the model identifier (so switching provider only needs the
    // Provider + ApiKey + Model triple updated).

    public string? AnthropicApiKey { get; set; }
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    // ── Gemini (provider=gemini) ───────────────────────────────────────────
    // Native generateContent at /v1beta/models/{model}:generateContent. Auth via x-goog-api-key.

    public string? GeminiApiKey { get; set; }
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    // ── Agent-CLI overrides (provider=claude-cli / codex-cli / copilot-cli) ──
    // Per-CLI model and effort knobs forwarded to AgentCliBackgroundLlm. Null = use the
    // variant's cheap-tier default (see AgentCliVariant for rationale). Empty string =
    // explicitly inherit the CLI's own default. Otherwise the value is passed verbatim
    // as the corresponding flag value. Codex has no reasoning-effort knob, so there is
    // no CodexCliEffort.

    /// <summary>
    /// Tier 2 model override for Claude Code. Null = variant default (<c>sonnet</c>).
    /// Empty string = inherit the user's Claude Code default model. Otherwise passed
    /// to <c>claude -p</c> as <c>--model &lt;value&gt;</c>. Accepts shorthand
    /// (<c>sonnet</c>, <c>haiku</c>, <c>opus</c>) or full IDs (<c>claude-sonnet-4-6</c>).
    /// </summary>
    public string? ClaudeCliModel { get; set; }

    /// <summary>
    /// Tier 2 model override for Codex. Null = variant default (<c>gpt-5.4-mini</c>).
    /// Empty string = inherit Codex's configured default. Otherwise passed to
    /// <c>codex exec</c> as <c>-m &lt;value&gt;</c>.
    /// </summary>
    public string? CodexCliModel { get; set; }

    /// <summary>
    /// Tier 2 model override for GitHub Copilot. Null = variant default
    /// (<c>gpt-5.4-mini</c>). Empty string = inherit Copilot's configured default.
    /// Otherwise passed to <c>copilot --print</c> as <c>--model &lt;value&gt;</c>.
    /// </summary>
    public string? CopilotCliModel { get; set; }

    /// <summary>
    /// Tier 2 reasoning-effort override for Claude Code. Null = variant default
    /// (<c>low</c>). Empty string = inherit Claude Code's configured effort. Otherwise
    /// passed as <c>--effort &lt;value&gt;</c>. Valid values: <c>low, medium, high,
    /// xhigh, max</c>.
    /// </summary>
    public string? ClaudeCliEffort { get; set; }

    /// <summary>
    /// Tier 2 reasoning-effort override for GitHub Copilot. Null = variant default
    /// (<c>low</c>). Empty string = inherit Copilot's configured effort. Otherwise
    /// passed as <c>--effort &lt;value&gt;</c>. Valid values: <c>low, medium, high,
    /// xhigh</c> (no <c>max</c>).
    /// </summary>
    public string? CopilotCliEffort { get; set; }
}
