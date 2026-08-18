using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests.Auth;

/// <summary>
/// Characterization tests for the route -> permission mapping. Written against the
/// hand-written handler guards and expected to hold unchanged once that mapping moves
/// into endpoint metadata enforced by a single filter: this task must change where the
/// decision is expressed, never which caller gets a 403 for which route, nor what the
/// 403 body says.
/// </summary>
public sealed class EndpointPermissionTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EndpointPermissionTests(ScriniaServerFactory factory) => _factory = factory;

    // Enumerated from the route table in the five *Endpoints.cs files, not from the set of
    // handlers that happen to call HasPermission today. {store} is substituted with the
    // factory's PrimaryStore — a store that actually exists, so the store-resolution
    // middleware cannot pre-empt the permission check with its own 404.
    public static TheoryData<string, string, string> GatedRoutes() => new()
    {
        { "POST",   "/api/v1/stores/{store}/memories",                     Permissions.Store },
        { "GET",    "/api/v1/stores/{store}/memories",                     Permissions.Search },
        { "GET",    "/api/v1/stores/{store}/memories/x",                   Permissions.Read },
        { "GET",    "/api/v1/stores/{store}/memories/x/entry",             Permissions.Read },
        { "PATCH",  "/api/v1/stores/{store}/memories/x",                   Permissions.Store },
        { "DELETE", "/api/v1/stores/{store}/memories/x",                   Permissions.Forget },
        { "POST",   "/api/v1/stores/{store}/memories/x/append",            Permissions.Append },
        { "POST",   "/api/v1/stores/{store}/memories/x/copy",              Permissions.Copy },
        { "POST",   "/api/v1/stores/{store}/memories/x/copy-from-client",  Permissions.Copy },
        { "GET",    "/api/v1/stores/{store}/memories/x/chunks/0",          Permissions.Read },
        { "GET",    "/api/v1/stores/{store}/search",                       Permissions.Search },
        { "POST",   "/api/v1/stores/{store}/search",                       Permissions.Search },
        { "POST",   "/api/v1/stores/{store}/export",                       Permissions.Export },
        { "POST",   "/api/v1/stores/{store}/import",                       Permissions.Import },
        { "GET",    "/api/v1/stores/{store}/embeddings/status",            Permissions.Read },
        { "GET",    "/api/v1/stores/{store}/embeddings/settings",          Permissions.Read },
        { "POST",   "/api/v1/stores/{store}/embeddings/reindex",           Permissions.Reindex },
        { "GET",    "/api/v1/stores/{store}/chat/providers",               Permissions.Chat },
        // The conjunctive case. ChatEndpoints requires chat AND read AND search, so it needs
        // a row per permission — this is the only route in the table whose 403 can be
        // triggered three different ways, and the riskiest transformation in this task.
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Chat },
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Read },
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Search },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Route_Returns403_WhenCallerLacksItsPermission(string method, string path, string required)
    {
        // A key holding every permission EXCEPT the one under test. This is the probe that
        // makes the assertion sharp: a route that ignored its declaration entirely would
        // answer 200/400/404 here, not 403.
        string[] granted = Permissions.All.Where(p => p != required).ToArray();
        var client = _factory.CreateClientWithPermissions(granted);

        var response = await SendAsync(client, method, Resolve(path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{method} {path} declares {required}");
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Route_DoesNotReturn403_WhenCallerHoldsItsPermission(string method, string path, string required)
    {
        // The inverse probe. Without it, a filter that returned 403 unconditionally would
        // pass the test above for every row.
        var client = _factory.CreateClientWithPermissions([.. Permissions.All]);

        var response = await SendAsync(client, method, Resolve(path));

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            $"{method} {path} should admit a caller holding {required}");
    }

    [Fact]
    public async Task ForbiddenBody_IsUnchanged()
    {
        var client = _factory.CreateClientWithPermissions();

        var response = await SendAsync(client, "POST", Resolve("/api/v1/stores/{store}/memories"));

        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Permission 'store' required.",
            "clients parse this body; moving where the check lives must not change what it says");
    }

    [Fact]
    public async Task ForbiddenBody_ForTheConjunctiveRoute_IsUnchanged()
    {
        // The conjunctive route's denial names all three permissions in one sentence rather
        // than the first one missing. Pinned separately from the single-permission body
        // because it is the one wording a consolidation could quietly rewrite.
        var client = _factory.CreateClientWithPermissions();

        var response = await SendAsync(client, "POST", Resolve("/api/v1/stores/{store}/chat/"));

        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Permissions 'chat', 'read', and 'search' are required.",
            "clients parse this body; moving where the check lives must not change what it says");
    }

    private string Resolve(string path) => path.Replace("{store}", _factory.PrimaryStore);

    /// <summary>Test-file scaffolding: issue <paramref name="method"/> at <paramref name="path"/>
    /// with an empty JSON body. GET carries no body — none of the GET routes read one, and a
    /// GET with content is a request shape no client sends.</summary>
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!HttpMethod.Get.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }
}
