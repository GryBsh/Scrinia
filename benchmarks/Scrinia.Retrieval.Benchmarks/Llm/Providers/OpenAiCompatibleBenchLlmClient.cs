using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Agent;

namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// <see cref="IBenchLlmClient"/> implementation that posts to an OpenAI-compatible
/// chat-completions endpoint. Works with Ollama, llama.cpp server, LM Studio,
/// Docker Model Runner, vLLM, OpenAI itself, Azure OpenAI, or any other server
/// that speaks the chat-completions schema.
///
/// <para>Construct via <see cref="Create"/> in production (manages its own
/// <see cref="HttpClient"/> lifetime). Tests inject a stubbed <see cref="HttpClient"/>
/// via the internal constructor to mock <c>HttpMessageHandler</c>.</para>
/// </summary>
public sealed class OpenAiCompatibleBenchLlmClient : IBenchLlmClient, IToolCallingClient, IDisposable
{
    private readonly LlmOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Factory for production use. Creates a self-owned <see cref="HttpClient"/>
    /// with an infinite timeout. Allowing enough time for rich memory read/write and reasoning to complete.
    /// </summary>
    public static OpenAiCompatibleBenchLlmClient Create(LlmOptions options)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAiCompatibleBenchLlmClient(options, http, ownsHttp: true);
    }

    /// <summary>
    /// Test-friendly constructor. Caller provides the <see cref="HttpClient"/>
    /// and controls its lifetime. Used in unit tests with a mocked
    /// <see cref="HttpMessageHandler"/>.
    /// </summary>
    internal OpenAiCompatibleBenchLlmClient(LlmOptions options, HttpClient http, bool ownsHttp = false)
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

    public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct)
    {
        var request = new ChatRequest(
            Model: _options.Model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            MaxTokens: maxTokens,
            Temperature: _options.Temperature,
            // Wire-level JSON-object output. Supported by Ollama, llama.cpp server,
            // LM Studio, vLLM, OpenAI itself. Servers that don't recognize the field
            // ignore it harmlessly — prompt-only discipline applies in that case.
            ResponseFormat: jsonMode ? new ResponseFormat("json_object") : null);
        return SendChatAsync(request, ct);
    }

    /// <summary>
    /// Posts <paramref name="request"/> to the chat-completions endpoint and returns the
    /// model's response content string (trimmed) or null on any failure the caller
    /// should treat as skip-and-continue: non-success status, network error, timeout,
    /// or JSON-deserialization failure of the response envelope. The 4xx/5xx body is
    /// echoed to stderr so misconfigurations are debuggable without strace.
    /// </summary>
    private async Task<string?> SendChatAsync(ChatRequest request, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"))
            {
                Content = JsonContent.Create(request, LlmJsonContext.Default.ChatRequest),
            };
            ApplyAuth(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody;
                try { errBody = await resp.Content.ReadAsStringAsync(ct); }
                catch { errBody = "<unreadable>"; }
                if (errBody.Length > 500) errBody = errBody[..500] + "...";
                Console.Error.WriteLine(
                    $"[scrinia-bench:llm] {_options.Provider}/{_options.Model} POST {req.RequestUri} -> " +
                    $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody}");
                return null;
            }

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
    /// Posts a tool-calling turn to the chat-completions endpoint. Returns the assistant's
    /// content, any tool calls it requested, and the raw finish reason. Returns null ONLY
    /// on transport failure, timeout, or an unusable response (skip-and-continue, same
    /// contract as <see cref="CompleteAsync"/>) — the cause is logged to stderr first,
    /// since silent swallowing is not acceptable for this path.
    /// </summary>
    public async Task<ToolTurnResult?> ChatWithToolsAsync(
        IReadOnlyList<ToolChatMessage> messages, IReadOnlyList<ToolSpec> tools,
        int maxTokens, CancellationToken ct)
    {
        try
        {
            var wireMessages = messages.Select(ToWire).ToArray();
            var wireTools = tools.Select(t => new ToolDefinition(
                "function",
                new ToolFunctionSpec(t.Name, t.Description,
                    JsonDocument.Parse(t.JsonSchemaParameters).RootElement))).ToArray();
            var request = new ToolChatRequest(
                _options.Model, wireMessages, wireTools, "auto", maxTokens, _options.Temperature);

            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"))
            {
                Content = JsonContent.Create(request, LlmJsonContext.Default.ToolChatRequest),
            };
            ApplyAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody;
                try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { errBody = "<unreadable>"; }
                if (errBody.Length > 500) errBody = errBody[..500] + "...";
                Console.Error.WriteLine(
                    $"[scrinia-bench:llm] {_options.Provider}/{_options.Model} tools POST {req.RequestUri} -> " +
                    $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody}");
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync(LlmJsonContext.Default.ToolChatResponse, ct);
            var choice = body?.Choices is { Length: > 0 } ? body.Choices[0] : null;
            if (choice is null)
            {
                Console.Error.WriteLine($"[scrinia-bench:llm] {_options.Provider}/{_options.Model} tools: empty choices");
                return null;
            }
            var calls = (choice.Message.ToolCalls ?? [])
                .Where(c => c.Function is not null)
                .Select(c => new ToolCallRequest(c.Id, c.Function.Name, c.Function.Arguments))
                .ToList();
            return new ToolTurnResult(choice.Message.Content, calls, choice.FinishReason);
        }
        catch (HttpRequestException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools transport: {ex.Message}"); return null; }
        catch (TaskCanceledException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools timeout/cancel: {ex.Message}"); return null; }
        catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"[scrinia-bench:llm] tools json: {ex.Message}"); return null; }
    }

    private static ToolWireMessage ToWire(ToolChatMessage m) =>
        new(m.Role, m.Content,
            m.ToolCalls?.Select(tc => new ToolCall(
                tc.Id, "function", new ToolCallFunction(tc.ToolName, tc.ArgumentsJson))).ToArray(),
            m.ToolCallId);

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
