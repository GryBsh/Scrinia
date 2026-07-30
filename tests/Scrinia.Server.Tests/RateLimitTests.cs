using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
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

    // Two distinct users share the same loopback IP under the test server. If limiting
    // partitions by user (correct), exhausting user A does not rate-limit user B. Uses a
    // low PermitLimit override so the test is fast and deterministic instead of relying on
    // the default 100/min "api" policy.
    [Fact]
    public async Task RateLimit_PartitionsByUser_NotSharedIp()
    {
        const int Limit = 3;

        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Scrinia:RateLimit:PermitLimit", Limit.ToString()));

        var keyStore = factory.Services.GetRequiredService<ApiKeyStore>();
        var (aRaw, _, _) = keyStore.CreateKey("rl-user-a", [_factory.PrimaryStore], ["search"], "a");
        var (bRaw, _, _) = keyStore.CreateKey("rl-user-b", [_factory.PrimaryStore], ["search"], "b");

        var url = $"/api/v1/stores/{_factory.PrimaryStore}/memories";

        var a = factory.CreateClient();
        a.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aRaw);
        HttpResponseMessage last = null!;
        for (int i = 0; i < Limit + 5; i++)
            last = await a.GetAsync(url);
        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "user A should now be limited");

        var b = factory.CreateClient();
        b.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bRaw);
        var bResp = await b.GetAsync(url);
        bResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "user B shares the test server's loopback IP with user A but has a distinct partition key, so should be unaffected");
    }
}
