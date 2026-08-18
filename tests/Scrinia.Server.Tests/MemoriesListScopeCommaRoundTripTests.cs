using System.Net.Http.Json;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 9 fix round 1, F-4 (CONTROLLER RULING): commit 1 of Task 9 made
/// <c>IMemoryStore.ListScoped</c> PROMISE list semantics. <see cref="Scrinia.Core.FileMemoryStore"/>
/// honours it, but <see cref="HttpMemoryStore"/> used to re-join the list on commas before sending
/// it to <c>/memories</c>, and the server split it back apart — so a scope named "odd,name" was
/// addressable through every LOCAL caller (Task 9's own <c>SearchScopeCommaRoundTripTests</c> proves
/// that for <c>/search</c>) and invisible to every FILTERED REMOTE read through <c>/memories</c>.
/// <c>MemoryTools.Core.cs</c>'s <c>list</c> and <c>MemoryTools.Facts.cs</c>'s <c>facts</c> both route
/// through <c>ListScoped</c>, so those two tools silently did not deliver the comma-named-scope
/// capability remotely, with no way for a caller to tell which store it was talking to.
///
/// <para>Mirrors <c>SearchScopeCommaRoundTripTests</c>'s structure and its reason for needing a real
/// Kestrel socket rather than <c>TestServer</c>'s in-memory handler: <c>HttpMemoryStore</c> sends
/// synchronously (<c>HttpClient.Send</c>), which <c>TestServer</c>'s handler rejects
/// unconditionally.</para>
/// </summary>
public sealed class MemoriesListScopeCommaRoundTripTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;
    private HttpMemoryStore _remote = null!;

    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        _remote = new HttpMemoryStore(_client, _factory.PrimaryStore);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _remote.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task SeedTwoMemoriesAsync()
    {
        // The target: a memory in a topic scope whose name contains a comma. Its internal scope
        // string is "local:odd,name" — MemoryNaming.BuildScopedTopicScope passes a comma through a
        // segment unchanged (Task 9's verified premise).
        var target = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["findable content"], "/odd,name/probe"));
        target.EnsureSuccessStatusCode();

        // A decoy in an unrelated scope. Under the old comma-joined encoding, a filter of
        // "odd,name" split into two scopes ["odd", "name"] that match NEITHER the target's real
        // scope NOR this decoy's — so the decoy guards against the opposite failure, an unfiltered
        // listing that returns everything regardless of the scopes argument.
        var decoy = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["findable content too"], "/decoy/other"));
        decoy.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ListScoped_ACommaNamedScope_ReturnsOnlyThatScope()
    {
        await SeedTwoMemoriesAsync();

        var results = _remote.ListScoped(["odd,name"]);

        results.Should().ContainSingle(
            "a scope filter of \"odd,name\" must select the one real scope of that name, " +
            "not split into two scopes that match nothing (the target) or everything (the decoy)");
        results[0].Entry.Name.Should().Be("probe");
        results[0].Scope.Should().Be("local:odd,name");
    }

    [Fact]
    public async Task LoadIndex_ACommaNamedScope_ReturnsTheEntry()
    {
        await SeedTwoMemoriesAsync();

        // LoadIndex takes the raw internal scope string, the same shape ResolveReadScopes yields.
        var entries = _remote.LoadIndex("local:odd,name");

        entries.Should().ContainSingle(
            "LoadIndex must be able to fetch a single comma-named scope's entries — under the old " +
            "comma-joined /memories encoding this scope's own name would be misread as a two-scope " +
            "filter server-side");
        entries[0].Name.Should().Be("probe");
    }

    /// <summary>The REST layer directly, independent of <see cref="HttpMemoryStore"/>'s request
    /// building — proves the SERVER side of F-4, not just the client side the two tests above
    /// exercise.</summary>
    [Fact]
    public async Task Memories_Get_RepeatedScopesParam_SelectsOnlyThatScope()
    {
        await SeedTwoMemoriesAsync();

        var resp = await _client.GetFromJsonAsync<ListResponse>(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories?scopes={Uri.EscapeDataString("odd,name")}");

        resp!.Memories.Should().ContainSingle(
            "/memories?scopes=... must bind as a repeated-param array (ASP.NET Core's native " +
            "string[]? binding), so a single \"odd,name\" value names ONE scope literally, not two");
        resp.Memories[0].Name.Should().Be("probe");
    }

    /// <summary>H-3 (fix round 2). The three tests above all pass a SINGLE-element scopes list, so
    /// a client-side re-join back to a comma string (reverting <c>FetchAllPages</c>'s per-element
    /// <c>&amp;scopes=</c> encoding to a single <c>&amp;scopes={Join(",", scopes)}</c> value)
    /// produces a byte-identical request for any of them — they pin the SERVER half of F-4 and none
    /// of the CLIENT half. A multi-element list is the only shape that tells the two encodings
    /// apart: re-joined, <c>["odd,name", "api"]</c> becomes the single query value
    /// "odd,name,api", which resolves to one scope named literally "odd,name,api" — matching
    /// neither of the two real scopes — while <c>["odd", "name"]</c> re-joins to "odd,name", which
    /// DOES match the real "odd,name" scope, fabricating a result from two tokens that separately
    /// name nothing.</summary>
    [Fact]
    public async Task ListScoped_MultipleScopesIncludingACommaNamedOne_SelectsExactlyThoseScopes()
    {
        await SeedTwoMemoriesAsync();
        var apiSeed = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest(["api content"], "/api/api-probe"));
        apiSeed.EnsureSuccessStatusCode();

        var both = _remote.ListScoped(["odd,name", "api"]);
        both.Select(r => r.Entry.Name).Should().BeEquivalentTo(["probe", "api-probe"],
            "a multi-element filter naming a comma-containing scope and a plain one must select " +
            "both scopes literally, not collapse into a single re-joined value that matches " +
            "neither real scope");

        var neither = _remote.ListScoped(["odd", "name"]);
        neither.Should().BeEmpty(
            "\"odd\" and \"name\" alone name no real scope — they exist only as a re-join " +
            "artifact of the single scope \"odd,name\"; a correct multi-element request must not " +
            "fabricate a match from them");
    }
}
