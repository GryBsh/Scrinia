using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core.Search;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// Covers <c>POST /memories/{name}/copy-from-client</c> — the route that lets a client promote an
/// ephemeral the server has never seen, by carrying the artifact plus the metadata the client
/// already derived, rather than a bare name the server resolves against its own store. See
/// <c>MemoryEndpoints.CopyFromClient</c>'s doc comment for the <c>{name}</c>-is-destination
/// asymmetry this file's tests are built around.
/// </summary>
public sealed class CopyFromClientEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    private readonly HttpClient _client;
    private readonly string _base;

    public CopyFromClientEndpointTests(ScriniaServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }

    /// <summary>
    /// The promotion carries a drift baseline naming a file that does NOT exist in the server's
    /// workspace. That is the assertion that matters: derivation hashes <c>file</c> values against
    /// the local filesystem, so a baseline surviving intact can only have been carried. A test that
    /// merely asserted the memory exists would pass while the server silently re-derived it to
    /// empty.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_CarriesTheClientsDriftBaselineIntact()
    {
        var entry = new CarriedEntry(
            OriginalBytes: 42,
            UpdatedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Description: "carried from the client",
            Attributes: new Dictionary<string, string[]> { ["file"] = ["src/OnlyOnTheClient.cs"] },
            Keywords: ["carried-keyword"],
            TermFrequencies: new Dictionary<string, int> { ["carried-keyword"] = 3 },
            ChunkEntries: null,
            FileHashes: new Dictionary<string, string> { ["src/OnlyOnTheClient.cs"] = "deadbeefcafe" },
            AgentKeywords: ["carried-keyword"]);
        var req = new CopyFromClientRequest(["client-only content"], entry);

        var resp = await _client.PostAsJsonAsync($"{_base}/memories/promoted/copy-from-client", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: await resp.Content.ReadAsStringAsync());

        var got = await _client.GetFromJsonAsync<MemoryEntry>($"{_base}/memories/promoted/entry");
        got.Should().NotBeNull();
        got!.Name.Should().Be("promoted");
        got.OriginalBytes.Should().Be(42);
        got.FileHashes.Should().BeEquivalentTo(entry.FileHashes,
            because: "the drift baseline names a file the server's own filesystem does not have — "
                   + "surviving intact proves it was carried, not silently re-derived to empty");
        got.TermFrequencies.Should().BeEquivalentTo(entry.TermFrequencies);
        got.AgentKeywords.Should().BeEquivalentTo(entry.AgentKeywords);
    }

    /// <summary>No name resolution happens against this store, so a same-named persistent memory is
    /// irrelevant rather than a collision to copy from. This is the wrong-memory defect, pinned.</summary>
    [Fact]
    public async Task CopyFromClient_IgnoresASameNamedMemoryInTheServersOwnStore()
    {
        var serverReq = new StoreRequest(["SERVER-SIDE marker content"], "collide", "server-side");
        var serverResp = await _client.PostAsJsonAsync($"{_base}/memories", serverReq);
        serverResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var clientEntry = new CarriedEntry(
            OriginalBytes: 99, UpdatedAt: null, Description: "client-side",
            Attributes: null, Keywords: null, TermFrequencies: null, ChunkEntries: null,
            FileHashes: null, AgentKeywords: null);
        var copyReq = new CopyFromClientRequest(
            ["CLIENT-SIDE marker content"], clientEntry, Overwrite: true);

        var copyResp = await _client.PostAsJsonAsync($"{_base}/memories/collide/copy-from-client", copyReq);
        copyResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: await copyResp.Content.ReadAsStringAsync());

        var showResp = await _client.GetAsync($"{_base}/memories/collide");
        showResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var showBody = await showResp.Content.ReadFromJsonAsync<ShowResponse>();
        showBody!.Content.Should().Equal(["CLIENT-SIDE marker content"],
            because: "no name resolution ran against this store — the same-named persistent memory "
                   + "must be irrelevant, and the client's own content must be what landed");
    }

    [Fact]
    public async Task CopyFromClient_WithoutCopyPermission_Returns403()
    {
        var (rawKey, _) = _factory.CreateRestrictedStoreKey(_factory.PrimaryStore);
        using var limitedClient = _factory.CreateClient();
        limitedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

        var req = new CopyFromClientRequest(
            ["content"], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await limitedClient.PostAsJsonAsync($"{_base}/memories/no-perm-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("copy",
            because: "the error must name the missing permission, not just report failure");
    }

    /// <summary>
    /// <c>CreateRestrictedStoreKey</c> (used above) issues a key with NO permissions at all, scoped
    /// to one store — right for pinning the permission check, wrong for pinning
    /// <c>IsWritable</c>: hitting this route with it 403s on the permission check before
    /// <c>IsWritable</c> ever runs, so it cannot tell the two failure modes apart. A genuine
    /// read-only-store 403 needs a caller that DOES hold 'copy' but whose store access is marked
    /// read-only — the same gap <c>PatchEndpointTests.Patch_DeniedWhenStoreIsReadOnlyForThisKey</c>
    /// (M-1) found and fixed for PATCH, via a real <c>IClaimsTransformation</c> stand-in for the SSO
    /// claim <c>IsWritable</c> reads. This reuses that exact transform
    /// (<see cref="MarkTargetUserReadOnlyTransform"/>) and its target identity
    /// (<see cref="ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId"/>) rather than a second copy,
    /// through a factory that grants the target identity 'copy' instead of 'store'.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_DeniedWhenStoreIsReadOnlyForThisKey()
    {
        using var factory = new CopyFromClientReadOnlyServerFactory();
        using var readOnlyClient = factory.CreateReadOnlyTargetClient();

        var req = new CopyFromClientRequest(
            ["content"], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await readOnlyClient.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories/ro-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "IsWritable must deny copy-from-client against a store the caller's claims mark read-only");
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("read-only",
            because: "the 403 must genuinely be IsWritable's rejection, not the permission check's — "
                   + "the target identity DOES hold 'copy'");
    }

    [Fact]
    public async Task CopyFromClient_WithOverLongName_Returns400()
    {
        string longName = new string('a', 300);
        var req = new CopyFromClientRequest(
            ["content"], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/{longName}/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("256",
            because: "the message must name the actual bound, matching RememberMemory's");
    }

    /// <summary>
    /// Step 4b. <c>WriteCarriedCopy</c> resolves the destination via <c>ParseQualifiedName</c>,
    /// which throws <c>ArgumentException</c> — not a caller-facing refusal — for the removed
    /// "topic:subject" colon syntax. Reachable from an untrusted caller via this route, an unguarded
    /// throw would be an unhandled 500; this pins the 400 the endpoint's catch produces instead.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_WithMalformedDestinationName_Returns400()
    {
        var req = new CopyFromClientRequest(
            ["content"], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await _client.PostAsJsonAsync(
            $"{_base}/memories/legacy:colon-name/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("path syntax",
            because: "the refusal must be ParseQualifiedName's own caller-facing message, caught "
                   + "and turned into a 400 rather than surfacing as an unhandled 500");
    }

    [Fact]
    public async Task CopyFromClient_WithEmptyContent_Returns400()
    {
        var req = new CopyFromClientRequest(
            [], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/empty-content-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("content is required",
            because: "the message must name the missing field, not just report failure");
    }

    [Fact]
    public async Task CopyFromClient_WithOverSizeContentElement_Returns400()
    {
        string huge = new string('x', 6 * 1024 * 1024); // over the 5 MB per-element cap
        var req = new CopyFromClientRequest(
            [huge], new CarriedEntry(1, null, null, null, null, null, null, null));
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/oversize-content-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("MB",
            because: "the message must name the actual bound, matching RememberMemory's");
    }

    [Fact]
    public async Task CopyFromClient_WithOutOfBoundsAttributes_Returns400()
    {
        var tooManyKeys = new Dictionary<string, string[]>();
        for (int i = 0; i < KnownAttributes.MaxAttributes + 1; i++)
            tooManyKeys[$"key{i}"] = ["v"];

        var entry = new CarriedEntry(
            1, null, null, tooManyKeys, null, null, null, null);
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain($"{KnownAttributes.MaxAttributes} keys",
            because: "the message must name the actual bound applied");
    }

    [Fact]
    public async Task CopyFromClient_ExistingDestinationWithoutOverwrite_Returns400()
    {
        var storeResp = await _client.PostAsJsonAsync($"{_base}/memories",
            new StoreRequest(["already here"], "existing-dest-probe"));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var entry = new CarriedEntry(1, null, null, null, null, null, null, null);
        var req = new CopyFromClientRequest(["new content"], entry); // Overwrite defaults to false
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/existing-dest-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("already exists",
            because: "the message must be WriteCarriedCopy's own overwrite refusal");
    }
}

/// <summary>
/// Provisions two API keys against one store, for
/// <see cref="CopyFromClientEndpointTests.CopyFromClient_DeniedWhenStoreIsReadOnlyForThisKey"/>:
/// the factory's own (writable, 'copy'-holding) seed key, and a second key under
/// <see cref="ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId"/> — the same identity
/// <c>PatchEndpointTests</c>' <see cref="MarkTargetUserReadOnlyTransform"/> marks read-only — so
/// this route's <c>IsWritable</c> check gets the same genuine-claims-transform coverage PATCH's did
/// (M-1), without a second copy of the transform or the target-identity constant. Unlike
/// <see cref="ReadOnlyClaimsServerFactory"/>, both keys here hold 'copy' rather than 'store', since
/// that is the permission this route checks before <c>IsWritable</c> runs.
/// </summary>
public sealed class CopyFromClientReadOnlyServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    public string PrimaryStore { get; } = "test-store";
    private string ReadOnlyTargetApiKey { get; set; } = "";

    public CopyFromClientReadOnlyServerFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinium-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Scrinia:DataDir", _tempDir);
        builder.UseSetting($"Scrinia:Stores:{PrimaryStore}", Path.Combine(_tempDir, "stores", PrimaryStore));

        builder.ConfigureServices(services =>
        {
            services.AddScoped<IClaimsTransformation, MarkTargetUserReadOnlyTransform>();

            var sp = services.BuildServiceProvider();
            var keyStore = sp.GetRequiredService<ApiKeyStore>();
            var (readOnlyRawKey, _, _) = keyStore.CreateKey(
                ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId, [PrimaryStore], ["copy"], "readonly-probe-key");
            ReadOnlyTargetApiKey = readOnlyRawKey;
        });
    }

    public HttpClient CreateReadOnlyTargetClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadOnlyTargetApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
