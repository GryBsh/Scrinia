using System.Net.Http.Json;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Search;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 9, D-2: the layer where the old comma-joined <c>scopes</c>/<c>excludeTopics</c> encoding
/// actually lost information. A comma is legal in a scope name (<c>MemoryNaming.TryBuildScopedTopicScope</c>
/// passes it through unchanged — see the task brief's verified premise), so a scope filter that joins
/// its entries with commas cannot distinguish one scope named "a,b" from two scopes "a" and "b".
/// This drives a real round trip — <see cref="HttpMemoryStore"/> over a real Kestrel socket, through
/// the REST <c>/search</c> endpoint, into the server's <c>FileMemoryStore</c> — on both the GET route
/// (repeated query params) and the POST route (a JSON array body), since the two transports carry
/// the array independently and either could regress without the other noticing.
///
/// <para>Must be driven over a real Kestrel socket, not <c>TestServer</c>'s in-memory handler:
/// <c>HttpMemoryStore</c> sends synchronously (<c>HttpClient.Send</c>), and
/// <c>Microsoft.AspNetCore.TestHost.ClientHandler.Send</c> throws <c>NotSupportedException</c>
/// unconditionally. Same pattern as <c>RemoteLoadIndexTests</c> and
/// <c>Conformance/StoreConformanceFixture</c>.</para>
/// </summary>
public sealed class SearchScopeCommaRoundTripTests : IAsyncLifetime
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
        // HttpMemoryStore owns _client's disposal, so no separate _client.Dispose() here.
        _remote.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task SeedTwoMemoriesAsync(bool sharedAttribute = false)
    {
        var attributes = sharedAttribute
            ? new Dictionary<string, string[]> { ["topic"] = ["widget"] }
            : null;

        // The target: a memory in a topic scope whose name contains a comma.
        var target = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["findable content about widgets"], "/odd,name/probe", "probe", Attributes: attributes));
        target.EnsureSuccessStatusCode();

        // A decoy in an unrelated scope. Under the old comma-joined encoding, a filter of
        // "odd,name" split into two scopes ["odd", "name"] that match NEITHER the target's real
        // scope NOR this decoy's — so the decoy is not what proves the fix; the target's presence
        // (or absence) is. The decoy instead guards against the opposite failure, an unfiltered
        // search that returns everything regardless of the scopes argument. When sharedAttribute
        // is set, the decoy carries the SAME attribute as the target, so an attributeFilter alone
        // cannot distinguish them — only correct scope selection can.
        var decoy = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["findable content about widgets too"], "/decoy/other", "decoy", Attributes: attributes));
        decoy.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Search_Get_AScopeNameContainingAComma_SelectsOnlyThatScope()
    {
        await SeedTwoMemoriesAsync();

        // No attributeFilter -> HttpMemoryStore.SearchRemote takes the GET path (BuildSearchGet),
        // which Task 9 (D-2) changed to repeated query params (?scopes=a&scopes=b).
        var results = _remote.SearchAll(new SearchQuery("findable") { Scopes = ["odd,name"], Limit = 20 });

        results.Should().ContainSingle(
            "a scope filter of \"odd,name\" must select the one real scope of that name, " +
            "not split into two scopes that match nothing (the target) or everything (the decoy)");
        results[0].Should().BeOfType<EntryResult>()
            .Which.Item.Entry.Name.Should().Be("probe");
    }

    [Fact]
    public async Task Search_Post_AScopeNameContainingAComma_SelectsOnlyThatScope()
    {
        // Both memories carry the same "topic: widget" attribute, so attributeFilter alone cannot
        // tell them apart — only correctly selecting the "odd,name" scope can.
        await SeedTwoMemoriesAsync(sharedAttribute: true);

        // A non-empty attributeFilter forces HttpMemoryStore.SearchRemote onto the POST path
        // (BuildSearchPost), which Task 9 (D-2) changed to send scopes/excludeTopics as a JSON
        // array in the request body rather than a comma-joined string field.
        var results = _remote.SearchAll(new SearchQuery("findable")
        {
            Scopes = ["odd,name"],
            Limit = 20,
            AttributeFilter = new Dictionary<string, string[]> { ["topic"] = ["widget"] },
        });

        // Under the old comma-joined encoding, "odd,name" split into scopes ["odd", "name"],
        // matching neither memory's real scope — this would return empty. Under the fix it must
        // find exactly the target, proving the POST body's array reached the server and selected
        // the right scope.
        results.Should().ContainSingle(
            "the POST body's scopes array must select the one real \"odd,name\" scope, " +
            "not split into two scopes that match nothing");
        results[0].Should().BeOfType<EntryResult>()
            .Which.Item.Entry.Name.Should().Be("probe");
    }
}
