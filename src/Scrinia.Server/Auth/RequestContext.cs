using Scrinia.Core;

namespace Scrinia.Server.Auth;

/// <summary>
/// Scoped per-request context populated by middleware after authentication.
/// Replaces WorkspaceContext — supports multi-store and permissions.
/// </summary>
public sealed class RequestContext
{
    public string UserId { get; set; } = "";
    public string[] Stores { get; set; } = [];
    public string[] Permissions { get; set; } = [];
    public string? ActiveStore { get; set; }
    public IMemoryStore? Store { get; set; }

    /// <summary>
    /// <see cref="Store"/> narrowed to the local-filesystem contract, for the endpoints that
    /// read or write workspace files directly — bundle export and import.
    /// <para>The store comes from <see cref="Scrinia.Core.IStorageBackend.CreateStore"/>, whose
    /// only in-repo implementation (<see cref="Scrinia.Core.FilesystemBackend"/>) always returns a
    /// <see cref="Scrinia.Core.FileMemoryStore"/>, so this is total for the server as shipped. A
    /// replacement backend registered via DI could return a non-local store; before the
    /// ILocalMemoryStore split those endpoints threw <see cref="NotSupportedException"/> from
    /// inside the store in exactly that case, and they still do — from here, once, by name.</para>
    /// </summary>
    public ILocalMemoryStore LocalStore =>
        Store as ILocalMemoryStore
        ?? throw new NotSupportedException(
            "This endpoint needs local filesystem access, and the configured storage backend "
            + "did not produce a local store.");

    public bool HasPermission(string permission) =>
        Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public bool CanAccessStore(string storeName) =>
        IsGlobalAdmin || Stores.Contains(storeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the caller holds wildcard store access ("*") — the global admin role.</summary>
    public bool IsGlobalAdmin => Stores.Contains("*", StringComparer.OrdinalIgnoreCase);

    /// <summary>Store access level claims, populated from SSO claim transformer.</summary>
    public string[] StoreAccessLevels { get; set; } = [];

    /// <summary>
    /// Returns the access level for the given store ("read-only" or "read-write").
    /// API key users default to "read-write" (no store_access claims present).
    /// </summary>
    public string GetStoreAccessLevel(string storeName)
    {
        var match = Array.Find(StoreAccessLevels, c => c.StartsWith($"{storeName}:", StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            int colonIdx = match.IndexOf(':');
            return colonIdx >= 0 ? match[(colonIdx + 1)..] : "read-write";
        }
        return "read-write"; // default for API keys (backward compat)
    }
}
