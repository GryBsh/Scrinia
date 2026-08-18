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
///
/// <para><b>Internal on purpose.</b> Every type it inspects is internal, so a caller outside
/// this assembly could invoke <see cref="Validate"/> but could not construct anything it
/// reads. A public method whose inputs are unreachable is a public surface in name only.</para>
/// </summary>
internal static class PermissionCoverageValidator
{
    /// <summary>
    /// Validates the application's own route table.
    ///
    /// <para>Choosing the source is this method's job, not the call site's, because there are
    /// two plausible sources and one of them is silently wrong. The
    /// <see cref="EndpointDataSource"/> registered in DI is a composite the routing middleware
    /// fills in when the host starts — after the last line of Program.cs — so at startup it
    /// holds nothing and validating it checks nothing while appearing to work. The endpoints
    /// live in <c>app.DataSources</c>, which every <c>Map*</c> call writes into. Pinned by
    /// <c>ValidatePermissionCoverage_ThrowsForAnUndeclaredRoute_OnARealWebApplication</c>.</para>
    /// </summary>
    internal static void ValidatePermissionCoverage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
        Validate(source);
        WarnAboutPluginRoutesTheGateCannotSee(source, app.Logger);
    }

    internal static void Validate(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        foreach (var endpoint in source.Endpoints)
        {
            string name = Describe(endpoint);

            // Every declaration on the endpoint, not just the effective one: a group-level
            // RequirePermission shadowed by a route-level one is still enforced by its own
            // filter, so a typo in it still produces a route gated on a permission no key can
            // hold. Checked before any short-circuit below can skip it, because
            // RequirePermission does not validate its own arguments.
            var declarations = endpoint.Metadata.GetOrderedMetadata<RequiredPermissionsMetadata>();
            foreach (var declaration in declarations)
                foreach (string permission in declaration.Permissions)
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

            // The last declaration is the effective one, matching how GetMetadata reads it.
            bool declared = declarations.Count > 0;
            bool isPublic = endpoint.Metadata.GetMetadata<PublicEndpointMetadata>() is not null;

            if (declared && isPublic)
            {
                problems.Add($"{name}: declares both RequirePermission and PublicEndpoint.");
                continue;
            }

            if (!declared && !isPublic)
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

    /// <summary>
    /// Names the plugin-mapped routes the coverage check did not examine.
    ///
    /// <para>The plugin group's marker is a group convention, so it silences the check for
    /// every route a plugin maps into it — whether or not that plugin declared anything. No
    /// test can observe this, because plugins load only when Vulkan is explicitly enabled, so
    /// an operator running with plugins gets a boot-time list instead of silence. Nothing is
    /// logged when no plugin mapped a route, which is the default configuration.</para>
    /// </summary>
    private static void WarnAboutPluginRoutesTheGateCannotSee(EndpointDataSource source, ILogger logger)
    {
        string[] unexamined =
        [
            .. source.Endpoints
                .Where(e => e.Metadata.GetMetadata<PermissionCheckedElsewhereMetadata>()
                    is { Where: PermissionCheckedElsewhereReasons.PluginOwnDeclarations })
                .Select(Describe),
        ];

        if (unexamined.Length == 0)
            return;

        logger.LogWarning(
            "Permission coverage was not verified for {Count} plugin-mapped route(s): {Routes}. "
            + "They bypass the startup coverage check, so each plugin is trusted to declare the "
            + "permissions its own routes require — a promise nothing here enforces.",
            unexamined.Length,
            string.Join(", ", unexamined));
    }

    private static string Describe(Endpoint endpoint) =>
        endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed endpoint>";
}
