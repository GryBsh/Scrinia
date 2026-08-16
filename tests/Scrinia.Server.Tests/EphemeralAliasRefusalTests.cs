using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// D-2: the ephemeral alias is a SURFACE form. The REST write routes sit below the surface, so
/// they refuse it rather than guessing between an ephemeral and the topic "temp". Before this,
/// POST /memories with an alias name wrote permanent bytes under memories/temp/, making it a
/// third, different memory from the two the MCP surface could produce.
/// </summary>
public class EphemeralAliasRefusalTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EphemeralAliasRefusalTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task PostMemories_RefusesTheAlias()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/temp/x", content = new[] { "hello" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical form is unchanged — only the alias is refused.</summary>
    [Fact]
    public async Task PostMemories_StillAcceptsTheCanonicalEphemeralForm()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "~x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode,
            $"expected success, got {(int)response.StatusCode} — a non-success here would make "
            + "the BadRequest assertion above vacuous");
    }

    /// <summary>A near-miss name must still be storable.</summary>
    [Fact]
    public async Task PostMemories_AcceptsANearMissName()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/tempest/x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// F1 (2026-08-16 final fix wave): the alias refusal only covered five of the seven write
    /// call sites — <c>CopyFromClient</c> was missing it entirely. Before this fix, a real Kestrel
    /// round trip against <c>POST .../memories/%2Ftemp%2Fx/copy-from-client</c> returned 200 and
    /// wrote <c>.scrinia/memories/temp/x.nmp2</c> — permanent bytes under the address the MCP
    /// facade and CLI treat as the in-memory ephemeral <c>~x</c>.
    ///
    /// <para><c>{name}</c> is the DESTINATION on this route and travels in the URL PATH, escaped —
    /// like <see cref="CopyFromClientEndpointTests"/>'s own escaped-slash tests, this needs a real
    /// Kestrel round trip: the shared class-fixture <c>TestServer</c> client does not reliably
    /// carry <c>%2F</c> through to the route value.</para>
    /// </summary>
    [Fact]
    public async Task CopyFromClient_RefusesTheAlias()
    {
        using var factory = new ScriniaServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = factory.CreateAuthenticatedClient();

        var entry = new CarriedEntry(
            UpdatedAt: null, Description: null, Attributes: null, Keywords: null,
            TermFrequencies: null, ChunkEntries: null, FileHashes: null, AgentKeywords: null);
        var req = new CopyFromClientRequest(["hello"], entry);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories/{Uri.EscapeDataString("/temp/x")}/copy-from-client",
            req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        // Nothing must have landed on disk — a 400 that still wrote the artifact would be lying
        // about the state, exactly the defect this fix removes.
        var store = (FileMemoryStore)factory.Services.GetRequiredService<StoreManager>().GetStore(factory.PrimaryStore);
        string storeDir = store.GetStoreDirForScope("local:temp");
        Assert.False(File.Exists(Path.Combine(storeDir, "x.nmp2")),
            "the refusal must fire before any artifact write, not merely report failure after one");
    }

    /// <summary>
    /// F1's other missing call site: <c>PatchEntry</c>. Before this fix, a real Kestrel round trip
    /// against <c>PATCH .../memories/%2Ftemp%2Fz</c> returned 200 and updated the entry stored
    /// under the persistent topic scope <c>local:temp</c> — the same address the MCP facade and
    /// CLI never resolve there.
    ///
    /// <para>Seeds the target directly through <see cref="IMemoryStore.StoreAsync"/> against the
    /// resolved <c>(subject, scope)</c> pair, bypassing the REST write surface entirely — seeding
    /// through <c>POST /memories</c> with the alias would hit the very refusal under test.</para>
    /// </summary>
    [Fact]
    public async Task PatchEntry_RefusesTheAlias()
    {
        using var factory = new ScriniaServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = factory.CreateAuthenticatedClient();

        var store = factory.Services.GetRequiredService<StoreManager>().GetStore(factory.PrimaryStore);
        var seededArtifact = await store.StoreAsync(new MemoryWrite("z", "local:temp", ["seed content"]));
        var seeded = MemoryEntry.From(store, seededArtifact, "local:temp");

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories/{Uri.EscapeDataString("/temp/z")}",
            seeded with { Description = "patched via the alias" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        // The seeded entry must be untouched — proof the refusal fired before PatchEntry ever
        // resolved the name and reached TryUpdateEntry.
        var reread = store.LoadIndex("local:temp").First(e => e.Name == "z");
        Assert.Equal(seededArtifact.ContentPreview, reread.ContentPreview);
        Assert.NotEqual("patched via the alias", reread.ContentPreview);
    }
}
