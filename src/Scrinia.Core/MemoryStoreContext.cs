namespace Scrinia.Core;

/// <summary>
/// AsyncLocal indirection for <see cref="IMemoryStore"/>.
/// MCP tools read this to dispatch to local or remote storage.
/// Follows the same pattern as <see cref="SessionBudget"/> overrides.
/// </summary>
public static class MemoryStoreContext
{
    private static readonly AsyncLocal<IMemoryStore?> _current = new();

    /// <summary>
    /// Gets or sets the IMemoryStore for the current async flow.
    /// MCP tools read this to dispatch to local or remote storage.
    /// </summary>
    public static IMemoryStore? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
