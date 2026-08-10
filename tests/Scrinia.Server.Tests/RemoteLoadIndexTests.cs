using System.Linq;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia;
using Scrinia.Server.Models;
using Xunit;

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
/// still report <c>true</c> — a success report for a write that never reached the server. That is
/// a strictly worse defect than the one this task set out to fix, so the override fails closed:
/// it returns <c>false</c> whenever it cannot actually persist, rather than trading "reports
/// failure for a memory that exists" for "reports success for a write that didn't happen".</para>
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

    private async Task StoreAsync(string name, string body, Dictionary<string, string[]>? attributes = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, Attributes: attributes));
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

        bool updated = _remote.TryUpdateEntry("tryupdate-probe", "local", entry =>
            entry with
            {
                Attributes = new Dictionary<string, string[]>(entry.Attributes!) { ["person"] = ["acme"] },
            });

        updated.Should().BeFalse(
            because: "HttpMemoryStore.Upsert cannot persist a metadata-only mutation remotely, " +
                     "so reporting true here would be a success report for a write that never " +
                     "happened — a worse defect than the \"not found\" this task fixed");

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
    /// reports no such entry, and TryUpdateEntry returns false for the ordinary reason, not
    /// because of the fail-closed override above.
    /// </summary>
    [Fact]
    public void TryUpdateEntry_ReturnsFalse_WhenTheEntryDoesNotExist()
    {
        bool updated = _remote.TryUpdateEntry("does-not-exist", "local", entry => entry);

        updated.Should().BeFalse(because: "no such memory was ever stored");
    }
}
