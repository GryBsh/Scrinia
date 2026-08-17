using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Server.Auth;

namespace Scrinia.Server.Tests;

/// <summary>
/// Finding H6. The embeddings routes lived at /api/v1/plugins/embeddings, a group with no
/// {store} route value — so neither MemoryStoreContext.Current (set only in the MCP session
/// flow, Program.cs:309) nor RequestContext.Store (set only inside the {store} branch,
/// Program.cs:502, its sole assignment in src/) could ever be non-null, and reindex returned
/// 400 unconditionally. These tests pin the store-scoped replacement.
/// </summary>
public sealed class EmbeddingsEndpointsTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EmbeddingsEndpointsTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Status_UnderStoreScopedPath_Answers200()
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/embeddings/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reindex_WithPermission_ResolvesTheStore()
    {
        // On a machine where the Model2Vec model hasn't been downloaded, the real
        // IEmbeddingsAdmin falls back to NullEmbeddingProvider (IsAvailable == false), so the
        // handler's availability check bails out BEFORE ever reaching ctx.Store — the exact
        // line finding H6 is about. A merely-absent "No store context available" string would
        // then pass for the wrong reason. A fake admin that is always available forces the
        // handler past that gate, so a genuine 200 here is real proof the {store} route value
        // resolved, not a proxy any earlier bail-out would also satisfy.
        var fakeAdmin = new FakeEmbeddingsAdmin();

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IEmbeddingsAdmin>(fakeAdmin)));

        // Minted directly against this cloned host's own ApiKeyStore (same pattern as
        // RateLimitTests.RateLimit_PartitionsByUser_NotSharedIp) rather than reusing
        // _factory.TestApiKey, since ConfigureWebHost re-runs for this clone and would
        // otherwise mutate the shared fixture's key out from under the other tests in this class.
        var keyStore = factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(
            "embeddings-reindex-user", [_factory.PrimaryStore], ["reindex"], "reindex-fake-admin");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawKey);

        var resp = await client.PostAsync($"/api/v1/stores/{_factory.PrimaryStore}/embeddings/reindex", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the fake admin is always available, so anything but 200 means the store never resolved");
        fakeAdmin.ReceivedStore.Should().NotBeNull(
            because: "ReindexStoreAsync only runs once ctx.Store is non-null — the {store} route value " +
                     "resolving it is finding H6's fix");
    }

    private sealed class FakeEmbeddingsAdmin : IEmbeddingsAdmin
    {
        public string ProviderName => "fake-admin";
        public bool IsAvailable => true;
        public int Dimensions => 4;
        public double SemanticWeight => 1.0;
        public int TotalVectorCount => 0;
        public string Hardware => "test";

        public IMemoryStore? ReceivedStore { get; private set; }

        public Task<int> ReindexStoreAsync(IMemoryStore store, CancellationToken ct)
        {
            ReceivedStore = store;
            return Task.FromResult(3);
        }
    }

    [Fact]
    public async Task Reindex_WithoutPermission_Is403()
    {
        var (rawKey, storeName) = _factory.CreateRestrictedStoreKey(_factory.PrimaryStore);
        var client = _factory.CreateClientWithKey(rawKey);

        var resp = await client.PostAsync($"/api/v1/stores/{storeName}/embeddings/reindex", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a full-store re-embed is unbounded work and the old group gated nothing");
    }

    [Fact]
    public async Task RetiredPluginPath_NoLongerResolves()
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsync("/api/v1/plugins/embeddings/reindex", null);

        // Be.NotFound, not NotBe.OK: the deleted handler returned 400 unconditionally, so
        // NotBe(OK) passed against the merge base and pinned nothing. Only 404 distinguishes
        // "the route is gone" from "the route is still mapped and rejecting the request".
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "the inline plugin-owned group was replaced by the {store}-scoped routes, "
                   + "so nothing should be mapped at the old path at all");
    }
}
