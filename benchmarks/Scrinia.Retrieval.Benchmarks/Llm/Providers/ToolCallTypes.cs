namespace Scrinia.Retrieval.Benchmarks.Llm.Providers;

/// One tool the model may call. JsonSchema is a raw JSON schema string for the args object.
public sealed record ToolSpec(string Name, string Description, string JsonSchemaParameters);

/// A message in the tool-calling conversation. Role is "system"|"user"|"assistant"|"tool".
/// For assistant turns that requested tools, ToolCalls is non-empty and Content may be "".
/// For tool-result turns, Role="tool", ToolCallId echoes the call, Content is the tool output.
public sealed record ToolChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ToolCallId = null);

/// A single tool call the model emitted. ArgumentsJson is the raw JSON args string.
public sealed record ToolCallRequest(string Id, string ToolName, string ArgumentsJson);

/// Result of one assistant turn. StopReason is the raw finish_reason.
public sealed record ToolTurnResult(
    string? AssistantContent,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    string? StopReason);
