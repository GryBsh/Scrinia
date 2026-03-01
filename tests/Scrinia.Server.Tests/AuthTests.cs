using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class AuthTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    private readonly string _base;

    public AuthTests(ScriniaServerFactory factory)
    {
        _factory = factory;
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    [Fact]
    public async Task Missing_key_returns_401()
    {
        var client = _factory.CreateClient(); // no auth header
        var resp = await client.GetAsync($"{_base}/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_key_returns_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "scri_invalid_key_that_does_not_exist");
        var resp = await client.GetAsync($"{_base}/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoked_key_returns_401()
    {
        // Create a key, then revoke it
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, keyId, _) = keyStore.CreateKey("revoke-user", [_factory.PrimaryStore], label: "revoke-test");
        keyStore.RevokeKey(keyId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", rawKey);
        var resp = await client.GetAsync($"{_base}/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_key_returns_200()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"{_base}/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Valid_key_unauthorized_store_returns_403()
    {
        // Create a key with access only to store-2
        var (rawKey, _) = _factory.CreateRestrictedStoreKey(_factory.SecondaryStore);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", rawKey);

        // Access test-store (which this key doesn't have access to)
        var resp = await client.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nonexistent_store_returns_404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/stores/nonexistent-store/memories");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
