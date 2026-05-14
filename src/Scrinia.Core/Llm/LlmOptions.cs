namespace Scrinia.Core.Llm;

/// <summary>
/// Configuration POCO for the background LLM. Read from <c>Scrinia:Llm:*</c> config keys
/// in <c>WorkspaceSetup</c>. Mirrors <see cref="Embeddings.EmbeddingOptions"/>.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Backend selector. "auto" lets <c>WorkspaceSetup</c> probe for an installed plugin
    /// first, then fall back to the OpenAI-compatible HTTP endpoint. "openai" forces the
    /// HTTP path (covers OpenAI itself, Ollama, llama.cpp server, LM Studio, vLLM, Docker
    /// Model Runner — anything speaking the chat-completions API). "plugin" forces the
    /// bundled subprocess. "none" disables Tier 2.
    /// </summary>
    public string Provider { get; set; } = "auto";

    /// <summary>
    /// Base URL for an OpenAI-compatible chat-completions endpoint. The default matches
    /// Ollama's bundled endpoint; llama.cpp server, LM Studio, and Docker Model Runner
    /// all expose the same surface on different ports.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>Model name passed in the chat-completions request body.</summary>
    public string Model { get; set; } = "lfm2.5:1.2b-thinking";

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
}
