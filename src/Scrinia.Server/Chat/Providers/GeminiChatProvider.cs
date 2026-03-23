using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Resilience;

namespace Scrinia.Server.Chat.Providers;

/// <summary>Google Gemini chat provider. Translates to/from Gemini generateContent format.</summary>
public sealed class GeminiChatProvider : IChatProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;

    public string Name => "gemini";

    public GeminiChatProvider(string apiKey, string model, string baseUrl, int maxTokens, double temperature,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
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
        // Extract system instruction
        string? system = messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var contents = messages.Where(m => m.Role != "system").Select(TranslateMessage).ToArray();

        var toolDecls = tools.Select(t => new GeminiFunction(t.Name, t.Description, t.Parameters)).ToArray();
        var geminiTools = toolDecls.Length > 0
            ? new[] { new GeminiToolDef(toolDecls) }
            : null;

        var request = new GeminiRequest(
            contents,
            system is not null ? new GeminiSystemInstruction([new GeminiPart(system, null, null)]) : null,
            geminiTools,
            new GeminiGenerationConfig(_maxTokens, _temperature));

        var jsonBody = JsonSerializer.Serialize(request, GeminiChatJsonContext.Default.GeminiRequest);
        string url = $"v1beta/models/{_model}:streamGenerateContent?alt=sse";

        HttpResponseMessage response;
        string? circuitError = null;
        try
        {
            _circuitBreaker.EnsureClosed();
            response = await RetryPolicy.ExecuteAsync(
                async () =>
                {
                    var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
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

            int toolIndex = 0;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;

                string data = line["data: ".Length..];
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; } // malformed SSE chunk — skip

                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("candidates", out var candidates)) continue;
                    var candidate = candidates.EnumerateArray().FirstOrDefault();
                    if (candidate.ValueKind == JsonValueKind.Undefined) continue;

                    if (!candidate.TryGetProperty("content", out var content)) continue;
                    if (!content.TryGetProperty("parts", out var parts)) continue;

                    foreach (var part in parts.EnumerateArray())
                    {
                        // Text chunk
                        if (part.TryGetProperty("text", out var text))
                        {
                            string? t = text.GetString();
                            if (!string.IsNullOrEmpty(t))
                                yield return new ChatEvent("chunk", Content: t);
                        }

                        // Function call
                        if (part.TryGetProperty("functionCall", out var fc))
                        {
                            string? name = fc.TryGetProperty("name", out var n) ? n.GetString() : null;
                            string args = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";

                            yield return new ChatEvent("tool-call", ToolName: name,
                                ToolCallId: $"call_{toolIndex++}", Content: args);
                        }
                    }
                }
            }

            _circuitBreaker.RecordSuccess();
            yield return new ChatEvent("done");
        }
    }

    private static GeminiContent TranslateMessage(ChatMessage msg)
    {
        string role = msg.Role == "assistant" ? "model" : "user";

        // Tool results are sent as user messages with functionResponse parts
        if (msg.Role == "tool")
        {
            var responsePart = new GeminiPart(null,
                null,
                new GeminiFunctionResponse(msg.ToolCallId ?? "unknown",
                    new GeminiFunctionResponseBody(msg.Content ?? "")));
            return new GeminiContent("user", [responsePart]);
        }

        var parts = new List<GeminiPart>();

        if (!string.IsNullOrEmpty(msg.Content))
            parts.Add(new GeminiPart(msg.Content, null, null));

        if (msg.ToolCalls is { Length: > 0 })
        {
            foreach (var tc in msg.ToolCalls)
            {
                object args;
                try { args = JsonSerializer.Deserialize<JsonElement>(tc.Arguments); }
                catch { args = new { }; }
                parts.Add(new GeminiPart(null, new GeminiFunctionCall(tc.Name, args), null));
            }
        }

        return new GeminiContent(role, parts.ToArray());
    }

    public void Dispose() => _http.Dispose();
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record GeminiRequest(
    [property: JsonPropertyName("contents")] GeminiContent[] Contents,
    [property: JsonPropertyName("systemInstruction")] GeminiSystemInstruction? SystemInstruction,
    [property: JsonPropertyName("tools")] GeminiToolDef[]? Tools,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig? GenerationConfig);

internal sealed record GeminiContent(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("parts")] GeminiPart[] Parts);

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("functionCall")] GeminiFunctionCall? FunctionCall,
    [property: JsonPropertyName("functionResponse")] GeminiFunctionResponse? FunctionResponse);

internal sealed record GeminiFunctionCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("args")] object Args);

internal sealed record GeminiFunctionResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("response")] GeminiFunctionResponseBody Response);

internal sealed record GeminiFunctionResponseBody(
    [property: JsonPropertyName("result")] string Result);

internal sealed record GeminiSystemInstruction(
    [property: JsonPropertyName("parts")] GeminiPart[] Parts);

internal sealed record GeminiToolDef(
    [property: JsonPropertyName("functionDeclarations")] GeminiFunction[] FunctionDeclarations);

internal sealed record GeminiFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object Parameters);

internal sealed record GeminiGenerationConfig(
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("temperature")] double Temperature);

[JsonSerializable(typeof(GeminiRequest))]
internal partial class GeminiChatJsonContext : JsonSerializerContext;
