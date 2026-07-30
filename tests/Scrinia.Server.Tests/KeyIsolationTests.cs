using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class KeyIsolationTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    public KeyIsolationTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task StoreScopedAdmin_CannotSeeOtherStoresKeys()
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();

        // A manage_keys admin scoped ONLY to SecondaryStore ("store-2").
        var (scopedAdminRaw, scopedAdminId, _) = keyStore.CreateKey(
            "store2-admin", [_factory.SecondaryStore], ["manage_keys"], "scoped-admin");

        // A target key scoped ONLY to PrimaryStore ("test-store").
        var (_, primaryOnlyId, _) = keyStore.CreateKey(
            "primary-user", [_factory.PrimaryStore], ["search"], "primary-only");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scopedAdminRaw);

        // List: must NOT include the primary-only key; must include the scoped admin's own key.
        var listResp = await client.GetAsync("/api/v1/keys/");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var keys = await listResp.Content.ReadFromJsonAsync<KeySummaryDto[]>();
        keys!.Select(k => k.Id).Should().Contain(scopedAdminId);
        keys!.Select(k => k.Id).Should().NotContain(primaryOnlyId);

        // Get + Revoke of the out-of-scope key: 404 (no cross-tenant existence leak).
        (await client.GetAsync($"/api/v1/keys/{primaryOnlyId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/v1/keys/{primaryOnlyId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GlobalAdmin_WithWildcardStore_SeesAllKeys()
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (globalRaw, _, _) = keyStore.CreateKey("global-admin", ["*"], ["manage_keys"], "global");
        var (_, someId, _) = keyStore.CreateKey("someone", [_factory.PrimaryStore], ["search"], "target");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", globalRaw);

        var keys = await (await client.GetAsync("/api/v1/keys/"))
            .Content.ReadFromJsonAsync<KeySummaryDto[]>();
        keys!.Select(k => k.Id).Should().Contain(someId);
    }
}
