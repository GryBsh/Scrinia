namespace Scrinia.Core.Llm.Providers;

/// <summary>
/// Shared boilerplate for HTTP-backed <see cref="IBackgroundLlm"/> implementations
/// (Anthropic Messages API, Gemini generateContent). Owns the <see cref="HttpClient"/>,
/// dispatches per-task prompts from <see cref="LlmPrompts"/> through a common
/// <see cref="CompleteAsync"/>, and standardises error handling so concrete providers
/// only have to express request shape + response parsing.
///
/// <para>Modeled on <c>ResilientEmbeddingProvider</c> but lighter — LLM calls aren't on
/// the search hot path, so we skip circuit-breaker / retry-policy ceremony. A flapping
/// endpoint surfaces as null per call; the caller (<c>LlmConsolidator</c>) already
/// tolerates this and continues the batch.</para>
/// </summary>
public abstract class ResilientLlmProvider : IBackgroundLlm, IDisposable
{
    protected readonly LlmOptions Options;
    protected readonly HttpClient Http;
    private readonly bool _ownsHttp;

    protected ResilientLlmProvider(LlmOptions options, HttpClient http, bool ownsHttp)
    {
        Options = options;
        Http = http;
        _ownsHttp = ownsHttp;
    }

    public abstract Task<bool> IsAvailableAsync(CancellationToken ct);

    public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.DescriptionSystem, LlmPrompts.DescriptionUser(content), maxTokens: 80, ct);

    public Task<string?> SummarizeAsync(string text, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.SummarySystem, LlmPrompts.SummaryUser(text), maxTokens: 320, ct);

    public async Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
    {
        string? raw = await CompleteAsync(LlmPrompts.FactsSystem, LlmPrompts.FactsUser(content), maxTokens: 400, ct);
        if (raw is null) return null;
        string[] parsed = LlmPrompts.ParseFacts(raw);
        return parsed.Length == 0 ? null : parsed;
    }

    /// <summary>
    /// Concrete providers send their native request shape and extract the model's text
    /// reply from the response. Returns null for any failure that should be treated as
    /// "skip this memory" (timeout, non-2xx, malformed JSON, empty content).
    /// </summary>
    protected abstract Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _ownsHttp) Http.Dispose();
    }
}
