namespace Scrinia.Core.Llm;

/// <summary>
/// AsyncLocal context for making <see cref="IBackgroundLlm"/> available to consolidation
/// code without threading it through every method. Mirrors <see cref="MemoryEventSinkContext"/>:
/// <see cref="Current"/> is set per-request in server mode, while <see cref="Default"/> provides
/// a process-wide fallback for the CLI single-session case (AsyncLocal does not propagate
/// through the generic host to MCP tool handlers).
/// </summary>
public static class BackgroundLlmContext
{
    private static readonly AsyncLocal<IBackgroundLlm?> _current = new();
    private static IBackgroundLlm? _default;

    /// <summary>The LLM for the current async context, falling back to <see cref="Default"/>.</summary>
    public static IBackgroundLlm? Current { get => _current.Value ?? _default; set => _current.Value = value; }

    /// <summary>Process-wide fallback used when no AsyncLocal value is set (CLI mode).</summary>
    public static IBackgroundLlm? Default { get => _default; set => _default = value; }
}
