using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests.Auth;

/// <summary>
/// Both directions of the startup gate. A false positive here is a server that will not
/// boot, so the accept cases are as load-bearing as the reject cases and are stated first.
/// </summary>
public class PermissionCoverageValidatorTests
{
    private static EndpointDataSource SourceOf(params Endpoint[] endpoints) =>
        new TestEndpointDataSource(endpoints);

    private static Endpoint Endpoint(string displayName, params object[] metadata) =>
        new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/x"),
            0,
            new EndpointMetadataCollection(metadata),
            displayName);

    private sealed class TestEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    [Fact]
    public void Accepts_AnAuthenticatedEndpointDeclaringAPermission()
    {
        var source = SourceOf(Endpoint("GET /gated",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata([Permissions.Read])));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnAuthenticatedEndpointMarkedPublic()
    {
        var source = SourceOf(Endpoint("GET /health",
            new AuthorizeAttribute(),
            new PublicEndpointMetadata()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnEndpointWithNoAuthorizationAtAll()
    {
        // Not every endpoint is in scope. An unauthenticated endpoint is a different
        // decision, made elsewhere, and this validator must not second-guess it.
        var source = SourceOf(Endpoint("GET /open"));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnEndpointGatedByANamedPolicy()
    {
        // A named policy is already an explicit, deliberate gate — this is how
        // KeyEndpoints ("ManageKeys") and HealthEndpoints ("Health") are protected.
        // Requiring RequirePermission on top would be ceremony, and forcing them to
        // claim PublicEndpoint() would be a false statement about routes that are gated.
        var source = SourceOf(Endpoint("POST /keys", new AuthorizeAttribute("ManageKeys")));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnEndpointCheckedBelowTheRouteTable()
    {
        var source = SourceOf(Endpoint("POST /mcp",
            new AuthorizeAttribute(),
            new PermissionCheckedElsewhereMetadata("McpPermissionFilter")));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_BareAuthenticationWithNoPolicyAndNoPermission()
    {
        // The actual hole: RequireAuthorization() with no policy proves only that the
        // caller is someone, not that they may do this.
        var source = SourceOf(Endpoint("POST /gap", new AuthorizeAttribute()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*POST /gap*");
    }

    [Fact]
    public void Rejects_AnAuthenticatedEndpointDeclaringNeither()
    {
        var source = SourceOf(Endpoint("GET /forgotten", new AuthorizeAttribute()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GET /forgotten*",
                "the message must name the offending route — a validator that fails "
                + "without saying which endpoint is worse than no validator");
    }

    [Fact]
    public void Rejects_AnEndpointDeclaringBoth()
    {
        // Both markers is a contradiction, not a belt-and-braces. Reading it as
        // "public wins" would let a stray PublicEndpoint() silently open a gated route.
        var source = SourceOf(Endpoint("GET /confused",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata([Permissions.Read]),
            new PublicEndpointMetadata()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GET /confused*");
    }

    [Fact]
    public void Rejects_APermissionNameOutsideTheVocabulary()
    {
        var source = SourceOf(Endpoint("GET /typo",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata(["raed"])));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*raed*");
    }

    [Fact]
    public void TheRealRouteTable_PassesItsOwnGate()
    {
        // Program.cs runs this same check at startup, but over the route table as it stands
        // before the host starts. This runs it over the started application's table and
        // asserts that table is not empty — the failure mode being guarded is a validator
        // wired to a source that holds nothing, which boots happily while checking nothing.
        using var factory = new ScriniaServerFactory();
        _ = factory.CreateAuthenticatedClient(); // force the app to build

        var live = factory.Services.GetRequiredService<EndpointDataSource>();

        live.Endpoints.Should().NotBeEmpty();
        live.Endpoints.Should().Contain(e =>
            e.Metadata.GetMetadata<RequiredPermissionsMetadata>() != null,
            "the memory routes declare permissions, so a table without one is the wrong table");

        var act = () => PermissionCoverageValidator.Validate(live);

        act.Should().NotThrow();
    }

    [Fact]
    public void OnlyTheKnownCallSites_BypassTheRouteTableCheck()
    {
        // PermissionCheckedElsewhere silences the coverage check, so its uses are pinned.
        // Counted from source rather than from the live route table, because the live table
        // cannot see both of them: the plugin group is empty in the default configuration
        // (plugins load only when Vulkan is explicitly enabled), so its use contributes no
        // endpoint. A third call site fails here and forces the question.
        string serverRoot = TestPaths.RepoRoot("src", "Scrinia.Server");
        var callSites = new List<string>();

        foreach (string file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            foreach (Match _ in Regex.Matches(File.ReadAllText(file), @"\.PermissionCheckedElsewhere\("))
                callSites.Add(Path.GetFileName(file));
        }

        callSites.Should().BeEquivalentTo(["Program.cs", "Program.cs"],
            "this marker suppresses the coverage check — a third use must be argued for, "
            + "not merely added");
    }

    [Fact]
    public void TheBypassDoesNotSpreadBeyondTheRoutesItWasOpenedFor()
    {
        // The count above cannot see which routes a call site covers: MapMcp expands "/mcp"
        // into several framework-shaped routes, and a group convention reaches everything
        // mapped into it. This asserts the reach, not the count — nothing outside /mcp and
        // /api/v1/plugins may carry the marker.
        using var factory = new ScriniaServerFactory();
        _ = factory.CreateAuthenticatedClient(); // force the app to build

        var bypassing = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<PermissionCheckedElsewhereMetadata>() is not null)
            .Select(e => (e as RouteEndpoint)?.RoutePattern.RawText ?? e.DisplayName ?? "<unnamed>")
            .ToList();

        bypassing.Should().NotBeEmpty("the /mcp routes carry the marker")
            .And.OnlyContain(route =>
                route.StartsWith("/mcp", StringComparison.Ordinal) ||
                route.StartsWith("/api/v1/plugins", StringComparison.Ordinal));
    }
}
