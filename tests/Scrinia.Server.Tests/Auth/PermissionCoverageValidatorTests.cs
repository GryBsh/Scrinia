using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public void Rejects_ATypoInADeclarationShadowedByALaterOne()
    {
        // A group-level RequirePermission and a route-level one both attach their own
        // enforcing filter, so both are live and both must be spell-checked. Reading only the
        // effective declaration would let the shadowed one gate the route on a permission no
        // key can ever hold, which is the exact failure this check exists to catch.
        var source = SourceOf(Endpoint("GET /shadowed",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata(["raed"]),
            new RequiredPermissionsMetadata([Permissions.Read])));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*raed*");
    }

    [Fact]
    public async Task ValidatePermissionCoverage_ThrowsForAnUndeclaredRoute_OnARealWebApplication()
    {
        // Pins WHICH route table the startup call reads. Every other test here hands Validate
        // a source it built itself, so none of them notices if ValidatePermissionCoverage
        // reads the EndpointDataSource in DI instead of app.DataSources — that composite is
        // empty until the host starts, so the check would iterate nothing and pass. This is a
        // real WebApplication carrying a real hole: it must throw.
        await using var app = BuildApp(a => a.MapGet("/undeclared", () => Results.Ok())
            .RequireAuthorization());

        var act = app.ValidatePermissionCoverage;

        act.Should().Throw<InvalidOperationException>().WithMessage("*/undeclared*");
    }

    [Fact]
    public async Task ValidatePermissionCoverage_AcceptsTheSameRouteOnceItDeclares()
    {
        // The other half of the pin. Without it, a ValidatePermissionCoverage that threw
        // unconditionally would satisfy the test above.
        await using var app = BuildApp(a => a.MapGet("/undeclared", () => Results.Ok())
            .RequireAuthorization().RequirePermission(Permissions.Read));

        var act = app.ValidatePermissionCoverage;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ValidatePermissionCoverage_NamesThePluginRoutesItCouldNotCheck()
    {
        // The plugin group's bypass is a group convention: it silences the check for whatever
        // a plugin maps into the group. The real thing is unobservable here — plugins load
        // only when Vulkan is enabled — but the warning that compensates for it is not, and
        // an unlogged warning would be no better than the silence it replaces.
        var log = new List<string>();
        await using var app = BuildApp(a =>
        {
            var group = a.MapGroup("/api/v1/plugins")
                .RequireAuthorization()
                .PermissionCheckedElsewhere(PermissionCheckedElsewhereReasons.PluginOwnDeclarations);
            group.MapGet("/whatever", () => Results.Ok());
        }, log);

        app.ValidatePermissionCoverage();

        log.Should().ContainSingle()
            .Which.Should().Contain("/api/v1/plugins/whatever")
            .And.Contain("not verified");
    }

    [Fact]
    public async Task ValidatePermissionCoverage_SaysNothingWhenNoPluginMappedARoute()
    {
        // The default configuration. A warning on every boot about a group nobody populated
        // is the kind of standing noise operators learn to scroll past.
        var log = new List<string>();
        await using var app = BuildApp(a => a.MapGroup("/api/v1/plugins")
            .RequireAuthorization()
            .PermissionCheckedElsewhere(PermissionCheckedElsewhereReasons.PluginOwnDeclarations), log);

        app.ValidatePermissionCoverage();

        log.Should().BeEmpty();
    }

    /// <summary>A throwaway application, never started. Warnings land in <paramref name="log"/>.</summary>
    private static WebApplication BuildApp(Action<WebApplication> map, ICollection<string>? log = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (log is not null)
            builder.Logging.AddProvider(new CapturingLoggerProvider(log));
        builder.Services.AddAuthorization();

        var app = builder.Build();
        map(app);
        return app;
    }

    private sealed class CapturingLoggerProvider(ICollection<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(log);
        public void Dispose() { }

        private sealed class CapturingLogger(ICollection<string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Add(formatter(state, exception));
            }
        }
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
        // endpoint. A third call site fails here and forces the question. Scans all of src/,
        // not just Scrinia.Server, so the pin does not depend on the method staying internal.
        string sourceRoot = TestPaths.RepoRoot("src");
        var callSites = new List<string>();

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            // Requires a receiver before the dot, so prose mentioning the method — the
            // validator's own failure message names it — is not counted as a use of it.
            foreach (Match _ in Regex.Matches(File.ReadAllText(file), @"[\w)\]]\s*\.PermissionCheckedElsewhere\s*\("))
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
