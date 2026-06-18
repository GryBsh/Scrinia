using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class McpEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public McpEndpointTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Mcp_endpoint_returns_401_without_auth()
    {
        var client = _factory.CreateClient();

        var request = CreateMcpInitRequest(_factory.PrimaryStore);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_endpoint_with_auth_accepts_initialize()
    {
        var client = _factory.CreateAuthenticatedClient();

        var request = CreateMcpInitRequest(_factory.PrimaryStore);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("serverInfo");
    }

    [Fact]
    public async Task Mcp_initialize_advertises_instructions_prompts_and_resources()
    {
        // Guards the per-client-integration additions to the HTTP MCP server: the
        // ServerInstructions field and the prompts/resources capabilities must be wired
        // and advertised in the initialize result (WithPrompts/WithResources registered).
        var client = _factory.CreateAuthenticatedClient();

        var request = CreateMcpInitRequest(_factory.PrimaryStore);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // ServerInstructions (the shared Scrinia primer) is surfaced to the client.
        body.Should().Contain("instructions");
        body.Should().Contain("persistent project-scoped memory",
            "the initialize result must carry the Scrinia primer as the server instructions");
        // Prompt + resource capabilities are advertised now that they're registered.
        body.Should().Contain("prompts");
        body.Should().Contain("resources");
    }

    [Fact]
    public async Task Mcp_endpoint_with_invalid_store_returns_error()
    {
        var client = _factory.CreateAuthenticatedClient();

        var request = CreateMcpInitRequest("nonexistent");
        var response = await client.SendAsync(request);

        // Invalid store triggers an exception in ConfigureSessionOptions → server error
        ((int)response.StatusCode).Should().BeGreaterOrEqualTo(400);
    }

    [Fact]
    public async Task Mcp_toolsCall_rejected_when_key_lacks_operation_permission()
    {
        // A key with store access but NO permissions must be rejected by the per-op auth filter
        // when it calls a gated tool (memory_remember requires "store"). This also proves the
        // filter extracts the tool name from the call params — if it didn't, gating would silently
        // no-op and the call would proceed.
        var (restrictedKey, store) = _factory.CreateRestrictedStoreKey(_factory.PrimaryStore);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restrictedKey);

        string? sessionId = await InitializeSessionAsync(client, store);
        sessionId.Should().NotBeNullOrEmpty("initialize must succeed and return a session id (store access is granted)");

        string body = await CallToolAsync(client, store, sessionId!,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"memory_remember","arguments":{"path":"x","content":["y"]}}}""");

        // The per-op filter rejects the call: JSON-RPC error, no result (tool never executed).
        // (The SDK sanitizes the filter's exception message at this layer, so we assert on the
        // error/result shape rather than the message text — see the paired "allowed" test, which
        // runs the identical call with a permissioned key and gets a result instead.)
        body.Should().Contain("\"error\"",
            "a permission-less key must be denied the memory_remember tool over MCP");
        body.Should().NotContain("\"result\"");
    }

    [Fact]
    public async Task Mcp_toolsCall_allowed_when_key_has_operation_permission()
    {
        // The full test key holds every permission — the same call must NOT be permission-denied.
        var client = _factory.CreateAuthenticatedClient();
        string store = _factory.PrimaryStore;

        string? sessionId = await InitializeSessionAsync(client, store);
        sessionId.Should().NotBeNullOrEmpty();

        string body = await CallToolAsync(client, store, sessionId!,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"memory_remember","arguments":{"path":"perm-ok","content":["hello"]}}}""");

        // Identical call to the rejected test, but with a fully-permissioned key: it passes the filter,
        // executes, and returns a result. The error-vs-result difference IS the auth gate.
        body.Should().Contain("\"result\"",
            "a fully-permissioned key's memory_remember call must succeed over MCP");
        body.Should().NotContain("\"error\"");
    }

    /// <summary>POSTs initialize, returns the Mcp-Session-Id header (or null).</summary>
    private async Task<string?> InitializeSessionAsync(HttpClient client, string store)
    {
        var response = await client.SendAsync(CreateMcpInitRequest(store));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : null;
    }

    /// <summary>Sends the initialized notification then a tools/call, returning the call response body.</summary>
    private async Task<string> CallToolAsync(HttpClient client, string store, string sessionId, string callJson)
    {
        // Required handshake step: tell the server the client is initialized.
        using (var notif = BuildMcpRequest(store, sessionId,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""))
            await client.SendAsync(notif);

        using var call = BuildMcpRequest(store, sessionId, callJson);
        var response = await client.SendAsync(call);
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpRequestMessage BuildMcpRequest(string store, string sessionId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp?store={store}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static HttpRequestMessage CreateMcpInitRequest(string store)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp?store={store}");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""",
            Encoding.UTF8, "application/json");
        // MCP Streamable HTTP requires Accept header
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }
}
