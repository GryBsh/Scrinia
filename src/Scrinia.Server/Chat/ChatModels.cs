using System.Text.Json.Serialization;

namespace Scrinia.Server.Chat;

/// <summary>Chat endpoint request.</summary>
public sealed record ChatRequest(
    ChatMessage[] Messages,
    string? Provider = null);

/// <summary>A single message in the conversation.</summary>
public sealed record ChatMessage(
    string Role,
    string? Content = null,
    ChatToolCall[]? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>A tool call made by the assistant.</summary>
public sealed record ChatToolCall(
    string Id,
    string Name,
    string Arguments);

/// <summary>SSE event streamed from the chat endpoint.</summary>
public sealed record ChatEvent(
    string Type,
    string? Content = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? Error = null);

/// <summary>Response for GET /chat/providers — lists available providers.</summary>
public sealed record ChatProvidersResponse(string[] Providers);

// ── Source-gen JSON context (trimming-safe) ──────────────────────────────────

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatMessage[]))]
[JsonSerializable(typeof(ChatToolCall))]
[JsonSerializable(typeof(ChatToolCall[]))]
[JsonSerializable(typeof(ChatEvent))]
[JsonSerializable(typeof(ChatProvidersResponse))]
public partial class ChatJsonContext : JsonSerializerContext;
