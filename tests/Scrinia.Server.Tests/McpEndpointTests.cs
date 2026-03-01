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
    public async Task Mcp_endpoint_with_invalid_store_returns_error()
    {
        var client = _factory.CreateAuthenticatedClient();

        var request = CreateMcpInitRequest("nonexistent");
        var response = await client.SendAsync(request);

        // Invalid store triggers an exception in ConfigureSessionOptions → server error
        ((int)response.StatusCode).Should().BeGreaterOrEqualTo(400);
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
