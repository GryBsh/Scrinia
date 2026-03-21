namespace Scrinia.Server.Chat;

/// <summary>
/// Interface for cloud LLM providers. Each implementation handles message format
/// translation and SSE parsing for its specific API.
/// </summary>
public interface IChatProvider
{
    /// <summary>Provider name (e.g., "anthropic", "openai", "gemini").</summary>
    string Name { get; }

    /// <summary>
    /// Streams a chat completion as a sequence of events. Handles message format
    /// translation, tool definition mapping, and SSE response parsing internally.
    /// </summary>
    IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatMessage[] messages,
        AgentToolDef[] tools,
        CancellationToken ct = default);
}

/// <summary>Tool definition passed to the LLM.</summary>
public sealed record AgentToolDef(
    string Name,
    string Description,
    Dictionary<string, object> Parameters);
