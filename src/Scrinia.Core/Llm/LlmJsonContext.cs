using System.Text.Json.Serialization;

namespace Scrinia.Core.Llm;

/// <summary>
/// Wire shapes for the OpenAI Chat Completions API subset Scrinia uses. Source-generated
/// to keep the published CLI trim-clean. Only the fields Scrinia actually reads/writes
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
internal partial class LlmJsonContext : JsonSerializerContext;

internal sealed record ChatMessage(string Role, string Content);

/// <summary>
/// OpenAI-compatible <c>response_format</c> directive. Set <see cref="Type"/> to
/// <c>"json_object"</c> to constrain the server to return a syntactically valid JSON
/// object. Supported by Ollama (<c>format: json</c> equivalent), llama.cpp server,
/// LM Studio, vLLM, and OpenAI itself. Servers that don't recognize the field ignore
/// it harmlessly — the prompt then carries the JSON instruction alone, which most
/// modern models honor even without the wire-level constraint.
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
