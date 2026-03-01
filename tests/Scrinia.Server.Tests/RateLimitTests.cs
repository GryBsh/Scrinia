using FluentAssertions;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class RateLimitTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public RateLimitTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Exceeding_rate_limit_returns_429()
    {
        var client = _factory.CreateAuthenticatedClient();
        var store = _factory.PrimaryStore;
        var url = $"/api/v1/stores/{store}/memories";

        // Send 101 requests — sliding window allows 100 per minute
        var tasks = Enumerable.Range(0, 101)
            .Select(_ => client.GetAsync(url))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        // At least one should be 429 Too Many Requests
        responses.Should().Contain(r => (int)r.StatusCode == 429,
            "exceeding 100 requests per minute should trigger rate limiting");
    }

    [Fact]
    public async Task Health_endpoints_are_not_rate_limited()
    {
        var client = _factory.CreateClient();

        // Health endpoints should always respond even under load
        var tasks = Enumerable.Range(0, 120)
            .Select(_ => client.GetAsync("/health/live"))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode,
            "health endpoints should not be rate limited");
    }
}
