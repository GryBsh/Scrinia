using FluentAssertions;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class OpenApiTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public OpenApiTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_spec_returns_valid_json()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"openapi\"");
    }

    [Fact]
    public async Task Scalar_ui_returns_html()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("html");
    }
}
