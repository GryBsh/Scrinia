using Microsoft.AspNetCore.Authorization;

namespace Scrinia.Server.Auth;

/// <summary>
/// Startup gate making a missing permission declaration impossible to ship. Every
/// endpoint that requires authentication must declare either the permissions it
/// needs or that it is deliberately ungated; anything else refuses to boot.
///
/// <para>Refusing to boot is the point. The alternative — logging a warning and serving —
/// is the state this mechanism exists to eliminate: a route that authenticates the caller,
/// checks nothing else, and looks fine.</para>
/// </summary>
public static class PermissionCoverageValidator
{
    public static void Validate(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        foreach (var endpoint in source.Endpoints)
        {
            string name = endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed endpoint>";
            var declared = endpoint.Metadata.GetMetadata<RequiredPermissionsMetadata>();

            // Spelling is checked wherever a declaration appears, before any of the
            // short-circuits below can skip it: RequirePermission does not validate its
            // arguments, so this is the only place a typo'd permission — a route gated on
            // a permission no key can ever hold — is caught.
            foreach (string permission in declared?.Permissions ?? [])
                if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
                    problems.Add($"{name}: '{permission}' is not a declared permission.");

            // Out of scope: an endpoint with no authorization requirement is a
            // separate decision made elsewhere.
            if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
                continue;

            // A named authorization policy is itself an explicit gate — KeyEndpoints
            // and HealthEndpoints are protected this way. Only bare authentication,
            // which proves identity and nothing else, needs a permission declaration.
            if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(a => !string.IsNullOrWhiteSpace(a.Policy)))
                continue;

            // Checked below the route table — /mcp resolves its permission per
            // JSON-RPC call. The marker's uses are pinned by a separate test.
            if (endpoint.Metadata.GetMetadata<PermissionCheckedElsewhereMetadata>() is not null)
                continue;

            bool isPublic = endpoint.Metadata.GetMetadata<PublicEndpointMetadata>() is not null;

            if (declared is not null && isPublic)
            {
                problems.Add($"{name}: declares both RequirePermission and PublicEndpoint.");
                continue;
            }

            if (declared is null && !isPublic)
            {
                problems.Add(
                    $"{name}: requires authentication but declares neither RequirePermission(...), "
                    + "PublicEndpoint(), nor PermissionCheckedElsewhere(...).");
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Endpoint permission coverage check failed. Every authenticated endpoint must "
                + "declare its permissions or be explicitly marked public:"
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
