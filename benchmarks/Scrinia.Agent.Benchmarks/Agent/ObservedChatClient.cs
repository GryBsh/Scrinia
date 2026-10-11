using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed class ObservedChatClient(IChatClient inner, ModelSettings settings, RunLimits limits,
    IRunEvents events, string phase, string? expectedModelId = null) : DelegatingChatClient(inner)
{
    private bool _observedIdentity;
    private readonly EpisodeBudget _budget = new(limits.MaxModelTurnsPerEpisode);
    private long? _inputTokens = 0;
    private long? _outputTokens = 0;
    public int ModelRequests { get; private set; }
    public UsageTotals Usage => new(_inputTokens, _outputTokens);
    public string? ReportedModelId { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        var actual = options?.Clone() ?? new ChatOptions();
        actual.ModelId = settings.Model;
        actual.Temperature = settings.Temperature;
        actual.MaxOutputTokens = settings.MaxOutputTokens;
        actual.ConversationId = null;
        actual.RawRepresentationFactory = null;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.BeforeModelRequest();
            await events.WriteAsync(phase, "model-request", ChatEvidence.Build(w =>
            {
                w.WriteNumber("request", ModelRequests + 1);
                w.WriteNumber("attempt", attempt);
                w.WriteString("model", settings.Model);
                w.WriteNumber("temperature", settings.Temperature);
                w.WriteNumber("maxOutputTokens", settings.MaxOutputTokens);
                w.WriteString("instructions", actual.Instructions);
                w.WritePropertyName("messages");
                ChatEvidence.WriteMessages(w, history);
            }), cancellationToken);
            ModelRequests++;
            long started = Stopwatch.GetTimestamp();
            ChatResponse response;
            try { response = await base.GetResponseAsync(history, actual, cancellationToken); }
            catch (Exception error)
            {
                // A failed request has no authoritative usage; do not invent zero cost.
                _inputTokens = null;
                _outputTokens = null;
                int? status = error switch
                {
                    ClientResultException result => result.Status,
                    HttpRequestException http when http.StatusCode is not null => (int)http.StatusCode,
                    _ => null
                };
                bool retry = status is 408 or 429 or 500 or 502 or 503 or 504 && attempt < limits.MaxModelAttempts;
                await events.WriteAsync(phase, "model-error", ChatEvidence.Build(w =>
                {
                    w.WriteNumber("request", ModelRequests);
                    w.WriteString("errorType", error.GetType().Name);
                    if (status is { } code) w.WriteNumber("status", code);
                    w.WriteBoolean("retry", retry);
                    w.WriteNumber("durationMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }), CancellationToken.None);
                if (!retry) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 200 * attempt)), cancellationToken);
                continue;
            }
            _inputTokens = AddUsage(_inputTokens, response.Usage?.InputTokenCount);
            _outputTokens = AddUsage(_outputTokens, response.Usage?.OutputTokenCount);
            string identity = response.ModelId ?? "unavailable";
            await events.WriteAsync(phase, "model-response", ChatEvidence.Build(w =>
            {
                w.WriteNumber("request", ModelRequests);
                w.WriteString("reportedModel", response.ModelId);
                w.WriteString("finishReason", response.FinishReason?.Value);
                w.WriteNumber("durationMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                w.WritePropertyName("usage");
                JsonSerializer.Serialize(w, new UsageTotals(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount), RecordJsonContext.Default.UsageTotals);
                w.WritePropertyName("messages");
                ChatEvidence.WriteMessages(w, response.Messages);
            }), cancellationToken);
            if ((expectedModelId is not null && identity != expectedModelId) ||
                (_observedIdentity && identity != (ReportedModelId ?? "unavailable")))
                throw new BenchmarkFailureException(BenchmarkFailureCode.MODEL_DRIFT, "Reported model identity changed before tool dispatch.");
            ReportedModelId = response.ModelId;
            _observedIdentity = true;
            if (response.FinishReason != ChatFinishReason.Stop && response.FinishReason != ChatFinishReason.ToolCalls)
                throw new InvalidDataException("Model response did not finish normally; no tool calls were dispatched.");
            if (response.Messages.Count == 0 || response.Messages.SelectMany(m => m.Contents).Any(c => c is FunctionCallContent f &&
                (string.IsNullOrWhiteSpace(f.CallId) || string.IsNullOrWhiteSpace(f.Name))))
                throw new InvalidDataException("Model response is malformed.");
            // Always send full in-process history, never a provider-managed conversation handle.
            response.ConversationId = null;
            return response;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Benchmark evidence requires complete, nonstreaming responses.");

    private static long? AddUsage(long? total, long? value) => total is not null && value is >= 0 ? checked(total + value) : null;
}

internal static class ChatEvidence
{
    internal static JsonElement Build(Action<Utf8JsonWriter> contents)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            contents(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }

    internal static void WriteMessages(Utf8JsonWriter writer, IEnumerable<ChatMessage> messages)
    {
        writer.WriteStartArray();
        foreach (var message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", message.Role.Value);
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var content in message.Contents) WriteContent(writer, content);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static void WriteContent(Utf8JsonWriter writer, AIContent content)
    {
        writer.WriteStartObject();
        switch (content)
        {
            case TextContent text:
                writer.WriteString("kind", "text"); writer.WriteString("text", text.Text); break;
            case FunctionCallContent call:
                writer.WriteString("kind", "function-call"); writer.WriteString("id", call.CallId); writer.WriteString("name", call.Name);
                writer.WritePropertyName("arguments"); WriteValue(writer, call.Arguments); break;
            case FunctionResultContent result:
                writer.WriteString("kind", "function-result"); writer.WriteString("id", result.CallId);
                writer.WritePropertyName("result"); WriteValue(writer, result.Result); break;
            case TextReasoningContent reasoning:
                writer.WriteString("kind", "reasoning"); writer.WriteString("text", reasoning.Text); break;
            default:
                writer.WriteString("kind", content.GetType().Name); break;
        }
        writer.WriteEndObject();
    }

    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case System.Text.Json.Nodes.JsonNode node: node.WriteTo(writer); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case AIContent content: WriteContent(writer, content); break;
            case IEnumerable<KeyValuePair<string, object?>> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary.OrderBy(p => p.Key, StringComparer.Ordinal))
                { writer.WritePropertyName(key); WriteValue(writer, item); }
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default: throw new InvalidDataException("Unsupported tool value in benchmark evidence.");
        }
    }

    internal static bool IsToolError(object? result) => result switch
    {
        TextContent text => text.Text.TrimStart().StartsWith("status: error", StringComparison.Ordinal),
        IEnumerable<AIContent> contents => contents.Any(IsToolError),
        JsonElement element when element.ValueKind == JsonValueKind.Object =>
            (element.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) ||
            (element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "error") ||
            (element.TryGetProperty("structuredContent", out var structured) && IsToolError(structured)) ||
            (element.TryGetProperty("content", out var content) && IsToolError(content)) ||
            (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                text.GetString()!.TrimStart().StartsWith("status: error", StringComparison.Ordinal)),
        JsonElement element when element.ValueKind == JsonValueKind.Array => element.EnumerateArray().Any(e => IsToolError(e)),
        _ => false
    };
}
