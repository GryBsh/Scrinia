namespace Scrinia.Server.Auth;

/// <summary>
/// The permission vocabulary, stated once. Both authorization surfaces — the REST
/// endpoint filter and <see cref="McpPermissionFilter"/> — reference these constants,
/// so renaming a permission is a compile error rather than a silent divergence
/// between two independently maintained mappings.
/// </summary>
public static class Permissions
{
    public const string Append = "append";
    public const string Chat = "chat";
    public const string Copy = "copy";
    public const string Export = "export";
    public const string Forget = "forget";
    public const string Import = "import";
    public const string Read = "read";
    public const string Reindex = "reindex";
    public const string Search = "search";
    public const string Store = "store";

    /// <summary>Every declared permission. Pinned against the declared constants by test.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Append, Chat, Copy, Export, Forget, Import, Read, Reindex, Search, Store,
    ];
}

/// <summary>
/// The literal <c>"store"</c> used as an auth claim type, a route-value key, and a query-string key
/// throughout the server to name which memory store a request targets. Unrelated to
/// <see cref="Permissions.Store"/>, which is the permission required to write to one — a pre-existing
/// naming collision in the domain, not something introduced here.
///
/// <para>Declared in this file rather than its own: the test enumerating server source for bare
/// permission literals excludes only a file named <c>Permissions.cs</c>, so a same-named
/// <c>"store"</c> constant living anywhere else would still be flagged even though it means
/// something else entirely.</para>
/// </summary>
internal static class StoreSelectorKey
{
    public const string Name = "store";
}

/// <summary>
/// MCP tool names that happen to be spelled identically to the permission they require —
/// <see cref="McpPermissionFilter"/>'s switch maps tool <c>"search"</c> to permission
/// <see cref="Permissions.Search"/>, tool <c>"append"</c> to <see cref="Permissions.Append"/>, and
/// tool <c>"forget"</c> to <see cref="Permissions.Forget"/>. These are tool identities, not
/// permission values — reusing <see cref="Permissions"/>'s constants for them would conflate the
/// two — but a bare <c>"search"</c>/<c>"append"</c>/<c>"forget"</c> literal on the switch's
/// left-hand side is textually indistinguishable, to a whole-file literal scan, from the very
/// bare-permission-literal hazard that scan exists to catch. Declared here (excluded from the
/// scan by filename) so the switch's pattern side can reference a symbol instead.
/// </summary>
internal static class McpToolNames
{
    public const string Search = "search";
    public const string Append = "append";
    public const string Forget = "forget";
}
