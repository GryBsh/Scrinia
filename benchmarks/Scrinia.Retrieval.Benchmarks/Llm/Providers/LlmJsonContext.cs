using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// <summary>
/// Wire shapes for the OpenAI Chat Completions API subset the bench uses. Source-generated
/// to keep the published CLI trim-clean. Only the fields the bench actually reads/writes
/// are declared — unknown JSON keys are ignored on deserialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ChatErrorResponse))]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(ResponseFormat))]
[JsonSerializable(typeof(ToolChatRequest))]
[JsonSerializable(typeof(ToolChatResponse))]
internal partial class LlmJsonContext : JsonSerializerContext;

internal sealed record ChatMessage(string Role, string Content);

/// <summary>
/// OpenAI-compatible <c>response_format</c> directive. Set <see cref="Type"/> to
/// <c>"json_object"</c> to constrain the server to return a syntactically valid JSON
/// object. Supported by Ollama, llama.cpp server, LM Studio, vLLM, and OpenAI itself.
/// Servers that don't recognize the field ignore it harmlessly.
/// </summary>
internal sealed record ResponseFormat([property: JsonPropertyName("type")] string Type);

internal sealed record ChatRequest(
    string Model,
    ChatMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    double? Temperature,
    [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat = null,
    bool Stream = false);

internal sealed record ChatResponseChoice(int Index, ChatMessage Message, string? FinishReason);

internal sealed record ChatResponse(string? Id, string? Model, ChatResponseChoice[]? Choices);

internal sealed record ChatErrorBody(string? Message, string? Type, string? Code);

internal sealed record ChatErrorResponse(ChatErrorBody? Error);

internal sealed record ModelInfo(string Id);

internal sealed record ModelsResponse(ModelInfo[]? Data);

// Tool-calling wire shapes. Free-form JSON (tool parameter schemas, tool-call arguments)
// is represented as JsonElement so the source-generated context stays closed over known
// types even though the schema/argument shapes are model- and tool-defined.

internal sealed record ToolFunctionSpec(
    string Name, string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record ToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolFunctionSpec Function);

internal sealed record ToolCallFunction(string Name, string Arguments);

internal sealed record ToolCall(
    string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function);

// Extend the existing message/response shapes with tool fields. Because ChatMessage
// is used by CompleteAsync too, add a SEPARATE request/response message record for the
// tools path rather than widening ChatMessage.
internal sealed record ToolWireMessage(
    string Role,
    string? Content,
    [property: JsonPropertyName("tool_calls")] ToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

internal sealed record ToolChatRequest(
    string Model,
    ToolWireMessage[] Messages,
    ToolDefinition[] Tools,
    [property: JsonPropertyName("tool_choice")] string ToolChoice,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    double? Temperature,
    bool Stream = false);

internal sealed record ToolChatResponseChoice(int Index, ToolWireMessage Message, string? FinishReason);
internal sealed record ToolChatResponse(string? Id, string? Model, ToolChatResponseChoice[]? Choices);
