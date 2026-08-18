using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 9, D-5: the old <c>scopes</c>/<c>excludeTopics</c> bound was a single 1KB cap on the whole
/// comma-joined string (<c>scopes is {{ Length: &gt; 1024 }}</c>). Converted to arrays, that had to
/// become a COUNT bound and a PER-ELEMENT length bound (<c>ValidateStringArrayBounds</c> in
/// MemoryEndpoints.cs) — losing either would leave the total unbounded again, which is exactly the
/// silent-regression shape this branch exists to catch. These tests pin both halves of the bound,
/// for both <c>scopes</c> and <c>excludeTopics</c>, and the within-bound cases that prove the bound
/// is not accidentally zero.
/// </summary>
public sealed class SearchScopeArrayBoundsTests : IClassFixture<ScriniaServerFactory>
{
    private readonly HttpClient _client;
    private readonly string _base;

    public SearchScopeArrayBoundsTests(ScriniaServerFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    [Fact]
    public async Task Search_Post_ScopesWith65Entries_ReturnsBadRequest()
    {
        var tooMany = Enumerable.Range(0, 65).Select(i => $"scope-{i}").ToArray();
        var req = new SearchRequest("beta", Limit: 10, Scopes: tooMany);

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "65 scope entries is a resource-exhaustion vector the count bound must reject");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("scopes",
            because: "a bare 400 assertion would also pass if rejected for an unrelated reason");
    }

