using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Core.Resilience;
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
    public async Task Health_returns_200_without_auth_status_only()
    {
        var client = _factory.CreateClient(); // no auth header
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
        body.Checks.Should().BeNullOrEmpty("unauthenticated health should not expose check details");
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
    public async Task Health_ready_returns_200_status_only()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
        body.Checks.Should().BeNullOrEmpty("unauthenticated ready should not expose check details");
    }

    [Fact]
    public async Task Health_details_returns_checks_when_authenticated()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/health/details");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
        body.Checks.Should().NotBeNullOrEmpty();
        body.Checks.Should().Contain(c => c.Name == "sqlite");
        body.Checks.Should().Contain(c => c.Name == "store:test-store");
    }

    [Fact]
    public async Task Health_details_returns_401_without_auth()
    {
        var client = _factory.CreateClient(); // no auth
        var resp = await client.GetAsync("/health/details");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_details_includes_circuit_breaker_state()
    {
        // Register a test circuit breaker
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 2));
        CircuitBreakerRegistry.Register("test:health-check", cb);
        try
        {
            var client = _factory.CreateAuthenticatedClient();
            var resp = await client.GetAsync("/health/details");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
            body.Should().NotBeNull();
            body!.Checks.Should().Contain(c => c.Name == "circuit-breaker:test:health-check" && c.Status == "closed");

            // Trip the circuit breaker
            cb.RecordFailure();
            cb.RecordFailure();
            cb.State.Should().Be(CircuitState.Open);

            var resp2 = await client.GetAsync("/health/details");
            var body2 = await resp2.Content.ReadFromJsonAsync<HealthResponse>();
            body2!.Checks.Should().Contain(c => c.Name == "circuit-breaker:test:health-check" && c.Status == "open");
        }
        finally
        {
            CircuitBreakerRegistry.Remove("test:health-check");
        }
    }
}
