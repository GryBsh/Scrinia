using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class StoreIsolationTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public StoreIsolationTests(ScriniaServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Different_stores_have_isolated_data()
    {
        // Client with access to both stores
        var client = _factory.CreateAuthenticatedClient();
        string store1Base = $"/api/v1/stores/{_factory.PrimaryStore}";
        string store2Base = $"/api/v1/stores/{_factory.SecondaryStore}";

        // Store memory in store 1
        var req = new StoreRequest(["Store 1 secret data"], "store1-secret");
        var storeResp = await client.PostAsJsonAsync($"{store1Base}/memories", req);
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Store 1 can see it
        var showResp1 = await client.GetAsync($"{store1Base}/memories/store1-secret");
        showResp1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Store 2 cannot see it
        var showResp2 = await client.GetAsync($"{store2Base}/memories/store1-secret");
        showResp2.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Store memory in store 2
        var req2 = new StoreRequest(["Store 2 data"], "store2-only");
        var storeResp2 = await client.PostAsJsonAsync($"{store2Base}/memories", req2);
        storeResp2.StatusCode.Should().Be(HttpStatusCode.Created);

        // Store 2 can see it
        var showResp3 = await client.GetAsync($"{store2Base}/memories/store2-only");
        showResp3.StatusCode.Should().Be(HttpStatusCode.OK);

        // Store 1 cannot see it
        var showResp4 = await client.GetAsync($"{store1Base}/memories/store2-only");
        showResp4.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lists_are_store_scoped()
    {
        var client = _factory.CreateAuthenticatedClient();
        string store1Base = $"/api/v1/stores/{_factory.PrimaryStore}";
        string store2Base = $"/api/v1/stores/{_factory.SecondaryStore}";

        // Store in each store with unique names
        await client.PostAsJsonAsync($"{store1Base}/memories",
            new StoreRequest(["Data A"], "isolation-a"));
        await client.PostAsJsonAsync($"{store2Base}/memories",
            new StoreRequest(["Data B"], "isolation-b"));

        // List store 1
        var list1 = await client.GetFromJsonAsync<ListResponse>($"{store1Base}/memories");
        list1.Should().NotBeNull();
        list1!.Memories.Should().Contain(m => m.QualifiedName == "isolation-a");
        list1.Memories.Should().NotContain(m => m.QualifiedName == "isolation-b");

        // List store 2
        var list2 = await client.GetFromJsonAsync<ListResponse>($"{store2Base}/memories");
        list2.Should().NotBeNull();
        list2!.Memories.Should().Contain(m => m.QualifiedName == "isolation-b");
        list2.Memories.Should().NotContain(m => m.QualifiedName == "isolation-a");
    }
}
