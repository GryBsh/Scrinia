using System.Net;
using FluentAssertions;

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
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsync($"/api/v1/stores/{_factory.PrimaryStore}/embeddings/reindex", null);

        // Whether this succeeds or reports "provider not available" depends on whether a model
        // is installed in the test environment. What must never happen again is the
        // unconditional "no store context", which was the response on every request.
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("No store context available",
            because: "the {store} route value is what resolves the store — finding H6");
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

        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
