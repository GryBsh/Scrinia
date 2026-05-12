using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Resilience;

namespace Scrinia.Server.Chat.Providers;

/// <summary>OpenAI chat provider. Messages pass through natively (OpenAI format is the internal format).</summary>
public sealed class OpenAiChatProvider : IChatProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;

    public string Name => "openai";

    public OpenAiChatProvider(string apiKey, string model, string baseUrl, int maxTokens, double temperature,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();
    }

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatMessage[] messages, AgentToolDef[] tools, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestMessages = messages.Select(TranslateMessage).ToArray();
        var requestTools = tools.Select(t => new OpenAiTool("function",
            new OpenAiFunction(t.Name, t.Description, t.Parameters))).ToArray();

        var request = new OpenAiRequest(_model, requestMessages, true, _maxTokens, _temperature,
            requestTools.Length > 0 ? requestTools : null);

        var jsonBody = JsonSerializer.Serialize(request, OpenAiChatJsonContext.Default.OpenAiRequest);

        HttpResponseMessage response;
        string? circuitError = null;
        try
        {
            _circuitBreaker.EnsureClosed();
            response = await RetryPolicy.ExecuteAsync(
                async () =>
                {
                    var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                    httpReq.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    return await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
                },
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                logger: null,
                ct);
        }
        catch (CircuitBreakerOpenException ex)
        {
            circuitError = ex.Message;
            response = null!;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw; // Let it propagate to ChatEndpoints catch-all
        }

        if (circuitError is not null)
        {
            yield return new ChatEvent("error", Error: circuitError);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (TransientDetector.IsTransient(response))
                    _circuitBreaker.RecordFailure();
                string body = "";
                try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
                var detail = body.Length > 500 ? body[..500] + "..." : body;
                yield return new ChatEvent("error", Error: $"Provider returned {(int)response.StatusCode}: {detail}");
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;

                string data = line["data: ".Length..];
                if (data == "[DONE]") break;

                OpenAiChunk? chunk;
                try { chunk = JsonSerializer.Deserialize(data, OpenAiChatJsonContext.Default.OpenAiChunk); }
                catch (JsonException) { continue; } // malformed chunk — skip but data: prefix already filtered non-JSON

                var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
                if (delta is null) continue;

                // Text content
                if (!string.IsNullOrEmpty(delta.Content))
                    yield return new ChatEvent("chunk", Content: delta.Content);

                // Tool calls (index-based accumulation)
                if (delta.ToolCalls is { Length: > 0 })
                {
                    foreach (var tc in delta.ToolCalls)
                    {
                        if (!toolCalls.ContainsKey(tc.Index))
                            toolCalls[tc.Index] = (tc.Id ?? $"call_{tc.Index}", tc.Function?.Name ?? "", new StringBuilder());

                        var entry = toolCalls[tc.Index];
                        if (tc.Function?.Name is not null && entry.Name == "")
                            toolCalls[tc.Index] = (entry.Id, tc.Function.Name, entry.Args);
                        if (tc.Function?.Arguments is not null)
                            entry.Args.Append(tc.Function.Arguments);
                    }
                }
            }

            // Emit accumulated tool calls
            foreach (var (_, (id, name, args)) in toolCalls)
                yield return new ChatEvent("tool-call", ToolName: name, ToolCallId: id,
                    Content: args.ToString());

            _circuitBreaker.RecordSuccess();
            yield return new ChatEvent("done");
        }
    }

    private static OpenAiMessage TranslateMessage(ChatMessage msg) => msg.Role switch
    {
        "tool" => new OpenAiMessage("tool", msg.Content, null, msg.ToolCallId),
        "assistant" when msg.ToolCalls is { Length: > 0 } =>
            new OpenAiMessage("assistant", msg.Content,
                msg.ToolCalls.Select(tc => new OpenAiToolCall(tc.Id, "function",
                    new OpenAiFunctionCall(tc.Name, tc.Arguments))).ToArray(), null),
        _ => new OpenAiMessage(msg.Role, msg.Content, null, null),
    };

    public void Dispose() => _http.Dispose();
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record OpenAiRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] OpenAiMessage[] Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("tools")] OpenAiTool[]? Tools);

internal sealed record OpenAiMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] OpenAiToolCall[]? ToolCalls,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

internal sealed record OpenAiTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiFunction Function);

internal sealed record OpenAiFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object Parameters);

internal sealed record OpenAiToolCall(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiFunctionCall Function);

internal sealed record OpenAiFunctionCall(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments);

internal sealed record OpenAiChunk(
    [property: JsonPropertyName("choices")] OpenAiChoice[]? Choices);

internal sealed record OpenAiChoice(
    [property: JsonPropertyName("delta")] OpenAiDelta? Delta);

internal sealed record OpenAiDelta(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] OpenAiDeltaToolCall[]? ToolCalls);

internal sealed record OpenAiDeltaToolCall(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("function")] OpenAiFunctionCall? Function);

[JsonSerializable(typeof(OpenAiRequest))]
[JsonSerializable(typeof(OpenAiChunk))]
internal sealed partial class OpenAiChatJsonContext : JsonSerializerContext;
