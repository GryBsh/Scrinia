namespace Scrinia.Core.Formats;

/// <summary>
/// Thrown when a write targets a topic scope holding a sidecar written by a newer build.
///
/// <para>One type for both refusal levels — the scope gate that stops ordinary write verbs
/// early, and the <c>WriteSidecarCore</c> backstop — so a caller has one thing to catch
/// regardless of which fired.</para>
/// </summary>
public sealed class ScopeReadOnlyException : InvalidOperationException
{
    public string Scope { get; }
    public string MemoryName { get; }
    public int DeclaredVersion { get; }
    public int SupportedVersion { get; }
    public string? AppendOperationId { get; }

    public ScopeReadOnlyException(string scope, string memoryName, int declared, int supported,
        string? appendOperationId = null)
        : base($"Scope '{scope}' is read-only: memory '{memoryName}' declares schema version " +
               $"{declared}, but this build of scri supports up to {supported}. " +
               "Reading and searching still work. Upgrade scri to write to this scope.")
    {
        Scope = scope;
        MemoryName = memoryName;
        DeclaredVersion = declared;
        SupportedVersion = supported;
        AppendOperationId = appendOperationId;
    }
}
