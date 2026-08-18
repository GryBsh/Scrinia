using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 9 fix round 1, F-4: converting <c>/memories</c>'s <c>scopes</c> query parameter from a
/// single comma-joined string to repeated params (matching <c>/search</c>, Task 9 D-2) means it
/// needs the same <c>ValidateStringArrayBounds</c> guard <c>/search</c> already has — a bound this
/// route had never had before, since the old comma-joined string had no size check of its own.
/// Mirrors <see cref="SearchScopeArrayBoundsTests"/>'s bound tests for <c>/search</c>.
/// </summary>
public sealed class MemoriesScopeArrayBoundsTests : IClassFixture<ScriniaServerFactory>
{
    private readonly HttpClient _client;
    private readonly string _base;

    public MemoriesScopeArrayBoundsTests(ScriniaServerFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    [Fact]
    public async Task Memories_Get_ScopesWith65Entries_ReturnsBadRequest()
    {
        string query = string.Join("&", Enumerable.Range(0, 65).Select(i => $"scopes=scope-{i}"));

        var resp = await _client.GetAsync($"{_base}/memories?{query}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "65 scope entries is a resource-exhaustion vector /memories must reject too, " +
                     "the same as /search already does");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("scopes");
    }

    [Fact]
    public async Task Memories_Get_ScopesWith64Entries_ReturnsOk()
    {
        string query = string.Join("&", Enumerable.Range(0, 64).Select(i => $"scopes=scope-{i}"));

        var resp = await _client.GetAsync($"{_base}/memories?{query}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "64 entries is comfortably inside the count bound; without this test a bound " +
                     "of zero would still pass the oversized-case test above");
    }

    [Fact]
    public async Task Memories_Get_AScopeEntryOver1024Characters_ReturnsBadRequest()
    {
        var resp = await _client.GetAsync($"{_base}/memories?scopes={new string('a', 1025)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "one 1025-character scope name is what a count-only bound would let through");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("scopes");
    }

    [Fact]
    public async Task Memories_Get_EmptyScopesParam_BehavesIdenticallyToOmittingIt()
    {
        var seed = await _client.PostAsJsonAsync(_base + "/memories",
            new StoreRequest(["omega content"], "omega-probe"));
        seed.EnsureSuccessStatusCode();

        var withEmptyParam = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories?scopes=");
        var withOmitted = await _client.GetFromJsonAsync<ListResponse>($"{_base}/memories");

        withEmptyParam!.Memories.Select(m => m.Name).Should().BeEquivalentTo(
            withOmitted!.Memories.Select(m => m.Name),
            because: "a literal `?scopes=` with no value must behave exactly like omitting scopes " +
                     "entirely — the same F-1 fix applies here, since ListMemory now calls " +
                     "ctx.Store!.ListScoped(scopes) directly, same as /search's RunSearch does");
    }
}
