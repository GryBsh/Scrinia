using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class HealthEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public HealthEndpointTests(ScriniaServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_returns_200_without_auth()
    {
        var client = _factory.CreateClient(); // no auth header
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
        body.Checks.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Health_live_returns_200()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Health_ready_returns_200_with_checks()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
        body.Checks.Should().NotBeNullOrEmpty();
        body.Checks.Should().Contain(c => c.Name == "sqlite");
        body.Checks.Should().Contain(c => c.Name == "store:test-store");
    }
}
