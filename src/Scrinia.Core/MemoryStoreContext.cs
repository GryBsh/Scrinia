namespace Scrinia.Core;

/// <summary>
/// AsyncLocal indirection for <see cref="IMemoryStore"/>.
/// MCP tools read this to dispatch to the configured workspace store.
/// Follows the same pattern as <see cref="SessionBudget"/> overrides.
/// </summary>
public static class MemoryStoreContext
{
    private static readonly AsyncLocal<IMemoryStore?> _current = new();
    private static IMemoryStore? _default;

    /// <summary>
    /// Gets or sets the IMemoryStore for the current async flow.
    /// MCP tools read this to dispatch to the configured workspace store.
    /// Falls back to <see cref="Default"/> when no async-local value is set,
    /// bridging the generic-host thread boundary where AsyncLocal does not propagate.
    /// </summary>
    public static IMemoryStore? Current
    {
        get => _current.Value ?? _default;
        set => _current.Value = value;
    }

    /// <summary>Process-wide default used when no AsyncLocal value is set (CLI single-session mode).</summary>
    public static IMemoryStore? Default
    {
        get => _default;
        set => _default = value;
    }

    internal static IMemoryStore Resolve()
        => Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using MCP tools.");

    /// <summary>Raw AsyncLocal value without Default fallback — for StoreScope save/restore.</summary>
    internal static IMemoryStore? RawCurrent
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
