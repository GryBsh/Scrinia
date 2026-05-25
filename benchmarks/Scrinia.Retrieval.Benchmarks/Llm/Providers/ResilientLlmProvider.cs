namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// Shared boilerplate for HTTP-backed <see cref="IBenchLlmClient"/> implementations
/// (Anthropic Messages API, Gemini generateContent). Owns the <see cref="HttpClient"/>
/// and the <see cref="LlmOptions"/> reference; concrete providers express request
/// shape and response parsing.
///
/// <para>Modeled on <c>ResilientEmbeddingProvider</c> but lighter — agent harness calls
/// aren't on the search hot path, so we skip circuit-breaker / retry-policy ceremony.
/// A flapping endpoint surfaces as null per call; the agent harness already tolerates
/// this and continues the run.</para>
/// </summary>
public abstract class ResilientLlmProvider : IBenchLlmClient, IDisposable
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

    /// <summary>
    /// Concrete providers send their native request shape and extract the model's text
    /// reply from the response. Returns null for any failure that should be treated as
    /// "skip this op" (timeout, non-2xx, malformed JSON, empty content).
    /// </summary>
    public abstract Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct);

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
