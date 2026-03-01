using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class PermissionTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public PermissionTests(ScriniaServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Key_without_manage_keys_returns_403_on_key_endpoints()
    {
        // Create a key without manage_keys permission
        var (rawKey, _) = _factory.CreateRestrictedStoreKey(_factory.PrimaryStore);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", rawKey);

        var resp = await client.GetAsync("/api/v1/keys/");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Key_with_manage_keys_returns_200_on_key_endpoints()
    {
        var client = _factory.CreateAuthenticatedClient(); // has manage_keys
        var resp = await client.GetAsync("/api/v1/keys/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cannot_grant_stores_caller_does_not_have()
    {
        // Create an admin-like client that only has access to test-store
        var (adminKey, _) = _factory.CreateRestrictedStoreKey(_factory.PrimaryStore);

        // Actually, we need manage_keys too. Let's use the main test key which has both stores + manage_keys.
        var client = _factory.CreateAuthenticatedClient();

        // Create a key that only has access to test-store + manage_keys
        var createReq = new CreateKeyRequest(
            "limited-admin", [_factory.PrimaryStore], ["manage_keys"]);
        var createResp = await client.PostAsJsonAsync("/api/v1/keys/", createReq);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResp.Content.ReadFromJsonAsync<CreateKeyResponse>();

        // Now use that limited key to try to grant access to store-2 (which it doesn't have)
        var limitedClient = _factory.CreateClient();
        limitedClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created!.RawKey);

        var escalationReq = new CreateKeyRequest(
            "escalation-target", [_factory.SecondaryStore]);
        var escalationResp = await limitedClient.PostAsJsonAsync("/api/v1/keys/", escalationReq);
        escalationResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cannot_grant_permissions_caller_does_not_have()
    {
        // Create a key with manage_keys but only test-store
        var client = _factory.CreateAuthenticatedClient();
        var createReq = new CreateKeyRequest(
            "no-manage-user", [_factory.PrimaryStore]); // no permissions
        var createResp = await client.PostAsJsonAsync("/api/v1/keys/", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<CreateKeyResponse>();

        // This key doesn't have manage_keys, so it can't even access /api/v1/keys
        // But let's test that even if it could, it couldn't grant manage_keys
        // We'll use the admin key to create a key with manage_keys but without the target permission
        var limitedAdminReq = new CreateKeyRequest(
            "limited-admin-2", [_factory.PrimaryStore], ["manage_keys"]);
        var limitedAdminResp = await client.PostAsJsonAsync("/api/v1/keys/", limitedAdminReq);
        var limitedAdmin = await limitedAdminResp.Content.ReadFromJsonAsync<CreateKeyResponse>();

        var limitedClient = _factory.CreateClient();
        limitedClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", limitedAdmin!.RawKey);

        // Try to grant a permission the limited admin doesn't have (e.g. "super_admin")
        var escalationReq = new CreateKeyRequest(
            "escalation-target-2", [_factory.PrimaryStore], ["super_admin"]);
        var escalationResp = await limitedClient.PostAsJsonAsync("/api/v1/keys/", escalationReq);
        escalationResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
