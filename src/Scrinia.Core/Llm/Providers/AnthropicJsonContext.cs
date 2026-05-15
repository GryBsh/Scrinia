using System.Text.Json.Serialization;

namespace Scrinia.Core.Llm.Providers;

/// <summary>
/// Source-generated JSON contracts for Anthropic's Messages API.
/// Reference: https://docs.anthropic.com/en/api/messages
/// </summary>
internal sealed record AnthropicMessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] AnthropicMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("temperature")] double Temperature);

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record AnthropicMessagesResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("content")] AnthropicContentBlock[]? Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason);

internal sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
internal partial class AnthropicJsonContext : JsonSerializerContext;
