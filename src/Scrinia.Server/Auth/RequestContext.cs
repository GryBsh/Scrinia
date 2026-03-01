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

    public bool HasPermission(string permission) =>
        Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public bool CanAccessStore(string storeName) =>
        Stores.Contains("*", StringComparer.OrdinalIgnoreCase)
        || Stores.Contains(storeName, StringComparer.OrdinalIgnoreCase);

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