    [Fact]
    public async Task Search_Post_ScopesWith64Entries_ReturnsOk()
    {
        var withinBound = Enumerable.Range(0, 64).Select(i => $"scope-{i}").ToArray();
        var req = new SearchRequest("beta", Limit: 10, Scopes: withinBound);

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "64 entries is comfortably inside the count bound; without this test a bound " +
                     "of zero would still pass the oversized-case test above");
    }

    [Fact]
    public async Task Search_Post_AScopeEntryOver1024Characters_ReturnsBadRequest()
    {
        var req = new SearchRequest("beta", Limit: 10, Scopes: [new string('a', 1025)]);

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "one 1025-character scope name is what a count-only bound would let through — " +
                     "the total must still be bounded per element, not just by how many there are");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("scopes");
    }

    [Fact]
    public async Task Search_Post_AScopeEntryAt1024Characters_ReturnsOk()
    {
        var req = new SearchRequest("beta", Limit: 10, Scopes: [new string('a', 1024)]);

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "exactly 1024 characters is comfortably inside the per-element bound; without " +
                     "this test a bound of zero would still pass the oversized-case test above");
    }

    [Fact]
    public async Task Search_Post_ExcludeTopicsWith65Entries_ReturnsBadRequest()
    {
        // Same bound, applied to excludeTopics independently — a fix that only covered scopes
        // would leave excludeTopics's half of the old string cap silently dropped.
        var tooMany = Enumerable.Range(0, 65).Select(i => $"topic-{i}").ToArray();
        var req = new SearchRequest("beta", Limit: 10, ExcludeTopics: tooMany);

        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "excludeTopics must carry its own count bound, independently of scopes'");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("excludeTopics");
    }

    /// <summary>Task 9, D-4, pinned at the REST layer: an explicit <c>"scopes": []</c> JSON array
    /// in the POST body must behave identically to omitting the field, not narrow the search or
    /// trip a bound. Core already guarantees this (FileMemoryStoreTests/SearchCandidatesHintTests),
    /// but the wire hop is a layer of its own — JSON model binding could in principle deserialize
    /// an empty array differently from a missing field, and this is where that would show up.</summary>
    [Fact]
    public async Task Search_Post_EmptyScopesArray_BehavesIdenticallyToOmittingIt()
    {
        var seed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["gamma content"], "gamma-probe", "gamma"));
        seed.EnsureSuccessStatusCode();

        var withEmpty = await _client.PostAsJsonAsync($"{_base}/search",
            new SearchRequest("gamma", Limit: 10, Scopes: [], ExcludeTopics: []));
        var withOmitted = await _client.PostAsJsonAsync($"{_base}/search",
            new SearchRequest("gamma", Limit: 10));

        withEmpty.StatusCode.Should().Be(HttpStatusCode.OK);
        var emptyBody = await withEmpty.Content.ReadFromJsonAsync<SearchResponse>();
        var omittedBody = await withOmitted.Content.ReadFromJsonAsync<SearchResponse>();
        emptyBody!.Results.Select(r => r.Name).Should().BeEquivalentTo(
            omittedBody!.Results.Select(r => r.Name),
            because: "an empty scopes/excludeTopics array is not a narrower filter than omitting them");
        emptyBody.Results.Should().Contain(r => r.Name.Contains("gamma-probe", StringComparison.Ordinal));
    }

    /// <summary>Task 9 fix round 1, F-1: ASP.NET Core binds an empty <c>?scopes=</c> query value to
    /// <c>string[] { "" }</c>, NOT an empty array — a different shape from the POST body's
    /// <c>Scopes: []</c>, which the test above already covered and which is why this GET-side
    /// regression went unnoticed. <c>ResolveReadScopes</c>'s old <c>is {{ Count: &gt; 0 }}</c> guard
    /// took the filter branch for a one-element list containing only <c>""</c>, and
    /// <c>NormalizeScopeFilters("")</c> resolved that to <c>["local"]</c> — silently narrowing
    /// "no filter" down to the local scope.</summary>
    [Fact]
    public async Task Search_Get_EmptyScopesParam_BehavesIdenticallyToOmittingIt()
    {
        var seed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["zeta content"], "/zeta-topic/zeta-probe", "zeta"));
        seed.EnsureSuccessStatusCode();

        var withEmptyParam = await _client.GetAsync($"{_base}/search?q=zeta&limit=10&scopes=");
        var withOmitted = await _client.GetAsync($"{_base}/search?q=zeta&limit=10");

        withEmptyParam.StatusCode.Should().Be(HttpStatusCode.OK);
        var emptyBody = await withEmptyParam.Content.ReadFromJsonAsync<SearchResponse>();
        var omittedBody = await withOmitted.Content.ReadFromJsonAsync<SearchResponse>();

        emptyBody!.Results.Select(r => r.Name).Should().BeEquivalentTo(
            omittedBody!.Results.Select(r => r.Name),
            because: "a literal `?scopes=` with no value must behave exactly like omitting scopes " +
                     "entirely, not silently narrow the search to the local scope");
        emptyBody.Results.Should().Contain(r => r.Name.Contains("zeta-probe", StringComparison.Ordinal),
            because: "zeta-probe lives in a topic scope, not local — a narrowed search would miss it");
    }

    /// <summary>Task 9 fix round 1, F-2: <c>{"scopes":[null]}</c> is schema-valid JSON for
    /// <c>string[]</c> regardless of the non-nullable element annotation, and used to reach an
    /// unguarded <c>.Trim()</c> a few frames downstream, throwing a <c>NullReferenceException</c>
    /// that the server answered as a 500 <c>{"error":"An internal error occurred."}</c>. Must never
    /// 500 — normalizing the null element away to "no filter" is an acceptable outcome (it is what
    /// this fix does), a 4xx would also be acceptable; a 500 is the one forbidden answer.</summary>
    [Fact]
    public async Task Search_Post_ScopesContainingOnlyANullElement_NeverReturns500()
    {
        var seed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["theta content"], "/theta-topic/theta-probe", "theta"));
        seed.EnsureSuccessStatusCode();

        var req = new SearchRequest("theta", Limit: 10, Scopes: new string[] { null! });
        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            because: "a null scope element must never reach a bare .Trim() and crash the request");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a scopes array containing only a null element normalizes away to \"no filter\", " +
                     "the same as omitting scopes entirely");
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().Contain(r => r.Name.Contains("theta-probe", StringComparison.Ordinal));
    }

    /// <summary>Task 9 fix round 1, F-2, the mixed-array sibling: a real scope name alongside a null
    /// element must not crash either — the null is dropped, the real entry still filters.</summary>
    [Fact]
    public async Task Search_Post_ScopesWithARealEntryAndANullElement_NeverReturns500()
    {
        var req = new SearchRequest("beta", Limit: 10, Scopes: new string[] { "local", null! });
        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            because: "a mix of a real scope and a null element must never crash the request");
    }

    /// <summary>Task 9 fix round 1, F-3: the old comma-string encoding dropped blank entries
    /// (<c>RemoveEmptyEntries | TrimEntries</c>), so <c>",api"</c> became <c>["api"]</c>. The new
    /// array path passed blanks straight to <c>NormalizeScopeFilters</c>, which maps <c>""</c> to
    /// <c>["local"]</c> — so <c>scopes: ["", "api"]</c> silently ADDED the local scope instead of
    /// resolving exactly as <c>["api"]</c>.</summary>
    [Fact]
    public async Task Search_Post_ScopesWithABlankAndANonBlankEntry_ResolvesExactlyAsTheNonBlankEntry()
    {
        var localSeed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["kappa content in local"], "kappa-local-probe"));
        localSeed.EnsureSuccessStatusCode();
        var apiSeed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["kappa content in api"], "/api/kappa-api-probe"));
        apiSeed.EnsureSuccessStatusCode();

        var req = new SearchRequest("kappa", Limit: 10, Scopes: ["", "api"]);
        var resp = await _client.PostAsJsonAsync($"{_base}/search", req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResponse>();
        body!.Results.Should().Contain(r => r.Name.Contains("kappa-api-probe", StringComparison.Ordinal));
        body.Results.Should().NotContain(r => r.Name.Contains("kappa-local-probe", StringComparison.Ordinal),
            because: "a blank entry must be DROPPED, not resolved to the local scope — " +
                     "[\"\", \"api\"] must behave exactly as [\"api\"]");
    }
}
