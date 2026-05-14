using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Scrinia.Core.Llm;

/// <summary>
/// <see cref="IBackgroundLlm"/> implementation that posts to an OpenAI-compatible
/// chat-completions endpoint. Works with Ollama, llama.cpp server, LM Studio,
/// Docker Model Runner, vLLM, OpenAI itself, Azure OpenAI, or any other server
/// that speaks the chat-completions schema.
///
/// <para>Construct via <see cref="Create"/> in production (manages its own
/// <see cref="HttpClient"/> lifetime). Tests inject a stubbed <see cref="HttpClient"/>
/// via the internal constructor to mock <c>HttpMessageHandler</c>.</para>
/// </summary>
public sealed class OpenAiCompatibleBackgroundLlm : IBackgroundLlm, IDisposable
{
    private readonly LlmOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Factory for production use. Creates a self-owned <see cref="HttpClient"/>
    /// with the request timeout from <paramref name="options"/>.
    /// </summary>
    public static OpenAiCompatibleBackgroundLlm Create(LlmOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        return new OpenAiCompatibleBackgroundLlm(options, http, ownsHttp: true);
    }

    /// <summary>
    /// Test-friendly constructor. Caller provides the <see cref="HttpClient"/>
    /// and controls its lifetime. Used in unit tests with a mocked
    /// <see cref="HttpMessageHandler"/>.
    /// </summary>
    internal OpenAiCompatibleBackgroundLlm(LlmOptions options, HttpClient http, bool ownsHttp = false)
    {
        _options = options;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        // Two-tier probe. The standard OpenAI-compat check is GET {base}/models, which
        // most servers (vLLM, llama.cpp server, LM Studio, OpenAI itself) implement.
        // Ollama supports it too, but only returns 200 when at least one model is pulled —
        // an empty install can return 404 even though POST {base}/chat/completions works.
        // So if the standard probe fails, fall back to a root-of-host check: if the server
        // responds at all there, we trust the user's BaseUrl and let the actual call
        // surface any specific endpoint issue at use time.
        if (await TryProbeAsync(BuildUri("models"), ct)) return true;
        if (TryDeriveRootUri(out var rootUri) && await TryProbeAsync(rootUri, ct)) return true;
        return false;
    }

    private async Task<bool> TryProbeAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private bool TryDeriveRootUri(out Uri rootUri)
    {
        try
        {
            var baseUri = new Uri(_options.BaseUrl);
            rootUri = new Uri($"{baseUri.Scheme}://{baseUri.Authority}/");
            return true;
        }
        catch
        {
            rootUri = null!;
            return false;
        }
    }

    public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.DescriptionSystem, LlmPrompts.DescriptionUser(content),
            maxTokens: 80, ct);

    public Task<string?> SummarizeAsync(string text, CancellationToken ct) =>
        CompleteAsync(LlmPrompts.SummarySystem, LlmPrompts.SummaryUser(text),
            maxTokens: 320, ct);

    public async Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
    {
        string? raw = await CompleteAsync(LlmPrompts.FactsSystem, LlmPrompts.FactsUser(content),
            maxTokens: 400, ct);
        if (raw is null) return null;
        string[] parsed = LlmPrompts.ParseFacts(raw);
        return parsed.Length == 0 ? null : parsed;
    }

    private async Task<string?> CompleteAsync(string systemPrompt, string userPrompt,
        int maxTokens, CancellationToken ct)
    {
        var request = new ChatRequest(
            Model: _options.Model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            MaxTokens: maxTokens,
            Temperature: _options.Temperature);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"))
            {
                Content = JsonContent.Create(request, LlmJsonContext.Default.ChatRequest),
            };
            ApplyAuth(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            ChatResponse? body = await resp.Content.ReadFromJsonAsync(
                LlmJsonContext.Default.ChatResponse, ct);
            string? content = body?.Choices?.Length > 0 ? body.Choices[0].Message.Content : null;
            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Build a request URI by joining the configured <see cref="LlmOptions.BaseUrl"/>
    /// with a relative path. Tolerates trailing or missing slashes on the base.
    /// </summary>
    private Uri BuildUri(string relative)
    {
        string baseUrl = _options.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{relative}");
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
