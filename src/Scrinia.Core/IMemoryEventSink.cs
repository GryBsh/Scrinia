namespace Scrinia.Core;

/// <summary>
/// Plugins implement this to react to store/append/forget events on the MCP code path.
/// The REST path uses <c>IMemoryOperationHook</c> via <c>PluginPipeline</c> instead.
/// Both paths should delegate to the same plugin logic internally to avoid duplication.
/// </summary>
public interface IMemoryEventSink
{
    Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct);
    Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct);
    Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct);
}

/// <summary>
/// AsyncLocal context for making <see cref="IMemoryEventSink"/> available
/// to MCP tools without passing it through every method.
/// <para>
/// In the server, <see cref="Current"/> is set per-request (AsyncLocal).
/// In the CLI, AsyncLocal doesn't propagate through the generic host to MCP tool handlers,
/// so <see cref="Default"/> provides a process-wide fallback.
/// </para>
/// </summary>
public static class MemoryEventSinkContext
{
    private static readonly AsyncLocal<IMemoryEventSink?> _current = new();
    private static IMemoryEventSink? _default;

    /// <summary>Gets/sets the event sink for the current async context, falling back to <see cref="Default"/>.</summary>
    public static IMemoryEventSink? Current { get => _current.Value ?? _default; set => _current.Value = value; }

    /// <summary>Process-wide default used when no AsyncLocal value is set (CLI single-session mode).</summary>
    public static IMemoryEventSink? Default { get => _default; set => _default = value; }

    /// <summary>Installs the process-wide default sink as a composite over <paramref name="sinks"/>.
    /// The one place <see cref="Default"/> is assigned from — six call sites in WorkspaceSetup each
    /// built their own CompositeEventSink, so a change to how the default is composed had six
    /// places to miss.</summary>
    public static void InstallDefault(params IMemoryEventSink[] sinks) =>
        Default = new CompositeEventSink(sinks);
}
