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

    /// <summary>
    /// Maps an MCP request to the permission it requires, or <c>null</c> when the request needs no
    /// specific permission (handshake/discovery, and the bootstrap/recovery tools <c>guide</c> and
    /// <c>restore</c>, which parallel REST having no such gate). Pure — unit-tested directly.
    ///
    /// <para>The mutating tools <c>compact</c>/<c>link</c>/<c>reconcile</c> map
    /// to <c>store</c>: there is no finer REST permission for them, so they ride the write gate.</para>
    /// </summary>
    public static string? RequiredPermission(string method, string? toolName) => method switch
    {
        "tools/call" => toolName switch
        {
            "search" or "list" => "search",
            "recall" => "read",
            "remember" or "compact" or "link" or "reconcile" => "store",
            "append" => "append",
            "forget" => "forget",
            _ => null, // guide, restore, or unknown → no gate (dispatch/handshake handled by SDK)
        },
        "resources/read" => "read",
        _ => null, // initialize, tools/list, prompts/*, resources/list, notifications, …
    };

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
            }
        }

        await next(ctx, ct);
    };
}
