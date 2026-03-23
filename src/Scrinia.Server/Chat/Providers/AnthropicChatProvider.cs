using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Resilience;

namespace Scrinia.Server.Chat.Providers;

/// <summary>Anthropic chat provider. Translates to/from Anthropic Messages API format.</summary>
public sealed class AnthropicChatProvider : IChatProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;

    public string Name => "anthropic";

    public AnthropicChatProvider(string apiKey, string model, string baseUrl, int maxTokens, double temperature,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
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
        // Extract system message
        string? system = messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var apiMessages = messages.Where(m => m.Role != "system").Select(TranslateMessage).ToArray();
        var apiTools = tools.Select(t => new AnthropicTool(t.Name, t.Description, t.Parameters)).ToArray();

        var request = new AnthropicRequest(_model, apiMessages, _maxTokens, system, true, _temperature,
            apiTools.Length > 0 ? apiTools : null);

        var jsonBody = JsonSerializer.Serialize(request, AnthropicChatJsonContext.Default.AnthropicRequest);

        HttpResponseMessage response;
        string? circuitError = null;
        try
        {
            _circuitBreaker.EnsureClosed();
            response = await RetryPolicy.ExecuteAsync(
                async () =>
                {
                    var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
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

            string? currentToolId = null;
            string? currentToolName = null;
            var currentToolArgs = new StringBuilder();

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;

                string data = line["data: ".Length..];
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; } // malformed SSE chunk — skip

                using (doc)
                {
                    string? eventType = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;

                    switch (eventType)
                    {
                        case "content_block_start":
                            if (doc.RootElement.TryGetProperty("content_block", out var block))
                            {
                                string? blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                                if (blockType == "tool_use")
                                {
                                    // Emit previous tool if any
                                    if (currentToolId is not null)
                                        yield return new ChatEvent("tool-call", ToolName: currentToolName,
                                            ToolCallId: currentToolId, Content: currentToolArgs.ToString());

                                    currentToolId = block.TryGetProperty("id", out var id) ? id.GetString() : null;
                                    currentToolName = block.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                                    currentToolArgs.Clear();
                                }
                            }
                            break;

                        case "content_block_delta":
                            if (doc.RootElement.TryGetProperty("delta", out var delta))
                            {
                                string? deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                                if (deltaType == "text_delta")
                                {
                                    string? text = delta.TryGetProperty("text", out var t) ? t.GetString() : null;
                                    if (!string.IsNullOrEmpty(text))
                                        yield return new ChatEvent("chunk", Content: text);
                                }
                                else if (deltaType == "input_json_delta")
                                {
                                    string? partialJson = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() : null;
                                    if (partialJson is not null)
                                        currentToolArgs.Append(partialJson);
                                }
                            }
                            break;

                        case "message_stop":
                            // Emit final tool if pending
                            if (currentToolId is not null)
                            {
                                yield return new ChatEvent("tool-call", ToolName: currentToolName,
                                    ToolCallId: currentToolId, Content: currentToolArgs.ToString());
                                currentToolId = null;
                            }
                            break;
                    }
                }
            }

            _circuitBreaker.RecordSuccess();
            yield return new ChatEvent("done");
        }
    }

    private static AnthropicMessage TranslateMessage(ChatMessage msg)
    {
        if (msg.Role == "assistant" && msg.ToolCalls is { Length: > 0 })
        {
            var content = new List<object>();
            if (!string.IsNullOrEmpty(msg.Content))
                content.Add(new AnthropicTextBlock("text", msg.Content));
            foreach (var tc in msg.ToolCalls)
            {
                object input;
                try { input = JsonSerializer.Deserialize<JsonElement>(tc.Arguments); }
                catch { input = new { }; }
                content.Add(new AnthropicToolUseBlock("tool_use", tc.Id, tc.Name, input));
            }
            return new AnthropicMessage("assistant", content.ToArray());
        }

        if (msg.Role == "tool")
        {
            // Tool results are sent as user messages with tool_result content blocks
            var block = new AnthropicToolResultBlock("tool_result", msg.ToolCallId!, msg.Content ?? "");
            return new AnthropicMessage("user", new object[] { block });
        }

        return new AnthropicMessage(msg.Role, msg.Content ?? "");
    }

    public void Dispose() => _http.Dispose();
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] AnthropicMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("tools")] AnthropicTool[]? Tools);

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object Content); // string or object[]

internal sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] object InputSchema);

internal sealed record AnthropicTextBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record AnthropicToolUseBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] object Input);

internal sealed record AnthropicToolResultBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tool_use_id")] string ToolUseId,
    [property: JsonPropertyName("content")] string Content);

[JsonSerializable(typeof(AnthropicRequest))]
internal partial class AnthropicChatJsonContext : JsonSerializerContext;
