using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Scrinia.Server.Auth;

/// <summary>
/// Brings the HTTP MCP surface to permission parity with the REST endpoints. The REST API gates
/// every operation on a fine-grained permission (read/search/store/append/forget/…); without this,
/// the MCP tools and the <c>scrinia://memory</c> resource would be reachable by any key that merely
/// has access to the store, regardless of which permissions it holds.
///
/// <para>Enforced as a single incoming JSON-RPC message filter (the SDK layer documented for
/// authentication) registered only on the HTTP server. The shared <see cref="ModelContextProtocol"/>
/// tool classes stay auth-agnostic, so the CLI stdio server — which has no <see cref="RequestContext"/>
/// — is unaffected.</para>
/// </summary>
public static class McpPermissionFilter
{
    private static readonly AsyncLocal<IReadOnlyCollection<string>?> _permissions = new();

    /// <summary>
    /// The caller's permissions for the current MCP session. Set in <c>ConfigureSessionOptions</c>
    /// from the authenticated <see cref="RequestContext"/> (the only place the request-scoped,
    /// auth-populated context is reliably available) and read by <see cref="Filter"/> — it flows in
    /// via the per-session execution context, the same mechanism <c>MemoryStoreContext.Current</c>
    /// uses. Resolving <see cref="RequestContext"/> inside the filter does NOT work: the message
    /// handler runs in a fresh DI scope whose <see cref="RequestContext"/> is empty.
    /// </summary>
    public static IReadOnlyCollection<string>? CurrentPermissions
    {
        get => _permissions.Value;
        set => _permissions.Value = value;
    }

    // AsyncLocal<bool> defaults to false when unset, so the flag is phrased as "read-only" (not
    // "writable"): unset therefore means "not read-only" = writable, which is what the CLI stdio
    // server (which sets nothing) needs — it must keep allowing writes unchanged.
    private static readonly AsyncLocal<bool> _storeReadOnly = new();

    /// <summary>
    /// Whether the current MCP session's store is read-only for the caller. Set in
    /// <c>ConfigureSessionOptions</c> alongside <see cref="CurrentPermissions"/>, from the same
    /// <see cref="RequestContext"/> via <c>MemoryEndpoints.IsWritable</c>. Defaults to <c>false</c>
    /// (writable) when unset, so the CLI stdio server — which never sets it — is unaffected.
    /// </summary>
    public static bool CurrentStoreReadOnly
    {
        get => _storeReadOnly.Value;
        set => _storeReadOnly.Value = value;
    }

    private static readonly HashSet<string> WritePermissions =
        new(StringComparer.OrdinalIgnoreCase) { Permissions.Store, Permissions.Append, Permissions.Forget };

    /// <summary>True when <paramref name="permission"/> authorizes a mutating operation.</summary>
    public static bool IsWritePermission(string? permission) =>
        permission is not null && WritePermissions.Contains(permission);

    /// <summary>
    /// Tools that are deliberately reachable without a permission. Listing them
    /// explicitly is what lets an unrecognised name fail closed instead of sharing
    /// a default arm with them.
    /// </summary>
    private static readonly string[] UngatedTools = ["guide", "restore"];

    /// <summary>
    /// Maps an MCP request to the permission it requires, or <c>null</c> when the request needs no
    /// specific permission (handshake/discovery, and the bootstrap/recovery tools <c>guide</c> and
    /// <c>restore</c>, which parallel REST having no such gate). Pure — unit-tested directly.
    ///
    /// <para>The mutating tools <c>compact</c>/<c>link</c>/<c>reconcile</c> map
    /// to <c>store</c>: there is no finer REST permission for them, so they ride the write gate.</para>
    ///
    /// <para><c>null</c> is also returned for an unrecognised tool name — but that case is NOT
    /// equivalent to <c>guide</c>/<c>restore</c> needing no permission. See
    /// <see cref="HasExplicitDecision"/>, which is the question that tells the two apart; the
    /// <see cref="Filter"/> denies on that question, not on this method's return value alone.</para>
    /// </summary>
    public static string? RequiredPermission(string method, string? toolName) => method switch
    {
        "tools/call" => toolName switch
        {
            McpToolNames.Search or "list" or "facts" => Permissions.Search,
            "recall" => Permissions.Read,
            "remember" or "compact" or "link" or "reconcile" => Permissions.Store,
            McpToolNames.Append => Permissions.Append,
            McpToolNames.Forget => Permissions.Forget,
            _ => null, // guide, restore, or unknown → no permission mapped (see HasExplicitDecision)
        },
        "resources/read" => Permissions.Read,
        _ => null, // initialize, tools/list, prompts/*, resources/list, notifications, …
    };

    /// <summary>
    /// True when this method/tool pair has a deliberate permission decision — either a
    /// mapped permission or membership of <see cref="UngatedTools"/>.
    ///
    /// Deliberately a separate question from <see cref="RequiredPermission"/> returning
    /// null. Null means "no permission needed", which is the right answer for `guide`
    /// and the wrong answer for a tool nobody mapped; one value cannot carry both. The
    /// filter denies when this returns false, so an unmapped tool fails closed without
    /// inventing a sentinel permission string that a key might somehow be granted.
    /// </summary>
    public static bool HasExplicitDecision(string method, string? toolName)
    {
        if (method != "tools/call")
            return true; // non-tool methods are decided by the outer switch

        if (toolName is null)
            return false;

        return RequiredPermission(method, toolName) is not null
            || UngatedTools.Contains(toolName, StringComparer.Ordinal);
    }

    /// <summary>
    /// The incoming-message filter. Inspects each JSON-RPC request, resolves the required permission,
    /// and throws <see cref="McpException"/> (which the SDK turns into a JSON-RPC error) when the
    /// caller's <see cref="RequestContext"/> lacks it. Fails closed: a gated op with no resolvable
    /// request context is denied.
    /// </summary>
    public static McpMessageFilter Filter => next => async (ctx, ct) =>
    {
        if (ctx.JsonRpcMessage is JsonRpcRequest req)
        {
            string? toolName = req.Method == "tools/call"
                ? (req.Params as JsonObject)?["name"]?.GetValue<string>()
                : null;

            if (!HasExplicitDecision(req.Method, toolName))
                // Fail closed: an unrecognised tool is indistinguishable from one
                // someone forgot to map, so it is denied rather than admitted.
                throw new McpException("This operation is not available.");

            string? required = RequiredPermission(req.Method, toolName);
            if (required is not null)
            {
                var perms = CurrentPermissions;
                bool granted = perms is not null
                    && perms.Contains(required, StringComparer.OrdinalIgnoreCase);
                if (!granted)
                    // Throwing short-circuits the pipeline: the call is blocked and never dispatched.
                    // The SDK sanitizes a filter-thrown exception to a generic JSON-RPC internal error
                    // at this layer (the detailed message below isn't surfaced to the client), which is
                    // an acceptable authz outcome — the operation is denied without leaking internals.
                    throw new McpException(
                        $"This API key lacks the '{required}' permission required for this operation.");

                // Read-only store boundary (parity with the REST write handlers, task B4): a caller
                // with the permission granted can still be denied if the store itself is read-only
                // for them (e.g. an SSO store_access claim). Checked after the permission-granted
                // check so a caller lacking the permission sees that error first.
                if (IsWritePermission(required) && CurrentStoreReadOnly)
                    throw new McpException(
                        "This store is read-only for this key; the requested operation would modify it.");
            }
        }

        await next(ctx, ct);
    };
}
