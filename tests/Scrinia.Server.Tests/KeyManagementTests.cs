using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class KeyManagementTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    private readonly HttpClient _client;

    public KeyManagementTests(ScriniaServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(); // has manage_keys
    }

    [Fact]
    public async Task Create_key_returns_raw_key_and_metadata()
    {
        var req = new CreateKeyRequest("new-user", [_factory.PrimaryStore], null, "test-label");
        var resp = await _client.PostAsJsonAsync("/api/v1/keys/", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<CreateKeyResponse>();
        body.Should().NotBeNull();
        body!.RawKey.Should().StartWith("scri_");
        body.UserId.Should().Be("new-user");
        body.Stores.Should().Contain(_factory.PrimaryStore);
    }

    [Fact]
    public async Task List_keys_shows_created_keys()
    {
        var resp = await _client.GetAsync("/api/v1/keys/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var keys = await resp.Content.ReadFromJsonAsync<KeySummaryDto[]>();
        keys.Should().NotBeNull();
        keys!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_key_by_id_returns_details()
    {
        var resp = await _client.GetAsync($"/api/v1/keys/{_factory.TestKeyId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var key = await resp.Content.ReadFromJsonAsync<KeySummaryDto>();
        key.Should().NotBeNull();
        key!.Id.Should().Be(_factory.TestKeyId);
        key.UserId.Should().Be(_factory.TestUserId);
    }

    [Fact]
    public async Task Revoke_key_prevents_subsequent_use()
    {
        // Create a new key
        var createReq = new CreateKeyRequest("revoke-test-user", [_factory.PrimaryStore], ["search"], "revoke-test");
        var createResp = await _client.PostAsJsonAsync("/api/v1/keys/", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<CreateKeyResponse>();

        // Verify the key works
        var tempClient = _factory.CreateClient();
        tempClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created!.RawKey);
        var checkResp = await tempClient.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/memories");
        checkResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Revoke it
        var revokeResp = await _client.DeleteAsync($"/api/v1/keys/{created.KeyId}");
        revokeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now it should fail
        var failResp = await tempClient.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/memories");
        failResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
