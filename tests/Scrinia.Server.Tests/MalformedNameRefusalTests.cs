using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Every route that resolves a caller-supplied memory NAME must answer 400 when the store refuses
/// it, not 500.
///
/// <para><b>What this file exists to stop.</b> Task 13 routed
/// <c>ParseQualifiedName</c>'s multi-segment branch through <c>MemoryNaming.BuildScopedTopicScope</c>,
/// which throws <c>ArgumentException</c> for a topic segment substitution cannot repair. Only
/// <c>copy-from-client</c> had a guard for that throw, so every other route turned a caller's bad
/// name into an unhandled 500 — including for names this same server ACCEPTED and stored one commit
/// earlier (<see cref="GetEntry_WithATopicSegmentTheScopeRuleRefuses_Returns400"/> covers that
/// specific widening).</para>
///
/// <para><b>Why the colon name is the workhorse fixture.</b> <c>legacy:colon-name</c> reaches
/// <c>ParseQualifiedName</c>'s refusal on a SINGLE segment, so it needs no <c>%2F</c> in the route
/// value and works over the shared in-memory test server. The Task-13 refusal class needs two
/// segments and therefore a real Kestrel round trip — see the one test below that takes one, and
/// <c>CopyFromClientEndpointTests.CopyFromClient_WithRepairableDestinationName_SanitizesTheScopeAndStores</c>
/// for why the in-memory server cannot carry an escaped slash.</para>
///
/// <para>Each assertion checks the BODY as well as the status: a 400 produced by some unrelated
/// validator (a missing field, a length bound) would satisfy the status alone while the name refusal
/// stayed unguarded.</para>
/// </summary>
public sealed class MalformedNameRefusalTests : IClassFixture<ScriniaServerFactory>
{
    private readonly HttpClient _client;
    private readonly string _base;

    /// <summary>A name <c>ParseQualifiedName</c> refuses on a single segment — the removed
    /// "topic:subject" syntax.</summary>
    private const string BadName = "legacy:colon-name";

    public MalformedNameRefusalTests(ScriniaServerFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    private static async Task ShouldBeANameRefusal(HttpResponseMessage resp)
    {
        string raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"a name the store refuses is the caller's error, not the server's: {raw}");

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("path syntax",
            because: "the refusal must be ParseQualifiedName's own caller-facing message — a 400 "
                   + "from some other validator would satisfy the status while the name refusal "
                   + "stayed unguarded");
        body.Error.Should().NotContain("Parameter",
            because: "ArgumentException's auto-appended C# parameter name is not caller-facing");
    }

    [Fact]
    public async Task Remember_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.PostAsJsonAsync(
            $"{_base}/memories", new StoreRequest(["content"], BadName)));

    [Fact]
    public async Task Recall_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.GetAsync($"{_base}/memories/{BadName}"));

    [Fact]
    public async Task GetEntry_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.GetAsync($"{_base}/memories/{BadName}/entry"));

    [Fact]
    public async Task GetChunk_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.GetAsync($"{_base}/memories/{BadName}/chunks/1"));

    [Fact]
    public async Task Forget_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.DeleteAsync($"{_base}/memories/{BadName}"));

    [Fact]
    public async Task Append_WithMalformedName_Returns400() =>
        await ShouldBeANameRefusal(await _client.PostAsJsonAsync(
            $"{_base}/memories/{BadName}/append", new AppendRequest("appended content")));

    /// <summary>PATCH resolves the name AFTER parsing and bounds-checking the body, so the body has
    /// to be complete enough to reach the name at all — all three of <c>description</c>,
    /// <c>attributes</c> and <c>keywords</c> present, since the complete-entry wire shape refuses an
    /// omitted key. A body missing one of them earns a 400 from THAT validator instead, which is why
    /// this file asserts on the message and not the status alone.</summary>
    [Fact]
    public async Task Patch_WithMalformedName_Returns400()
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"{_base}/memories/{BadName}")
        {
            Content = JsonContent.Create(new
            {
                description = "d",
                attributes = new Dictionary<string, string[]>(),
                keywords = (string[]?)null,
            }),
        };
        await ShouldBeANameRefusal(await _client.SendAsync(req));
    }

    /// <summary><c>copy</c> resolves BOTH names inside <c>CopyMemory</c>, so both need the
    /// guard.</summary>
    [Fact]
    public async Task Copy_WithMalformedSourceName_Returns400() =>
        await ShouldBeANameRefusal(await _client.PostAsJsonAsync(
            $"{_base}/memories/{BadName}/copy", new CopyRequest("valid-destination")));

    /// <summary>The destination is the one an unguarded source check would miss: a VALID source
    /// resolves fine and the throw comes from the destination, deeper inside
    /// <c>CopyMemory</c>.</summary>
    [Fact]
    public async Task Copy_WithMalformedDestinationName_Returns400()
    {
        var seed = await _client.PostAsJsonAsync(
            $"{_base}/memories", new StoreRequest(["copy source content"], "copy-source"));
        seed.StatusCode.Should().Be(HttpStatusCode.Created);

        await ShouldBeANameRefusal(await _client.PostAsJsonAsync(
            $"{_base}/memories/copy-source/copy", new CopyRequest(BadName)));
    }

    /// <summary>
    /// <c>export</c> is a THIRD name-resolution surface, not the <c>{name}</c> route: it builds each
    /// topic's scope through the same one constructor, via
    /// <c>BundleFormatService.ExportTopicsToZip</c>. Task 13 made that throw too, and nothing caught
    /// it. The fixture is a trailing dot rather than a colon because a topic is a scope segment, so
    /// this is the Task-13 refusal class exactly — and it needs no escaped slash, since a topic
    /// travels in the request body.
    /// </summary>
    [Fact]
    public async Task Export_WithATopicTheScopeRuleRefuses_Returns400()
    {
        var resp = await _client.PostAsJsonAsync($"{_base}/export", new ExportRequest(["notes."]));

        string raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"an unusable topic is the caller's error: {raw}");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("notes.",
            because: "the refusal must name the topic that could not be resolved");
    }

    /// <summary>
    /// The Task-13 widening itself, on the route the reviewer named. <c>/notes./x</c> is a name this
    /// server RESOLVED before Task 13 — <c>ParseQualifiedName</c> interpolated the raw segments into
    /// <c>"local:notes."</c> — so this is not a new class of bad input being refused, it is
    /// previously-working input whose refusal has to arrive as a 400.
    ///
    /// <para>Needs a real Kestrel round trip: the refusal only exists on the multi-segment branch,
    /// which requires a <c>%2F</c> in the route value, and the shared in-memory test server does not
    /// carry one through reliably.</para>
    /// </summary>
    [Fact]
    public async Task GetEntry_WithATopicSegmentTheScopeRuleRefuses_Returns400()
    {
        using var factory = new ScriniaServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = factory.CreateAuthenticatedClient();
        string storeBase = $"/api/v1/stores/{factory.PrimaryStore}";

        var resp = await client.GetAsync(
            $"{storeBase}/memories/{Uri.EscapeDataString("/notes./x")}/entry");

        string raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"a scope segment the aliasing rule refuses is a caller error: {raw}");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("trims",
            because: "the refusal must be the scope constructor's own explanation — the filesystem "
                   + "trims a trailing dot, so 'notes.' would name the 'notes' directory");
    }
}
