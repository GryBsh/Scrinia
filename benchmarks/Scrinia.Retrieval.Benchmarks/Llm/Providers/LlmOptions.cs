namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// Configuration POCO for the bench's LLM client backends. Built from explicit
/// <c>--agent-*</c> CLI args by <see cref="Scrinia.Retrieval.Benchmarks.Agent.AgentClientBuilder"/>
/// — the bench deliberately does NOT consult workspace config so measurement runs
/// stay reproducible.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Backend selector. <c>openai</c> / <c>ollama</c> force OpenAI-compat HTTP;
    /// <c>anthropic</c> / <c>gemini</c> force the corresponding native API;
    /// <c>claude-cli</c> / <c>codex-cli</c> / <c>copilot-cli</c> force the user's
    /// installed agent CLI.
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>
    /// Base URL for an OpenAI-compatible chat-completions endpoint. The default matches
    /// Ollama's bundled endpoint; llama.cpp server, LM Studio, and Docker Model Runner
    /// all expose the same surface on different ports.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>
    /// Model name passed in the chat-completions request body.
    /// </summary>
    public string Model { get; set; } = "lfm2:1.2b";

    /// <summary>Optional API key, sent as <c>Authorization: Bearer ...</c> when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Sampling temperature. The bench favours deterministic output (anchors, rewrites
    /// need to be reproducible across runs) so the default is low.
    /// </summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>
    /// HTTP request timeout applied per call. The agent harness sets stricter per-op
    /// budgets via CancellationToken — this is the outer ceiling.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    // Anthropic (provider=anthropic). Native Messages API at /v1/messages.
    public string? AnthropicApiKey { get; set; }
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    // Gemini (provider=gemini). Native generateContent at /v1beta/models/{model}:generateContent.
    public string? GeminiApiKey { get; set; }
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    // Agent-CLI overrides (provider=claude-cli / codex-cli / copilot-cli). Per-CLI
    // model and effort knobs forwarded to AgentCliBenchLlmClient. Null = use the
    // variant's default; empty string = inherit the CLI's own default; otherwise
    // passed verbatim as the corresponding flag value.

    public string? ClaudeCliModel { get; set; }
    public string? CodexCliModel { get; set; }
    public string? CopilotCliModel { get; set; }
    public string? ClaudeCliEffort { get; set; }
    public string? CopilotCliEffort { get; set; }
}
