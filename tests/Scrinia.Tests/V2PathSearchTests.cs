using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for path-based list and search operations with v2 path syntax.
/// Validates path prefix filtering on ListScoped and SearchAll, v2 path
/// store+read round-trips, and mixed v1+v2 coexistence.
/// </summary>
public sealed class V2PathSearchTests
{
    private static ScriniaMcpTools Tools() => new();

    // ── Path prefix list tests ──────────────────────────────────────────────

    [Fact]
    public async Task ListScoped_PathPrefix_ReturnsSubtreeEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Frontend auth research"], "/goal/G-5/research/frontend", "frontend research");
        await Tools().Store(["Backend auth research"], "/goal/G-5/research/backend", "backend research");

        string result = await Tools().List(scopes: "/goal/G-5/", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("frontend", because: "listing with /goal/G-5/ prefix should include research/frontend");
        content.Should().Contain("backend", because: "listing with /goal/G-5/ prefix should include research/backend");
    }

    [Fact]
    public async Task ListScoped_PathPrefix_ExcludesOtherPaths()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["G-5 content"], "/goal/G-5/notes", "G-5 notes");
        await Tools().Store(["G-6 content"], "/goal/G-6/notes", "G-6 notes");

        string result = await Tools().List(scopes: "/goal/G-5/", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("/goal/G-5/", because: "G-5 entries should be listed");
        content.Should().NotContain("G-6", because: "G-6 entries should be excluded when scoping to /goal/G-5/");
    }

    [Fact]
    public async Task ListScoped_PathPrefix_MatchesAtBoundary()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["G-5 research"], "/goal/G-5/research/api", "G-5 research");
        await Tools().Store(["G-50 research"], "/goal/G-50/research/api", "G-50 research");

        string result = await Tools().List(scopes: "/goal/G-5/", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("/goal/G-5/", because: "/goal/G-5 should match /goal/G-5/research");
        content.Should().NotContain("G-50", because: "/goal/G-5 must NOT match /goal/G-50 (boundary mismatch)");
    }

    [Fact]
    public async Task ListScoped_RootPath_ReturnsAll()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["alpha content"], "/api/alpha", "alpha");
        await Tools().Store(["beta content"], "/goal/G-1/beta", "beta");

        string result = await Tools().List(scopes: "/", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("alpha", because: "root path '/' should return all entries including /api/alpha");
        content.Should().Contain("beta", because: "root path '/' should return all entries including /goal/G-1/beta");
    }

    // ── Path prefix search tests ────────────────────────────────────────────

    [Fact]
    public async Task SearchAll_PathPrefix_ScopesToSubtree()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["OAuth2 authentication flow for REST API"], "/api/auth", "API auth notes");
        await Tools().Store(["Authentication research for goal G-5"], "/goal/G-5/research/auth-stuff", "G-5 auth research");

        string result = await Tools().Search("auth", scopes: "/api/");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("auth", because: "search scoped to /api/ should find the api auth entry");
        content.Should().NotContain("G-5", because: "search scoped to /api/ should exclude /goal/G-5 entries");
    }

    [Fact]
    public async Task SearchAll_NoPathPrefix_SearchesEverything()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["API authentication flow"], "/api/auth-flow", "API auth");
        await Tools().Store(["Goal research on authentication"], "/goal/G-7/research/auth-research", "G-7 auth");

        // Search without path prefix (scopes=null) but with scopes="all" to include entity scopes
        string result = await Tools().Search("authentication", scopes: "all");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("auth-flow", because: "unscoped search should return results from /api/");
        content.Should().Contain("auth-research", because: "unscoped search should return results from /goal/G-7/");
    }

    // ── V2 path write + read tests ──────────────────────────────────────────

    [Fact]
    public async Task Store_V2Path_ThenSearch_Finds()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Kubernetes deployment strategies for microservices"], "/infra/k8s-deploy", "K8s deploy notes");

        string result = await Tools().Search("kubernetes", scopes: "all");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("k8s-deploy", because: "search should find entries stored via v2 path");
    }

    [Fact]
    public async Task Store_V2Path_ThenList_Shows()
    {
        using var scope = new TestHelpers.StoreScope();
        // Use a 3+ segment path so it routes through v2 (IsV2PathScope returns true)
        await Tools().Store(["Service mesh configuration details"], "/infra/networking/service-mesh", "Service mesh notes");

        string result = await Tools().List(scopes: "all", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("/infra/networking/service-mesh",
            because: "list should show entries stored via v2 path with v2-formatted name");
    }

    // ── Mixed v1+v2 tests ───────────────────────────────────────────────────

    [Fact]
    public async Task List_MixedV1V2_ShowsBoth()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store via v1 topic:name syntax
        await Tools().Store(["V1 topic content about caching"], "patterns:caching", "V1 caching pattern");
        // Store via v2 /path syntax
        await Tools().Store(["V2 path content about logging"], "/observability/logging", "V2 logging notes");

        string result = await Tools().List(scopes: "all", mode: "full");
        var content = ResponseParser.Parse(result).Content!;

        content.Should().Contain("caching", because: "v1 topic:name entries should appear in list");
        content.Should().Contain("logging", because: "v2 /path entries should appear in list");
    }
}
