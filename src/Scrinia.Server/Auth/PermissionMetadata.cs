using Scrinia.Server.Models;

namespace Scrinia.Server.Auth;

/// <summary>
/// Endpoint metadata naming the permissions a caller must hold. Conjunctive: the
/// caller needs all of them.
///
/// <para><b>Internal on purpose.</b> The startup coverage validator reads this metadata as proof
/// that a route is guarded, but endpoint filters are not metadata, so the validator cannot
/// cross-check that enforcement is actually attached. Confining construction to
/// <see cref="PermissionEndpointExtensions.RequirePermission"/> — which attaches both in one
/// call — is what makes the inference sound. A public constructor would let any caller declare
/// a route guarded while gating nothing, which is exactly the unguarded-but-looks-guarded route
/// this whole mechanism exists to make impossible. The validator lives in this assembly and the
/// test project has InternalsVisibleTo, so nothing that needs to read it loses access.</para>
/// </summary>
internal sealed record RequiredPermissionsMetadata(IReadOnlyList<string> Permissions);

/// <summary>
/// Marks an endpoint as deliberately ungated. Its presence is what distinguishes
/// "no permission needed" from "someone forgot" — see PermissionCoverageValidator.
/// </summary>
internal sealed record PublicEndpointMetadata;

/// <summary>
/// Marks an endpoint whose permission check genuinely happens somewhere the route
/// table cannot see. <paramref name="Where"/> names that place and is required.
///
/// <para>This exists for exactly one shape of endpoint: one HTTP route carrying many
/// operations with different permissions. <c>/mcp</c> is the case — a single route over
/// JSON-RPC, where <see cref="McpPermissionFilter"/> resolves the permission per call.
/// Declaring a single RequirePermission there would be a lie, and PublicEndpoint() would
/// be a worse one.</para>
///
/// <para>It is also an escape hatch, so it is pinned: a test asserts the exact set of
/// endpoints using it. Adding a new one fails that test and forces the question.</para>
/// </summary>
internal sealed record PermissionCheckedElsewhereMetadata(string Where);

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Declares the permissions this route requires. Conjunctive.
    ///
    /// <para>Declaration and enforcement are attached together, in this one call, and this is the
    /// only place either can be attached: <see cref="RequiredPermissionsMetadata"/> and
    /// <see cref="PermissionEndpointFilter"/> are both internal, so no other assembly can produce
    /// one without the other. The coverage validator reads the metadata as proof the route is
    /// gated; a route carrying the metadata without the filter would be a declaration that gates
    /// nothing and still reads as covered.</para>
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Length == 0)
            throw new ArgumentException(
                "RequirePermission needs at least one permission. For a deliberately ungated "
                + "route use PublicEndpoint() — an empty list would read as 'gated' to the "
                + "coverage validator while gating nothing.",
                nameof(permissions));

        builder.WithMetadata(new RequiredPermissionsMetadata(permissions));
        builder.AddEndpointFilter(new PermissionEndpointFilter(permissions));
        return builder;
    }

    /// <summary>Declares this route as deliberately reachable without a permission.</summary>
    public static TBuilder PublicEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PublicEndpointMetadata());
        return builder;
    }

    /// <summary>
    /// Declares that this route's permission check happens outside the route table,
    /// naming where. See <see cref="PermissionCheckedElsewhereMetadata"/> — its use
    /// is pinned by test.
    /// </summary>
    public static TBuilder PermissionCheckedElsewhere<TBuilder>(this TBuilder builder, string where)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        builder.WithMetadata(new PermissionCheckedElsewhereMetadata(where));
        return builder;
    }
}

/// <summary>
/// The single enforcement point. Replaces the per-handler guards; the 403 body is
/// reproduced exactly as those guards wrote it, because clients parse it.
/// </summary>
internal sealed class PermissionEndpointFilter(IReadOnlyList<string> required) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        // RequestContext is registered AddScoped and populated by the auth middleware, which
        // runs before endpoint execution — DI is the one mechanism in place, so it is the only
        // one consulted here.
        var requestContext = ctx.HttpContext.RequestServices.GetService<RequestContext>();

        // Fail closed: a gated route with no resolvable request context is denied,
        // matching McpPermissionFilter's stance on the same question.
        if (requestContext is null)
            return Results.Json(new ErrorResponse("Permission required."), statusCode: 403);

        foreach (string permission in required)
        {
            if (!requestContext.HasPermission(permission))
                return Results.Json(new ErrorResponse(DenialMessage(required)), statusCode: 403);
        }

        return await next(ctx);
    }

    /// <summary>
    /// The denial text, derived from the whole declared set rather than from whichever
    /// permission was found missing first. That is what the guards being replaced said: a
    /// single-permission route named its one permission, and the one conjunctive route
    /// (chat) named all three in one sentence no matter which was absent. Both wordings are
    /// pinned by EndpointPermissionTests — clients parse this body.
    /// </summary>
    private static string DenialMessage(IReadOnlyList<string> required) =>
        required.Count == 1
            ? $"Permission '{required[0]}' required."
            : $"Permissions {Quote(required)} are required.";

    /// <summary>Renders a permission list as <c>'a', 'b', and 'c'</c>.</summary>
    private static string Quote(IReadOnlyList<string> permissions)
    {
        string[] quoted = [.. permissions.Select(p => $"'{p}'")];
        return quoted.Length == 2
            ? $"{quoted[0]} and {quoted[1]}"
            : $"{string.Join(", ", quoted[..^1])}, and {quoted[^1]}";
    }
}
