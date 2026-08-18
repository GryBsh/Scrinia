using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    //
    // The fourth column is the EXACT denial message the route produced before the mapping
    // moved. It is what pins the mapping in the over-declaring direction: the two status-code
    // probes below both still pass if a route declares a SUPERSET of its true requirement
    // (the negative probe still 403s, the all-permissions positive probe still does not), but
    // an extra permission changes the message, so this column catches it.
    public static TheoryData<string, string, string, string> GatedRoutes() => new()
    {
        { "POST",   "/api/v1/stores/{store}/memories",                     Permissions.Store,   "Permission 'store' required." },
        { "GET",    "/api/v1/stores/{store}/memories",                     Permissions.Search,  "Permission 'search' required." },
        { "GET",    "/api/v1/stores/{store}/memories/x",                   Permissions.Read,    "Permission 'read' required." },
        { "GET",    "/api/v1/stores/{store}/memories/x/entry",             Permissions.Read,    "Permission 'read' required." },
        { "PATCH",  "/api/v1/stores/{store}/memories/x",                   Permissions.Store,   "Permission 'store' required." },
        { "DELETE", "/api/v1/stores/{store}/memories/x",                   Permissions.Forget,  "Permission 'forget' required." },
        { "POST",   "/api/v1/stores/{store}/memories/x/append",            Permissions.Append,  "Permission 'append' required." },
        { "POST",   "/api/v1/stores/{store}/memories/x/copy",              Permissions.Copy,    "Permission 'copy' required." },
        { "POST",   "/api/v1/stores/{store}/memories/x/copy-from-client",  Permissions.Copy,    "Permission 'copy' required." },
        { "GET",    "/api/v1/stores/{store}/memories/x/chunks/0",          Permissions.Read,    "Permission 'read' required." },
        { "GET",    "/api/v1/stores/{store}/search",                       Permissions.Search,  "Permission 'search' required." },
        { "POST",   "/api/v1/stores/{store}/search",                       Permissions.Search,  "Permission 'search' required." },
        { "POST",   "/api/v1/stores/{store}/export",                       Permissions.Export,  "Permission 'export' required." },
        { "POST",   "/api/v1/stores/{store}/import",                       Permissions.Import,  "Permission 'import' required." },
        { "GET",    "/api/v1/stores/{store}/embeddings/status",            Permissions.Read,    "Permission 'read' required." },
        { "GET",    "/api/v1/stores/{store}/embeddings/settings",          Permissions.Read,    "Permission 'read' required." },
        { "POST",   "/api/v1/stores/{store}/embeddings/reindex",           Permissions.Reindex, "Permission 'reindex' required." },
        { "GET",    "/api/v1/stores/{store}/chat/providers",               Permissions.Chat,    "Permission 'chat' required." },
        // The conjunctive case. ChatEndpoints requires chat AND read AND search, so it needs
        // a row per permission — this is the only route in the table whose 403 can be
        // triggered three different ways, and the riskiest transformation in this task. Its
        // denial names all three in one sentence no matter which is absent, which is what the
        // guard it replaced did.
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Chat,    "Permissions 'chat', 'read', and 'search' are required." },
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Read,    "Permissions 'chat', 'read', and 'search' are required." },
        { "POST",   "/api/v1/stores/{store}/chat/",                        Permissions.Search,  "Permissions 'chat', 'read', and 'search' are required." },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Route_Returns403_WhenCallerLacksItsPermission(
        string method, string path, string required, string denial)
    {
        _ = denial;

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
    public async Task Route_DoesNotReturn403_WhenCallerHoldsItsPermission(
        string method, string path, string required, string denial)
    {
        _ = denial;

        // The inverse probe. Without it, a filter that returned 403 unconditionally would
        // pass the test above for every row.
        var client = _factory.CreateClientWithPermissions([.. Permissions.All]);

        var response = await SendAsync(client, method, Resolve(path));

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            $"{method} {path} should admit a caller holding {required}");
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task ForbiddenBody_IsByteIdentical(
        string method, string path, string required, string denial)
    {
        _ = required;

        // Compared byte for byte against the whole response body, not searched for a
        // substring: clients parse this document, so a change to the property name, the
        // casing, or the serializer's shape is as breaking as a change to the sentence, and
        // a substring check would see none of them.
        var client = _factory.CreateClientWithPermissions();

        var response = await SendAsync(client, method, Resolve(path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"a caller holding no permissions at all must be denied {method} {path}");
        string body = await response.Content.ReadAsStringAsync();
        // Built by concatenation, not by re-serializing: the server does not escape the
        // apostrophes these messages are full of, and running the expectation through a
        // serializer would quietly agree with whatever escaping the server chose. Spelling
        // the bytes out is what makes an encoder change visible.
        body.Should().Be("{\"error\":\"" + denial + "\"}",
            $"{method} {path} answered this exact document before the mapping moved");
    }

    /// <summary>
    /// Proves ENFORCEMENT, not declaration. Every other test in this class is driven by a
    /// hand-maintained table, so all of them together still cannot say whether the endpoints
    /// the running app actually built carry the metadata — nor whether metadata implies a
    /// filter, since filters are not metadata and no startup validator can see them. This
    /// enumerates the booted app's own route table and puts a real request through every
    /// endpoint that claims to be gated. A declaration attached without its filter fails here,
    /// and so does a route added after this task without one.
    /// </summary>
    [Fact]
    public async Task EveryEndpointDeclaringPermissions_ActuallyEnforcesThem()
    {
        var client = _factory.CreateClientWithPermissions();
        var declared = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionsMetadata>() is not null)
            .ToArray();

        declared.Should().HaveCountGreaterThanOrEqualTo(19,
            "the 19 routes this task moved off hand-written guards must all carry the declaration "
            + "in the app that actually boots, not merely in the source the table above was read from");

        var unenforced = new List<string>();
        foreach (var endpoint in declared)
        {
            string method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods[0] ?? "GET";
            string path = Concretize(endpoint.RoutePattern.RawText!);

            var response = await SendAsync(client, method, path);
            if (response.StatusCode != HttpStatusCode.Forbidden)
                unenforced.Add($"{method} {path} -> {(int)response.StatusCode}");
        }

        unenforced.Should().BeEmpty(
            "every endpoint carrying RequiredPermissionsMetadata must deny a caller holding no "
            + "permissions — the metadata is what the startup coverage validator trusts");
    }

    /// <summary>Turns a route pattern into a request path: the store parameter becomes a store
    /// that exists, int-constrained parameters become 0, everything else becomes a name that
    /// does not exist. Only the permission outcome is under test, so a 404 past the filter
    /// would be a fine answer for the request itself — it is a 404 INSTEAD of the 403 that is
    /// the failure this reports.</summary>
    private string Concretize(string pattern) =>
        Regex.Replace(pattern, @"\{([^}]+)\}", m =>
        {
            string token = m.Groups[1].Value;
            string name = token.Split(':', '=', '?')[0].TrimStart('*');
            return name == StoreRouteParameter ? _factory.PrimaryStore
                : token.Contains(":int", StringComparison.Ordinal) ? "0"
                : "x";
        });

    // The {store} route-value key. Spelled here rather than referenced from the server's
    // internal StoreSelectorKey so this file states its own coupling to the route templates.
    private const string StoreRouteParameter = "store";

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
