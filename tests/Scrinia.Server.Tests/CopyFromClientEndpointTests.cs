using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

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

    /// <summary>An entry carrying nothing but the required-null shape, for rejection-path tests
    /// that don't care about the metadata itself.</summary>
    private static CarriedEntry MinimalEntry() => new(
        UpdatedAt: null, Description: null, Attributes: null, Keywords: null,
        TermFrequencies: null, ChunkEntries: null, FileHashes: null, AgentKeywords: null);

    /// <summary>
    /// The promotion carries a drift baseline naming a file that does NOT exist in the server's
    /// workspace. That is the assertion that matters: derivation hashes <c>file</c> values against
    /// the local filesystem, so a baseline surviving intact can only have been carried. A test that
    /// merely asserted the memory exists would pass while the server silently re-derived it to
    /// empty.
    ///
    /// <para>Round 2 review, Important 7: also covers <c>ChunkEntries</c>, <c>Description</c> and
    /// <c>Attributes</c> — deleting any one of those three from the carry mapping left the
    /// round-1 suite green.</para>
    /// </summary>
    [Fact]
    public async Task CopyFromClient_CarriesTheClientsDriftBaselineIntact()
    {
        const string content = "client-only content";
        var entry = new CarriedEntry(
            UpdatedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Description: "carried from the client",
            Attributes: new Dictionary<string, string[]> { ["file"] = ["src/OnlyOnTheClient.cs"] },
            Keywords: ["carried-keyword"],
            TermFrequencies: new Dictionary<string, int> { ["carried-keyword"] = 3 },
            ChunkEntries: [new ChunkEntryDto(1, "chunk preview", ["chunk-keyword"],
                new Dictionary<string, int> { ["chunk-keyword"] = 1 })],
            FileHashes: new Dictionary<string, string> { ["src/OnlyOnTheClient.cs"] = "deadbeefcafe" },
            AgentKeywords: ["carried-keyword"]);
        var req = new CopyFromClientRequest([content], entry);

        var resp = await _client.PostAsJsonAsync($"{_base}/memories/promoted/copy-from-client", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: await resp.Content.ReadAsStringAsync());

        var got = await _client.GetFromJsonAsync<MemoryEntry>($"{_base}/memories/promoted/entry");
        got.Should().NotBeNull();
        got!.Name.Should().Be("promoted");
        got.OriginalBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(content),
            because: "OriginalBytes is derived from Content now (Important 5), not carried — "
                   + "CarriedEntry no longer has the field at all");
        got.FileHashes.Should().BeEquivalentTo(entry.FileHashes,
            because: "the drift baseline names a file the server's own filesystem does not have — "
                   + "surviving intact proves it was carried, not silently re-derived to empty");
        got.TermFrequencies.Should().BeEquivalentTo(entry.TermFrequencies);
        got.AgentKeywords.Should().BeEquivalentTo(entry.AgentKeywords);
        got.Description.Should().Be(entry.Description,
            because: "Description carries the client's ContentPreview — deleting that mapping left "
                   + "the round-1 suite green");
        got.Attributes.Should().BeEquivalentTo(entry.Attributes,
            because: "Attributes carries through Normalize, which is a no-op on an already-lowercase, "
                   + "already-trimmed map like this one");
        got.ChunkEntries.Should().BeEquivalentTo(entry.ChunkEntries,
            options => options.Excluding(c => c.ChunkIndex).WithStrictOrdering(),
            because: "ChunkEntries carries the client's per-chunk keywords/preview/term "
                   + "frequencies — deleting that mapping left the round-1 suite green");
        got.ChunkEntries![0].ChunkIndex.Should().Be(1);
    }

    /// <summary>No name resolution happens against this store, so a same-named persistent memory is
    /// irrelevant rather than a collision to copy from. This is the wrong-memory defect, pinned.</summary>
    [Fact]
    public async Task CopyFromClient_IgnoresASameNamedMemoryInTheServersOwnStore()
    {
        var serverReq = new StoreRequest(["SERVER-SIDE marker content"], "collide", "server-side");
        var serverResp = await _client.PostAsJsonAsync($"{_base}/memories", serverReq);
        serverResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var copyReq = new CopyFromClientRequest(
            ["CLIENT-SIDE marker content"], MinimalEntry(), Overwrite: true);

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

        var req = new CopyFromClientRequest(["content"], MinimalEntry());
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

        var req = new CopyFromClientRequest(["content"], MinimalEntry());
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
        var req = new CopyFromClientRequest(["content"], MinimalEntry());
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
        var req = new CopyFromClientRequest(["content"], MinimalEntry());
        var resp = await _client.PostAsJsonAsync(
            $"{_base}/memories/legacy:colon-name/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("path syntax",
            because: "the refusal must be ParseQualifiedName's own caller-facing message, caught "
                   + "and turned into a 400 rather than surfacing as an unhandled 500");
        body.Error.Should().NotContain("Parameter",
            because: "the internal C# parameter name ArgumentException auto-appends is not a "
                   + "caller-facing detail (round 2 review, minor)");
    }

    /// <summary>
    /// Round 2 review, Critical 2. <c>ParseQualifiedName</c> sanitizes ONLY the leaf segment
    /// (<c>FileMemoryStore.cs:253</c>) — the scope built from every other segment
    /// (<c>FileMemoryStore.cs:251</c>) is raw, so <c>*</c> reaches <c>Directory.CreateDirectory</c>
    /// intact and throws <c>IOException</c> there, past <c>ParseQualifiedName</c>'s own throw.
    /// Needs a real Kestrel round trip (mirrors <c>CaseInsensitiveRestoreTests</c>): the shared
    /// in-memory <c>TestServer</c> the class fixture's <c>_client</c> uses does not reliably carry an
    /// escaped slash (<c>%2F</c>) through to the route value the way a real HTTP request does, and a
    /// two-segment v2 path is required to reach the scope-building code at all — a single-segment
    /// name goes through the leaf's own (correct) sanitization instead.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_WithFilesystemInvalidDestinationName_Returns400()
    {
        using var factory = new ScriniaServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = factory.CreateAuthenticatedClient();

        string encodedName = Uri.EscapeDataString("/a*b/x");
        var req = new CopyFromClientRequest(["content"], MinimalEntry());
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories/{encodedName}/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("filesystem",
            because: "the refusal must be the endpoint's IOException guard, not an unhandled 500 — "
                   + "and must not leak the server's absolute filesystem path");
    }

    [Fact]
    public async Task CopyFromClient_WithEmptyContent_Returns400()
    {
        var req = new CopyFromClientRequest([], MinimalEntry());
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/empty-content-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("content is required",
            because: "the message must name the missing field, not just report failure");
    }

    /// <summary>Round 2 review, Important 3: the size check tolerated a null element (it only
    /// filtered non-null ones), so a null content element sailed past every guard into
    /// <c>EncodeChunks</c>'s <c>UTF8.GetBytes(null)</c> — an unhandled 500.</summary>
    [Fact]
    public async Task CopyFromClient_WithNullContentElement_Returns400()
    {
        var req = new CopyFromClientRequest(["real chunk", null!], MinimalEntry());
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/null-content-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("null",
            because: "the message must name the actual problem, not just report failure");
    }

    [Fact]
    public async Task CopyFromClient_WithOverSizeContentElement_Returns400()
    {
        string huge = new string('x', 6 * 1024 * 1024); // over the 5 MB per-element cap
        var req = new CopyFromClientRequest([huge], MinimalEntry());
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

        var entry = MinimalEntry() with { Attributes = tooManyKeys };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain($"{KnownAttributes.MaxAttributes} keys",
            because: "the message must name the actual bound applied");
    }

    // ── Round 2 review, Critical 1: bounds on the carried metadata ──────────────────────────
    //
    // This route is the only wire entry point for these six fields (StoreRequest doesn't carry
    // them; PatchEntry refuses them). Round 2 re-review found three of the ROUND-1 numbers here
    // (Keywords/AgentKeywords at 25, Description at 500) were tighter than what a real local write
    // can produce — a store-mode divergence, the exact defect class this branch removes. See
    // CarriedEntryBoundsError's doc comment and each MaxCarried* constant's own doc comment in
    // MemoryEndpoints.cs for which derivation and which number now governs each field.
    // TextAnalysis.DefaultMaxMergedKeywords (30, public — promoted in round 2 specifically so this
    // number and the wire bound cannot drift apart again) is referenced directly; the two bounds
    // that are private to MemoryEndpoints (MaxCarriedDescriptionLength = 50,000,
    // MaxCarriedTermFrequencyTerms = 8,324) are literal here with the formula in each test's
    // comment.

    [Fact]
    public async Task CopyFromClient_WithOverLongDescription_Returns400()
    {
        // > 50,000-char bound (MaxCarriedDescriptionLength — a RESOURCE guard: no local ceiling
        // exists for a curated description, which MemoryDerivation.cs:152-154 stores verbatim).
        var entry = MinimalEntry() with { Description = new string('d', 50_001) };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/desc-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("50000",
            because: "the message must name the actual bound applied");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyKeywords_Returns400()
    {
        var entry = MinimalEntry() with
        {
            // > 30-entry bound (TextAnalysis.DefaultMaxMergedKeywords — MergeKeywordsWithSource's
            // real post-merge ceiling, not AnalyzeText's topN=25, which bounds only the auto half).
            Keywords = Enumerable.Range(0, TextAnalysis.DefaultMaxMergedKeywords + 1)
                .Select(i => $"kw{i}").ToArray(),
        };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/kw-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("keywords",
            because: "the message must name the field the bound applies to");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyAgentKeywords_Returns400()
    {
        var entry = MinimalEntry() with
        {
            AgentKeywords = Enumerable.Range(0, TextAnalysis.DefaultMaxMergedKeywords + 1)
                .Select(i => $"kw{i}").ToArray(),
        };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/agent-kw-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("agentKeywords",
            because: "the message must name the field the bound applies to");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyTermFrequencyTerms_Returns400()
    {
        // > 8,324-entry bound (MaxCarriedTermFrequencyTerms = DefaultMaxTfTerms(100) +
        // MaxAttributes(32) * (1 + MaxValuesPerKey(256)) — a RESOURCE guard: MemoryDerivation.ForStore
        // mutates the AnalyzeText-capped dictionary twice more, unconditionally, after the 100 cap).
        var tf = Enumerable.Range(0, 8_325).ToDictionary(i => $"term{i}", i => 1);
        var entry = MinimalEntry() with { TermFrequencies = tf };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/tf-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("8324",
            because: "the message must name the actual bound applied");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyFileHashes_Returns400()
    {
        var hashes = Enumerable.Range(0, KnownAttributes.MaxValuesPerKey + 1)
            .ToDictionary(i => $"src/File{i}.cs", i => $"hash{i}");
        var entry = MinimalEntry() with { FileHashes = hashes };
        var req = new CopyFromClientRequest(["content"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/filehash-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain($"{KnownAttributes.MaxValuesPerKey}",
            because: "the message must name the actual bound applied");
    }

    /// <summary>
    /// Round 2 review: the round-1 version of this test sent
    /// <c>[ChunkEntryDto(1,…), ChunkEntryDto(2,…)]</c> against one content chunk and asserted only
    /// <c>Contain("chunkEntries")</c> — a message from either the COUNT check or the per-element
    /// RANGE check (the second entry's index 2 is out of range too) satisfies that assertion, so
    /// deleting the count check specifically left this green. Both entries now carry the SAME valid
    /// index (1, which is in range for Content.Length == 1), so only the count check can fire —
    /// duplicate indices are deliberately allowed (see CarriedEntryBoundsError's doc comment) — and
    /// the assertion is narrowed to the count message's distinctive text.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_WithMoreChunkEntriesThanContentChunks_Returns400()
    {
        var entry = MinimalEntry() with
        {
            ChunkEntries = [new ChunkEntryDto(1, null, null, null), new ChunkEntryDto(1, null, null, null)],
        };
        var req = new CopyFromClientRequest(["only one chunk"], entry); // Content.Length == 1
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/chunk-count-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("may not carry more entries",
            because: "the message must be the COUNT check's, not a range check that happens to also "
                   + "mention \"chunkEntries\" — the fixture must fail for the count reason specifically");
    }

    /// <summary>
    /// Round 2 review, Important 4: <c>ChunkIndex</c> is 1-based and client-chosen.
    /// <c>WeightedAttributeScorer</c> emits search hits at whatever index a <c>ChunkEntry</c>
    /// carries, with <c>totalChunks</c> read from the entry's real <c>ChunkCount</c> — an
    /// out-of-range index is the reachable form of the header-mismatch threat Step 4a of the
    /// original task ruled unreachable at the artifact's own header (which this endpoint encodes
    /// itself and so cannot forge); <c>ChunkEntries</c> is caller-authored data this endpoint does
    /// NOT derive, so nothing stopped a fabricated index before this fix.
    /// </summary>
    [Fact]
    public async Task CopyFromClient_WithChunkEntryIndexOutOfRange_Returns400()
    {
        var entry = MinimalEntry() with
        {
            ChunkEntries = [new ChunkEntryDto(2, null, null, null)], // Content has 1 chunk: valid range is [1,1]
        };
        var req = new CopyFromClientRequest(["only one chunk"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/chunk-index-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("chunkIndex",
            because: "the message must name the field the bound applies to");
    }

    // ── Round 3 review: the PER-CHUNK-ENTRY bounds had no tests of their own ────────────────
    //
    // Before round 2 split MaxCarriedPreviewLength/MaxCarriedKeywords into separate top-level and
    // per-chunk-entry constants, the top-level bounds tests above exercised the (then-shared)
    // numbers for both. After the split, only the top-level half stayed covered — a later "these
    // look like duplicates, unify them" refactor would have been silently green. Each ChunkEntry
    // here carries a valid ChunkIndex (1, matching Content.Length == 1) so only the field-specific
    // check under test can fire, not the index-range check.

    [Fact]
    public async Task CopyFromClient_WithOverLongChunkContentPreview_Returns400()
    {
        var entry = MinimalEntry() with
        {
            // > 500-char bound (MaxCarriedChunkPreviewLength — a PARITY guard: ComputeChunkEntries
            // always runs ContentPreview.Generate's own default maxLength over a real chunk).
            ChunkEntries = [new ChunkEntryDto(1, new string('p', 501), null, null)],
        };
        var req = new CopyFromClientRequest(["only one chunk"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/chunk-preview-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("contentPreview",
            because: "the message must name the field the bound applies to");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyChunkKeywords_Returns400()
    {
        var entry = MinimalEntry() with
        {
            // > 25-entry bound (MaxCarriedChunkKeywords — AnalyzeText's own topN default; distinct
            // from the top-level 30, since ComputeChunkEntries never merges in agent keywords).
            ChunkEntries = [new ChunkEntryDto(
                1, null, Enumerable.Range(0, 26).Select(i => $"kw{i}").ToArray(), null)],
        };
        var req = new CopyFromClientRequest(["only one chunk"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/chunk-kw-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("chunkEntries",
            because: "the message must name the field the bound applies to");
        body.Error.Should().Contain("keywords",
            because: "the message must distinguish this from the chunkIndex/contentPreview checks");
    }

    [Fact]
    public async Task CopyFromClient_WithTooManyChunkTermFrequencyTerms_Returns400()
    {
        // > 100-entry bound (TextAnalysis.DefaultMaxTfTerms, reused directly for the per-chunk
        // case — unlike the top-level TermFrequencies bound, this one genuinely is what
        // AnalyzeText's own pruning produces for a single chunk, unmutated afterwards).
        var tf = Enumerable.Range(0, TextAnalysis.DefaultMaxTfTerms + 1).ToDictionary(i => $"term{i}", i => 1);
        var entry = MinimalEntry() with
        {
            ChunkEntries = [new ChunkEntryDto(1, null, null, tf)],
        };
        var req = new CopyFromClientRequest(["only one chunk"], entry);
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/chunk-tf-bounds-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("termFrequencies",
            because: "the message must name the field the bound applies to");
    }

    [Fact]
    public async Task CopyFromClient_ExistingDestinationWithoutOverwrite_Returns400()
    {
        var storeResp = await _client.PostAsJsonAsync($"{_base}/memories",
            new StoreRequest(["already here"], "existing-dest-probe"));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var req = new CopyFromClientRequest(["new content"], MinimalEntry()); // Overwrite defaults to false
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/existing-dest-probe/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("already exists",
            because: "the message must be WriteCarriedCopy's own overwrite refusal");
    }

    /// <summary>
    /// Round 3 review: the round-2 disclosure that a post-write I/O fault (a locked sidecar, a
    /// full disk) was untested — verified analytically, not with a red/green cycle — gets a real
    /// trigger here instead. <c>FileMemoryStore.WriteSidecarCore</c> writes
    /// <c>Path.Combine(storeDir, SanitizeNameCore(entry.Name) + ".meta.json")</c> via a
    /// temp-file-then-<c>File.Move</c>; pre-creating a DIRECTORY at exactly that path makes the
    /// move fail deterministically on both Windows and Linux (you cannot move-overwrite a file onto
    /// an existing directory) — and the failure happens inside <c>Upsert</c>, which
    /// <c>WriteCarriedCopy</c> only reaches AFTER <c>AtomicWriteAllText</c> has already written the
    /// real <c>.nmp2</c> artifact.
    ///
    /// <para>Measured (temporary diagnostic catch, reverted): this specific trigger throws
    /// <c>UnauthorizedAccessException</c>, not <c>IOException</c> — which is exactly why the
    /// round-3 fix removes the <c>WriteCarriedCopy</c> catch entirely rather than narrowing it to
    /// <c>catch (IOException)</c>; the latter would not have caught this fault either, and would
    /// have left this exact regression open under a different exception type. Confirmed by
    /// temporarily reintroducing a <c>catch (Exception)</c> around the call and observing this test
    /// fail (expected 500, got 400) — proof the test is sensitive to exactly the property under
    /// test (whether <c>WriteCarriedCopy</c>'s exceptions propagate), not to one exception type.</para>
    ///
    /// <para>The second assertion below is the one that matters: it pins that the artifact
    /// genuinely landed and the response was honest about that — the whole property removing the
    /// round-2 catch restored — not merely that some non-200 code came back.</para>
    /// </summary>
    [Fact]
    public async Task CopyFromClient_WhenTheSidecarWriteFailsAfterTheArtifactLands_Returns500AndKeepsTheArtifact()
    {
        const string destName = "post-write-fault-probe";
        var store = (FileMemoryStore)_factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        string storeDir = store.GetStoreDirForScope("local");
        Directory.CreateDirectory(storeDir);

        // A directory sitting at the sidecar's own path makes WriteSidecarCore's File.Move fail —
        // deterministically, and only after AtomicWriteAllText (the artifact write, below) has
        // already succeeded.
        Directory.CreateDirectory(Path.Combine(storeDir, destName + ".meta.json"));

        var req = new CopyFromClientRequest(["post-write fault content"], MinimalEntry());
        var resp = await _client.PostAsJsonAsync($"{_base}/memories/{destName}/copy-from-client", req);

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            because: "a fault past the artifact write is a server-side fault, not a caller-facing "
                   + "refusal — it must not be reported as a 400 blaming the destination name");

        File.Exists(Path.Combine(storeDir, destName + ".nmp2")).Should().BeTrue(
            because: "AtomicWriteAllText runs before Upsert's sidecar write, so the artifact "
                   + "genuinely landed on disk — the 500 above must be honest about that, not "
                   + "silently imply the write never happened");
    }
}

/// <summary>
/// Provisions one API key against one store, for
/// <see cref="CopyFromClientEndpointTests.CopyFromClient_DeniedWhenStoreIsReadOnlyForThisKey"/>:
/// a key under <see cref="ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId"/> — the same identity
/// <c>PatchEndpointTests</c>' <see cref="MarkTargetUserReadOnlyTransform"/> marks read-only — so
/// this route's <c>IsWritable</c> check gets the same genuine-claims-transform coverage PATCH's did
/// (M-1), without a second copy of the transform or the target-identity constant. Unlike
/// <see cref="ReadOnlyClaimsServerFactory"/>, this key holds 'copy' rather than 'store', since that
/// is the permission this route checks before <c>IsWritable</c> runs; no second (writable) key is
/// needed because the test never has to create anything through it first.
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
