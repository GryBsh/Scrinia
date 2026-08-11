using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// <c>HttpMemoryStore.LoadIndex</c> used to return <c>[]</c> unconditionally, so
/// <c>IMemoryStore</c>'s default <c>TryUpdateEntry</c> always found nothing and reported
/// "not found" for a memory that genuinely exists — entity-typing promotion writes evaporated
/// remotely with no error. This pins the read-side fix: <c>LoadIndex</c> now proxies to the
/// listing endpoint instead of lying.
///
/// <para>It also pins why <c>HttpMemoryStore</c> now overrides <c>TryUpdateEntry</c> instead of
/// inheriting the default: <c>Upsert</c> is a deliberate no-op remotely (writes go through
/// <c>WriteArtifactAsync</c> instead), so once <c>LoadIndex</c> stops lying, the inherited
/// default would find the entry, apply the mutation, hand it to the no-op <c>Upsert</c>, and
/// still report <c>true</c> — a success report for a write that never reached the server. That
/// violates the contract <c>IMemoryStore.TryUpdateEntry</c>'s own doc states (the return value
/// means the mutation was persisted), regardless of who currently reads it: the one production
/// caller, <c>EntityTypingEventHandler.PromoteAsync</c>, calls this as a discarded statement and
/// does not branch on the result or retry either way, so this is a correctness fix for the
/// contract rather than a fix for an observed live bug. The override fails closed: it returns
/// <c>false</c> whenever it cannot actually persist, rather than trading "reports failure for a
/// memory that exists" for "reports success for a write that didn't happen".</para>
///
/// <para>Must be driven over a real Kestrel socket, not <c>TestServer</c>'s in-memory handler:
/// <c>HttpMemoryStore</c> sends synchronously (<c>HttpClient.Send</c>), and
/// <c>Microsoft.AspNetCore.TestHost.ClientHandler.Send</c> throws <c>NotSupportedException</c>
/// unconditionally. See <c>Conformance/StoreConformanceFixture.cs</c> for the same pattern.</para>
/// </summary>
public sealed class RemoteLoadIndexTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;
    private HttpMemoryStore _remote = null!;

    public Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do (mirrors StoreConformanceFixture).
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        _remote = new HttpMemoryStore(_client, _factory.PrimaryStore);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // HttpMemoryStore owns _client's disposal (its Dispose() disposes the HttpClient it was
        // constructed with), so no separate _client.Dispose() here.
        _remote.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task StoreAsync(string name, string body,
        Dictionary<string, string[]>? attributes = null, string[]? keywords = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, Attributes: attributes, Keywords: keywords));
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task LoadIndex_ReturnsTheStoredEntry()
    {
        await StoreAsync("loadindex-probe", "probe body for LoadIndex");

        var entries = _remote.LoadIndex("local");

        entries.Should().Contain(e => e.Name == "loadindex-probe",
            because: "the memory was just stored server-side and LoadIndex must proxy to the " +
                     "listing endpoint instead of returning an unconditional empty list");
    }

    /// <summary>
    /// The load-bearing assertion. A test that only checked LoadIndex was non-empty would pass
    /// against a proxy whose entries the update path still cannot use — measured directly: before
    /// the <c>TryUpdateEntry</c> override was added, this exact scenario returned <c>true</c>
    /// while <c>reloaded.Attributes</c> never gained the "person" key, i.e. it reported success
    /// for a write that silently discarded the mutation. The override closes that by refusing
    /// to report success it cannot back up: the entry is found (LoadIndex works), the mutation
    /// callback is invoked, and the result is still refused because there is no remote channel
    /// that can persist a metadata-only change — proven here by asserting the entry is
    /// byte-for-byte unchanged afterward, not merely that the call returned <c>false</c>.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_FailsClosed_RatherThanReportSuccessForADiscardedMutation()
    {
        await StoreAsync("tryupdate-probe", "probe body for TryUpdateEntry",
            attributes: new Dictionary<string, string[]> { ["mention"] = ["acme"] });

        // Captured so the assertions below can tell "TryUpdateEntry genuinely found the entry and
        // ran the mutation, then refused to persist it" apart from a stub that always returns
        // false without consulting LoadIndex at all — both would satisfy `updated.Should().BeFalse()`
        // alone.
        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("tryupdate-probe", "local", entry =>
        {
            mutateRan = true;
            return entry with
            {
                Attributes = new Dictionary<string, string[]>(entry.Attributes!) { ["person"] = ["acme"] },
            };
        });

        updated.Should().BeFalse(
            because: "HttpMemoryStore.Upsert cannot persist a metadata-only mutation remotely, " +
                     "so reporting true here would be a success report for a write that never " +
                     "happened — a worse defect than the \"not found\" this task fixed");
        mutateRan.Should().BeTrue(
            because: "the entry must actually have been found via LoadIndex and handed to the " +
                     "mutation callback — a constant `=> false` override would also satisfy the " +
                     "assertion above without exercising either");

        // A fresh, independent LoadIndex call — not the in-memory 'entry' local — so this can
        // only pass if the (refused) mutation genuinely never reached the server.
        var reloaded = _remote.LoadIndex("local").FirstOrDefault(e => e.Name == "tryupdate-probe");
        reloaded.Should().NotBeNull(because: "the entry must still exist — TryUpdateEntry must " +
                                              "not have deleted or corrupted it while refusing");
        reloaded!.Attributes.Should().NotContainKey("person",
            because: "a refused mutation must leave the remote entry exactly as it was, not " +
                     "partially applied");
    }

    /// <summary>
    /// The pre-existing "not found" case must still behave exactly as before: LoadIndex correctly
    /// reports no such entry, and TryUpdateEntry returns false for the ordinary reason (nothing to
    /// mutate), not because of the fail-closed override above.
    /// </summary>
    [Fact]
    public void TryUpdateEntry_ReturnsFalse_WhenTheEntryDoesNotExist()
    {
        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("does-not-exist", "local", entry =>
        {
            mutateRan = true;
            return entry;
        });

        updated.Should().BeFalse(because: "no such memory was ever stored");
        mutateRan.Should().BeFalse(
            because: "TryUpdateEntry must short-circuit on LoadIndex finding nothing, not invoke " +
                     "the mutation callback and then discard its result — that would make this " +
                     "case indistinguishable from the fail-closed one above for the wrong reason");
    }

    /// <summary>
    /// Reviewer-measured repro: seeding 205 entries over a Kestrel loopback and calling
    /// <c>LoadIndex("local")</c> returned 200, with "probe-0204" absent, because the proxy sent no
    /// <c>limit</c>/<c>offset</c> and the server's default list page size
    /// (<c>DefaultListLimit = 200</c>, <c>MemoryEndpoints.cs</c>) silently truncated it. That
    /// reproduces this task's own defect class above the pagination boundary: a scope this large
    /// would make <c>remember()</c>'s existing-entry lookup (<c>MemoryTools.Core.cs</c>) report
    /// "not found" for a memory that exists, skipping <c>ArchiveVersion</c> and resetting
    /// <c>CreatedAt</c> — silent data loss, just gated behind a size threshold instead of always.
    /// Seeded just over the boundary (205, not an arbitrarily larger number) to keep this fast.
    ///
    /// <para>Uses its own factory rather than the class's shared <see cref="_remote"/>: 205
    /// sequential writes plus the paged reads exceed the server's default rate limit (100
    /// requests/60s), which the fixture's other, much smaller tests don't need raised. Same
    /// override mechanism as <c>RateLimitTests.RateLimit_PartitionsByUser_NotSharedIp</c>.</para>
    /// </summary>
    [Fact]
    public async Task LoadIndex_ReturnsEveryEntry_AboveTheServersDefaultPageSize()
    {
        const int total = 205;

        // WithWebHostBuilder returns a WebApplicationFactory<Program> wrapper (a
        // DelegatedWebApplicationFactory), not a ScriniaServerFactory — so this cannot use
        // baseFactory's CreateAuthenticatedClient()/PrimaryStore-typed conveniences after the
        // clone. Build the client the same way RateLimitTests.RateLimit_PartitionsByUser_NotSharedIp
        // does: create a key directly against the clone's own ApiKeyStore.
        using var baseFactory = new ScriniaServerFactory();
        using var factory = baseFactory.WithWebHostBuilder(
            b => b.UseSetting("Scrinia:RateLimit:PermitLimit", (total * 2).ToString()));
        factory.UseKestrel(0);
        factory.StartServer();

        var keyStore = factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(
            "pagination-test-user", [baseFactory.PrimaryStore], ["search", "store"], "pagination-test");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);
        // HttpMemoryStore owns client's disposal (see DisposeAsync above for the same pattern).
        using var remote = new HttpMemoryStore(client, baseFactory.PrimaryStore);

        for (int i = 0; i < total; i++)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/v1/stores/{baseFactory.PrimaryStore}/memories",
                new StoreRequest([$"pagination probe body {i}"], $"probe-{i:D4}", $"pagination probe body {i}"));
            resp.EnsureSuccessStatusCode();
        }

        var entries = remote.LoadIndex("local");

        entries.Should().HaveCount(total,
            because: "a scope with more entries than the server's default list page size (200) " +
                     "must still come back whole, not silently truncated at the first page");
        entries.Should().Contain(e => e.Name == "probe-0204",
            because: "the last entry seeded — the one a first-page-only read would have dropped " +
                     "— must be present");
    }

    /// <summary>
    /// <c>HttpMemoryStore.TryUpdateEntry</c>'s own lookup used an ordinal
    /// <c>SanitizeName(e.Name) == SanitizeName(subject)</c>, so a case-mismatched subject fell out
    /// at the <c>current is null</c> guard and returned <c>false</c> WITHOUT invoking the mutation
    /// — collapsing "no such entry" back into "found it, cannot persist", the exact distinction
    /// this override exists to keep apart (the two sibling tests above pin each side of it). The
    /// return value is <c>false</c> in both cases by design, so <c>mutateRan</c> is the only thing
    /// that can tell them apart.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_FindsTheEntry_WhenTheSubjectCasingDiffers()
    {
        await StoreAsync("MixedCaseProbe", "probe body for casing");

        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("mixedcaseprobe", "local", entry =>
        {
            mutateRan = true;
            return entry;
        });

        mutateRan.Should().BeTrue(
            because: "the entry exists under a different casing and must be found — falling out at " +
                     "the null guard would report it absent, which is a different condition");
        updated.Should().BeFalse(
            because: "finding the entry changes nothing about this store's inability to persist a " +
                     "metadata-only mutation — the fail-closed contract is unchanged by this fix");
    }

    /// <summary>
    /// The listing wire carried no keywords at all — <c>MemoryListItem</c> had no such field, so
    /// every remote <c>ArtifactEntry</c> came back with <c>Keywords</c> null regardless of what was
    /// stored, while the server held them in hand. <c>MaintenanceEventSink</c> reads that field in
    /// three places; at <c>:102</c> an empty read makes the orphan branch write
    /// <c>Keywords = ["orphan"]</c>, clobbering every other keyword. That is inert only because
    /// <c>HttpMemoryStore.TryUpdateEntry</c> refuses every write today — the E4 PATCH endpoint
    /// would arm it.
    /// </summary>
    [Fact]
    public async Task LoadIndex_CarriesKeywords()
    {
        await StoreAsync("keyword-probe", "probe body for keywords", keywords: ["zebra"]);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "keyword-probe");

        entry.Keywords.Should().NotBeNull(
            because: "the server holds this entry's keywords and the listing endpoint must project " +
                     "them — a null here is the wire dropping data the server has in hand");
        // Contain, not equality: MemoryOrchestrator merges agent keywords with auto-extracted ones,
        // so the stored set is a superset (measured: [body, keywords, probe, zebra]).
        entry.Keywords!.Should().Contain("zebra");
    }
}
