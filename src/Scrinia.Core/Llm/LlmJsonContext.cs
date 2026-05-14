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
internal partial class LlmJsonContext : JsonSerializerContext;

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ChatRequest(
    string Model,
    ChatMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    double? Temperature,
    bool Stream = false);

internal sealed record ChatResponseChoice(int Index, ChatMessage Message, string? FinishReason);

internal sealed record ChatResponse(string? Id, string? Model, ChatResponseChoice[]? Choices);

internal sealed record ChatErrorBody(string? Message, string? Type, string? Code);

internal sealed record ChatErrorResponse(ChatErrorBody? Error);

internal sealed record ModelInfo(string Id);

internal sealed record ModelsResponse(ModelInfo[]? Data);
