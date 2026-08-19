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
        IsGlobalAdmin || Stores.Contains(storeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the caller holds wildcard store access ("*") — the global admin role.</summary>
    public bool IsGlobalAdmin => Stores.Contains("*", StringComparer.OrdinalIgnoreCase);

    /// <summary>Store access level claims, populated from an SSO claim transformer.
    /// <b>Empty in every shipped configuration</b> — no <c>IClaimsTransformation</c> outside the test
    /// suite emits <c>store_access</c>, so <see cref="GetStoreAccessLevel"/> always returns its
    /// read-write default. See <c>MemoryEndpoints.IsWritable</c> for what would have to be added.</summary>
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
