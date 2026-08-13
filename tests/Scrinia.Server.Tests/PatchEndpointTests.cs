using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// <c>GET /memories/{name}/entry</c> and <c>PATCH /memories/{name}</c> — E4 of the
/// store-owned-derivation design
/// (<c>docs/superpowers/specs/2026-08-11-store-owned-derivation-design.md</c>). Driven over a real
/// Kestrel socket, mirroring <c>RemoteLoadIndexTests</c>, for consistency with this branch's other
/// server-endpoint suites — even though these tests use plain async <c>HttpClient</c> calls rather
/// than <c>HttpMemoryStore.Send</c>, which is the specific reason that suite needs Kestrel.
///
/// <para>Findings I-1 through I-5 and M-1 through M-3 below come from the controller's review of
/// the first version of this file and <c>MemoryEndpoints.PatchEntry</c>: the original tests proved
/// PATCH could not DESTROY derived data but never proved it could not be TRICKED into accepting
/// caller-forged data, and the endpoint's bounds/normalization/completeness rules were either wrong
/// (I-1), missing (I-2, I-4) or unspecified (I-3, M-3). Each finding has a dedicated test below,
/// named after it.</para>
/// </summary>
public sealed class PatchEndpointTests : IAsyncLifetime
{
    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;
    private string _base = "";

    public Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do (mirrors RemoteLoadIndexTests).
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{_factory.PrimaryStore}";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Stores a memory and reads back its complete entry via the endpoint under test's
    /// GET sibling, so every test PATCHes a real, server-derived <see cref="MemoryEntry"/> rather
    /// than a hand-built one.</summary>
    private async Task<MemoryEntry> StoreAndGetEntryAsync(
        string name, string[] content, Dictionary<string, string[]>? attributes = null)
    {
        var storeResp = await _client.PostAsJsonAsync($"{_base}/memories",
            new StoreRequest(content, name, string.Concat(content), Attributes: attributes));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "the memory must be seeded successfully for the test to be valid");

        var entryResp = await _client.GetAsync($"{_base}/memories/{name}/entry");
        entryResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "GET /entry is what the test seeds through — its own correctness is exercised " +
                     "incidentally by every test in this file, not separately");
        var entry = await entryResp.Content.ReadFromJsonAsync<MemoryEntry>();
        entry.Should().NotBeNull();
        return entry!;
    }

    [Fact]
    public async Task Patch_PersistsAnAttributePromotion()
    {
        var entry = await StoreAndGetEntryAsync("patch-promote-probe", ["promotion probe body"]);

        var patched = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["person"] = ["acme"],
            },
        };

        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-promote-probe", patched);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an in-bounds metadata patch against the current UpdatedAt must be applied");

        var reGet = await _client.GetAsync($"{_base}/memories/patch-promote-probe/entry");
        reGet.StatusCode.Should().Be(HttpStatusCode.OK);
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();
        reread!.Attributes.Should().NotBeNull();
        reread.Attributes!.Should().ContainKey("person",
            because: "the promoted attribute must be visible on a fresh read, not just in the PATCH response");
        reread.Attributes!["person"].Should().Contain("acme");
    }

    /// <summary>
    /// The 200 body must describe the entry as STORED, derived fields included. It did not: the
    /// handler captured the entry inside the mutate callback, one step before
    /// <c>TryUpdateEntry</c> re-derives <c>TermFrequencies</c> from the attributes it just
    /// accepted, so the response reported the new attribute's term as absent while an immediate
    /// re-GET reported it present. <c>HttpMemoryStore</c> ignores this body, so nothing was
    /// mis-persisted — but <c>web/</c> and any other REST client reads it, and a derived field that
    /// can disagree with its payload is the exact defect the complete-entry wire shape exists to
    /// remove.
    /// <para>Asserted as equality against the re-GET rather than as "contains the term": the point
    /// is that the two agree, not that either one is individually right, and a response rebuilt
    /// from anything other than what was stored fails on some field or other.</para>
    /// </summary>
    [Fact]
    public async Task Patch_ResponseBody_MatchesWhatTheStoreActuallyPersisted()
    {
        var entry = await StoreAndGetEntryAsync("patch-response-freshness-probe", ["response freshness body"]);

        var patched = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["person"] = ["zanzibar"],
            },
        };

        var patchResp = await _client.PatchAsJsonAsync(
            $"{_base}/memories/patch-response-freshness-probe", patched);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<MemoryEntry>();

        var reGet = await _client.GetAsync($"{_base}/memories/patch-response-freshness-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();

        body!.TermFrequencies.Should().ContainKey("zanzibar",
            because: "the store re-derived the new attribute's term as part of the very write this " +
                     "response is reporting — omitting it makes the body describe an entry that " +
                     "never existed");

        // The WHOLE entry, not a chosen subset. The complete-entry wire shape's claim is that one
        // of these is the entry, so an assertion that named only the fields the current defect
        // touched would have to be widened by hand every time the shape grows a field — and the
        // next stale one would sit in whatever was left out.
        body.Should().BeEquivalentTo(reread,
            because: "the write response and an immediate read of the same entry must not disagree " +
                     "about ANY field, derived or otherwise");
    }

    /// <summary>
    /// THE load-bearing guard, rewritten per I-5. The original version of this test round-tripped
    /// the GET body unedited for the derived fields, so it could only catch the server NULLING them
    /// — mutation-proven blind to the server HONOURING caller-forged values (adding
    /// <c>TermFrequencies = req.TermFrequencies, FileHashes = req.FileHashes</c> to the handler's
    /// <c>with</c> expression compiled clean and left all four tests green). This version PATCHes a
    /// body that forges <c>TermFrequencies</c>, <c>FileHashes</c>, <c>ChunkEntries</c>,
    /// <c>ChunkCount</c>, <c>OriginalBytes</c> and <c>CreatedAt</c> to obviously-wrong values
    /// alongside a legitimate attribute change, and asserts the store's real, pre-patch values win
    /// for all six — the only version of this guard that catches both directions for every field
    /// the handler's doc comment claims the body can never move. <c>ChunkCount</c>/<c>OriginalBytes</c>/
    /// <c>CreatedAt</c> were added after the doc comment turned out to name them but the test never
    /// forged them — a caller-supplied <c>createdAt</c> reaching storage is an integrity concern on a
    /// public REST surface, not just an internal accounting detail.
    /// </summary>
    [Fact]
    public async Task Patch_LeavesDerivedFieldsIntact()
    {
        const string chunk1 = "Apricot orchard maintenance notes. Apricot trees need pruning every spring.";
        const string chunk2 = "Banana plantation records. Banana trees thrive near equatorial banana farms.";
        const string fileRelativePath = "patch-drift-probe.txt";
        const string fileContents = "drift baseline contents for the patch-survival probe";

        // Plant the drift-baseline file inside the store's own workspace root — FileMemoryStore's
        // hasher resolves `file` attribute values against exactly this path (mirrors
        // ListResponseCompleteEntryTests), so FileHashes genuinely populates rather than proving
        // nothing with an unresolvable path.
        var store = (FileMemoryStore)_factory.Services.GetRequiredService<StoreManager>()
            .GetStore(_factory.PrimaryStore);
        string fullPath = Path.Combine(store.WorkspaceRoot, fileRelativePath);
        await File.WriteAllTextAsync(fullPath, fileContents);

        var entry = await StoreAndGetEntryAsync(
            "patch-derived-survival-probe", [chunk1, chunk2],
            attributes: new Dictionary<string, string[]> { ["file"] = [fileRelativePath] });

        // Sanity: the setup genuinely produced all three derived field groups. A test that skipped
        // this would pass just as well against a store that always returns them null.
        entry.TermFrequencies.Should().NotBeNull(
            because: "the seeded content must have produced BM25 term frequencies");
        entry.ChunkEntries.Should().NotBeNull().And.HaveCount(2,
            because: "the memory was stored with two chunks");
        entry.FileHashes.Should().NotBeNull().And.ContainKey(fileRelativePath,
            because: "the `file` attribute must have resolved against the planted file");

        // Act: PATCH a body carrying a legitimate attribute change ALONGSIDE forged derived values.
        var forged = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["mention"] = ["survives-the-patch"],
            },
            TermFrequencies = new Dictionary<string, int> { ["poison"] = 9999 },
            FileHashes = new Dictionary<string, string> { ["x"] = "deadbeef" },
            ChunkEntries =
            [
                new ChunkEntryDto(1, "forged chunk", ["forged"], new Dictionary<string, int> { ["forged"] = 1 }),
            ],
            ChunkCount = 9999,
            OriginalBytes = 123456789,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var patchResp = await _client.PatchAsJsonAsync(
            $"{_base}/memories/patch-derived-survival-probe", forged);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var reGet = await _client.GetAsync($"{_base}/memories/patch-derived-survival-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();

        reread!.TermFrequencies.Should().NotContainKey("poison",
            because: "the forged key must never reach storage — this is what a round-trip-only test cannot catch");

        // Every term the store had derived for itself survives, and never loses weight. This used
        // to be a whole-map equality assertion, which can no longer hold: a legitimate attribute
        // change now legitimately re-seeds the affected key and its new values
        // (MemoryDerivation.ForMetadataUpdate), so "mention" gains +3. Survival-with-no-loss is the
        // form that still fails if the handler adopts the body's map, because the forged map
        // contains none of these terms at all.
        foreach (var (term, count) in entry.TermFrequencies!)
        {
            reread.TermFrequencies.Should().ContainKey(term,
                because: "the server's own derived term frequencies must survive a metadata patch " +
                         "— the caller's map must not replace them");
            reread.TermFrequencies![term].Should().BeGreaterOrEqualTo(count,
                because: "a metadata update only ever ADDS weight for what it changed; nothing it " +
                         "does may take weight away from a term derived from the content");
        }

        // A content term untouched by the patch keeps its EXACT count. Without this the assertion
        // above would tolerate a handler that rebuilt the whole map from something else and merely
        // happened to score higher everywhere.
        reread.TermFrequencies!["apricot"].Should().Be(entry.TermFrequencies["apricot"],
            because: "the patch changed an attribute, not the content — a term the change does not " +
                     "touch must come back bit-for-bit");

        // And the legitimate attribute change DID re-seed, server-side. Without this the test
        // passes just as well against a server that ignores attributes for indexing entirely,
        // which is the defect that made a remote update() silently unfindable by its own new
        // attribute while reporting success.
        reread.TermFrequencies.Should().ContainKey("survives",
            because: "the promoted attribute's value tokens must reach the BM25 index, and only " +
                     "the store can put them there — the wire refuses TermFrequencies from a body");
        reread.ChunkEntries.Should().BeEquivalentTo(entry.ChunkEntries,
            because: "the server must keep its own derived chunk entries, not the caller-forged ones");
        reread.FileHashes.Should().BeEquivalentTo(entry.FileHashes,
            because: "the server must keep its own derived drift baseline, not the caller-forged one");
        reread.FileHashes.Should().NotContainKey("x",
            because: "the forged FileHashes key must never reach storage");

        // The handler's own doc comment names ChunkCount/OriginalBytes/CreatedAt alongside
        // TermFrequencies/ChunkEntries/FileHashes as fields the body must never move — but until now
        // nothing forged them, so a future edit wiring any of these three into the handler's `with`
        // expression would have left this whole file green. CreatedAt in particular is an integrity
        // concern: PATCH is a public REST surface, and a caller-forged CreatedAt reaching storage
        // would let anyone rewrite a memory's history.
        reread.ChunkCount.Should().Be(entry.ChunkCount,
            because: "the forged ChunkCount must never reach storage");
        reread.OriginalBytes.Should().Be(entry.OriginalBytes,
            because: "the forged OriginalBytes must never reach storage");
        reread.CreatedAt.Should().Be(entry.CreatedAt,
            because: "the forged CreatedAt must never reach storage — PATCH must not let a caller " +
                     "rewrite a memory's history");

        // The legitimate attribute promotion this test carries alongside the guard did apply.
        reread.Attributes!.Should().ContainKey("mention");
    }

    [Fact]
    public async Task Patch_RejectsAStaleUpdatedAtWith409()
    {
        var entry = await StoreAndGetEntryAsync("patch-stale-probe", ["stale probe body"]);

        // Mutate the server's copy underneath the stale read — directly through the store, the
        // same seam TryUpdateEntry itself uses, so this genuinely represents a concurrent writer
        // rather than a second call through the endpoint under test.
        var store = _factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        bool mutated = store.TryUpdateEntry("patch-stale-probe", "local", current => current with
        {
            Attributes = new Dictionary<string, string[]>(current.Attributes ?? [])
            {
                ["mention"] = ["concurrent-writer"],
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        mutated.Should().BeTrue(because: "the entry must genuinely exist for this setup to be valid");

        // Act: PATCH using the UpdatedAt captured before the concurrent mutation above — now stale.
        var stalePatch = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["mention"] = ["stale-writer"],
            },
        };
        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-stale-probe", stalePatch);

        patchResp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "the body's UpdatedAt no longer matches the stored value");

        var reGet = await _client.GetAsync($"{_base}/memories/patch-stale-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();
        reread!.Attributes!["mention"].Should().Contain("concurrent-writer",
            because: "the concurrent writer's value must survive the rejected stale PATCH");
        reread.Attributes!["mention"].Should().NotContain("stale-writer",
            because: "the rejected stale PATCH must not have been applied, even partially — the " +
                     "server's value must be left exactly as the concurrent writer left it");
    }

    [Fact]
    public async Task Patch_RequiresTheStorePermission()
    {
        var entry = await StoreAndGetEntryAsync("patch-permission-probe", ["permission probe body"]);

        // A key with "read" but not "store" — proves the 403 below is genuinely about the missing
        // permission PatchEntry requires, not a broader lockout from a key with no permissions at all.
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(
            "read-only-user", [_factory.PrimaryStore], ["read"], "read-only-for-patch-test");
        using var limitedClient = _factory.CreateClient();
        limitedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

        var patchResp = await limitedClient.PatchAsJsonAsync(
            $"{_base}/memories/patch-permission-probe", entry);

        patchResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the key has 'read' but not 'store', and PatchEntry requires 'store'");

        // M-2: match the house pattern (SearchAttributeFilterBoundsTests) of asserting the message
        // too — a bare 403 would also pass for an unrelated lockout.
        var body = await patchResp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("store",
            because: "the error must name the missing permission, not just report failure");
    }

    /// <summary>
    /// I-1. 70 <c>file</c> values is within <see cref="KnownAttributes.MaxValuesPerKey"/> (256 —
    /// the write-path bound <c>RememberMemory</c> enforces, and <c>KnownAttributes.cs</c>'s own doc
    /// comment names "a memory citing 70 distinct paths" as the exact case that bound exists for)
    /// but outside the search filter's 64. Before the fix, a byte-identical PATCH round-trip of
    /// such an entry 400'd — proving the validator was reusing the wrong bound. This pins that the
    /// write-path bound is what PATCH actually applies.
    /// </summary>
    [Fact]
    public async Task Patch_AcceptsTheWritePathAttributeBound_NotTheTighterSearchFilterBound()
    {
        var fileValues = Enumerable.Range(0, 70).Select(i => $"src/File{i:D3}.cs").ToArray();
        var entry = await StoreAndGetEntryAsync("patch-bounds-probe", ["bounds probe body"],
            attributes: new Dictionary<string, string[]> { ["file"] = fileValues });
        entry.Attributes!["file"].Should().HaveCount(70,
            because: "the store itself must have accepted 70 file values for the PATCH round-trip " +
                     "below to be a meaningful test of PATCH's own bound, not RememberMemory's");

        // Act: PATCH the entry back completely unchanged.
        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-bounds-probe", entry);

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "PatchEntry must apply KnownAttributes.MaxValuesPerKey (256), not the search " +
                     "filter's 64 — a validator using 64 here rejects entries the server's own " +
                     "write path produces, breaking the round-trip this wire shape exists for");
    }

    /// <summary>
    /// R-6. I-1's lesson in miniature: bounds were checked against the RAW body before
    /// <see cref="KnownAttributes.Normalize"/> ran, so a 33-key body that collapses to 32 distinct
    /// keys under Normalize's case-insensitive dedupe (two keys differing only by case) was rejected
    /// — even though the server's own write path (<c>RememberMemory</c> → <c>MemoryDerivation</c>)
    /// would happily fold the same 33 raw keys down to 32 and accept them. Bounds must be checked
    /// against what actually gets persisted.
    /// </summary>
    [Fact]
    public async Task Patch_ValidatesAttributeBounds_AfterNormalization_NotBefore()
    {
        var entry = await StoreAndGetEntryAsync("patch-bounds-order-probe", ["bounds order probe body"]);

        // 33 raw dictionary keys, but "Dup0"/"dup0" differ only by case — Normalize's
        // case-insensitive dedupe collapses them into one, leaving exactly 32 distinct keys
        // post-normalization (KnownAttributes.MaxAttributes), not 33.
        var rawAttributes = new Dictionary<string, string[]>();
        for (int i = 1; i < 32; i++)
            rawAttributes[$"key{i}"] = ["v"];
        rawAttributes["Dup0"] = ["v"];
        rawAttributes["dup0"] = ["v"];
        rawAttributes.Should().HaveCount(33,
            because: "the raw body must genuinely carry 33 distinct dictionary keys for this test " +
                     "to exercise the pre- vs. post-normalization distinction");

        var patched = entry with { Attributes = rawAttributes };
        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-bounds-order-probe", patched);

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the body collapses to 32 distinct keys after KnownAttributes.Normalize — " +
                     "bounds must be checked against that, not the raw 33, or the server would " +
                     "reject an attribute map it could legitimately produce itself");
    }

    /// <summary>
    /// I-2. <see cref="KnownAttributes.Normalize"/> is the canonicalizer every other write path
    /// funnels through (lowercase+trim keys, trim values, drop blanks); before the fix, PATCH
    /// assigned the body's attributes verbatim, so a PATCH-written "Person"/"  spaced  "/blank
    /// key persisted un-canonicalized — invisible to ordinal readers like
    /// <c>AttributeReaders.DisplayTags</c> that match against <c>KnownAttributes.TagKey</c>.
    /// </summary>
    [Fact]
    public async Task Patch_NormalizesAttributeCasingWhitespaceAndBlanks()
    {
        var entry = await StoreAndGetEntryAsync("patch-normalize-probe", ["normalize probe body"]);

        var messyAttributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
        {
            ["Person"] = ["  Alice  "],
            ["  spaced  "] = ["x"],
            ["blank"] = ["   "],
        };
        var patched = entry with { Attributes = messyAttributes };

        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-normalize-probe", patched);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var reGet = await _client.GetAsync($"{_base}/memories/patch-normalize-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();

        reread!.Attributes.Should().NotBeNull();
        reread.Attributes!.Should().ContainKey("person",
            because: "KnownAttributes.Normalize lowercases keys, matching every other write path");
        reread.Attributes!["person"].Should().Contain("Alice",
            because: "Normalize trims values but does not change their case");
        reread.Attributes!["person"].Should().NotContain("  Alice  ",
            because: "the untrimmed raw value must not survive alongside the trimmed one");
        reread.Attributes!.Should().NotContainKey("Person",
            because: "the raw-cased key must not persist alongside the normalized \"person\" key");
        reread.Attributes!.Should().ContainKey("spaced",
            because: "Normalize trims keys, so \"  spaced  \" persists as \"spaced\"");
        reread.Attributes!.Should().NotContainKey("blank",
            because: "Normalize drops a key whose only value is blank after trimming");
    }

    /// <summary>
    /// I-3 (rejection side). PATCH has no memory content to re-run the store's keyword derivation
    /// (agent/auto classification + BM25 boosts) against, so a genuine attempt to CHANGE
    /// <c>Keywords</c> is refused with <c>400</c> rather than persisted raw — persisting it raw
    /// would let a remote client write a keyword list no local path could ever produce.
    /// </summary>
    [Fact]
    public async Task Patch_RejectsAChangedKeywordsWith400()
    {
        var entry = await StoreAndGetEntryAsync("patch-keywords-probe", ["keywords probe body about apricots"]);

        var patched = entry with { Keywords = ["not-a-real-derived-keyword"] };
        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-keywords-probe", patched);

        patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "PATCH has no content to re-derive keywords against, so a changed Keywords " +
                     "value must be refused rather than persisted raw");

        var reGet = await _client.GetAsync($"{_base}/memories/patch-keywords-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();
        reread!.Keywords.Should().BeEquivalentTo(entry.Keywords,
            because: "a rejected keyword change must leave the stored keywords exactly as they were");
    }

    /// <summary>
    /// I-3 (round-trip side) — the necessary companion to the rejection test above. A complete-entry
    /// PATCH body naturally carries whatever <c>Keywords</c> the prior GET returned; if PATCH
    /// refused every non-null <c>Keywords</c> value outright, it would refuse virtually every real
    /// metadata edit (almost every stored memory has auto-extracted keywords), which is not what
    /// I-3 asks for — only an actual CHANGE is refused.
    /// </summary>
    [Fact]
    public async Task Patch_AllowsAnUnchangedKeywordsRoundTrip()
    {
        var entry = await StoreAndGetEntryAsync(
            "patch-keywords-roundtrip-probe", ["keywords roundtrip probe body"]);

        var patched = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["mention"] = ["unrelated-change"],
            },
        };
        var patchResp = await _client.PatchAsJsonAsync(
            $"{_base}/memories/patch-keywords-roundtrip-probe", patched);

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "echoing back the same Keywords the entry already carries is not a change and " +
                     "must not be refused just because the field is present and non-null");
    }

    /// <summary>
    /// I-4. Before the fix, a body carrying only <c>name</c>/<c>updatedAt</c> returned <c>200</c>
    /// and silently erased the description, every keyword, and every attribute — the omitted
    /// <c>description</c>/<c>attributes</c>/<c>keywords</c> keys bound to null/default exactly as
    /// readily as an explicit "clear this field" would. The wire is a complete-entry contract; an
    /// incomplete body must be rejected, not treated as "set to empty."
    /// </summary>
    [Fact]
    public async Task Patch_RejectsAnIncompleteBodyWith400_AndLeavesMetadataIntact()
    {
        var entry = await StoreAndGetEntryAsync("patch-incomplete-probe", ["incomplete probe body"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["dana"] });
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!.Should().ContainKey("person");

        // Exactly the shape that silently wiped curated metadata before this fix: name + updatedAt,
        // nothing else.
        var partial = new { name = "patch-incomplete-probe", updatedAt = entry.UpdatedAt };
        var patchResp = await _client.PatchAsJsonAsync(
            $"{_base}/memories/patch-incomplete-probe", partial);

        patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a body missing description/attributes/keywords must be rejected outright, " +
                     "not treated as \"clear these fields\"");

        var reGet = await _client.GetAsync($"{_base}/memories/patch-incomplete-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();
        reread!.Attributes.Should().NotBeNull();
        reread.Attributes!.Should().ContainKey("person",
            because: "the rejected incomplete PATCH must not have touched the stored attributes — " +
                     "proving the reject happens before any write, not just that the response is 400");
    }

    /// <summary>
    /// R-5. The round-1 completeness test above only covered the all-three-missing case
    /// (<c>{"name":...,"updatedAt":...}</c>) — a mutant disabling just the <c>description</c> clause
    /// of the three-way <c>||</c> check survived all 11 round-1/round-2 tests, because nothing PATCHed
    /// a body missing exactly one field. This covers each of the three required fields individually,
    /// via one shared code path (a dictionary with all fields present, one key removed) so a mutant
    /// targeting any single field's presence check is caught regardless of which one.
    /// </summary>
    [Theory]
    [InlineData("description")]
    [InlineData("attributes")]
    [InlineData("keywords")]
    public async Task Patch_RejectsABodyMissingAnySingleRequiredFieldWith400(string omittedField)
    {
        var entry = await StoreAndGetEntryAsync($"patch-missing-{omittedField}-probe",
            ["missing single field probe body"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["dana"] });

        var fields = new Dictionary<string, object?>
        {
            ["name"] = entry.Name,
            ["updatedAt"] = entry.UpdatedAt,
            ["description"] = entry.Description,
            ["attributes"] = entry.Attributes,
            ["keywords"] = entry.Keywords,
        };
        fields.Remove(omittedField);
        string json = JsonSerializer.Serialize(fields,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var patchResp = await _client.PatchAsync($"{_base}/memories/patch-missing-{omittedField}-probe", content);

        patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"a body missing only \"{omittedField}\" must still be rejected as incomplete, " +
                     "not accepted because the other two required fields are present");
    }

    /// <summary>
    /// R-1. A well-formed but non-object JSON root ("[]", "123", "\"hello\"", "true", "null") parses
    /// fine — <c>JsonDocument.ParseAsync</c> never throws for it — but <c>TryGetProperty</c> throws
    /// <c>InvalidOperationException</c> on a non-object element, which <c>catch (JsonException)</c>
    /// does not see. Measured before the fix: all five of these returned <c>500</c>, letting an
    /// authenticated caller spam unhandled-exception logs with a one-byte body. Model binding used to
    /// reject all of these with <c>400</c>; this pins that PatchEntry's own manual parsing does too.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"hello\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Patch_RejectsANonObjectJsonBodyWith400_NotA500(string nonObjectJson)
    {
        var entry = await StoreAndGetEntryAsync("patch-nonobject-probe", ["non-object body probe"]);

        using var content = new StringContent(nonObjectJson, Encoding.UTF8, "application/json");
        var patchResp = await _client.PatchAsync($"{_base}/memories/patch-nonobject-probe", content);

        patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"a well-formed but non-object JSON root (\"{nonObjectJson}\") must be rejected " +
                     "as a 400, the same way model binding used to — not surface as an unhandled 500");
    }

    /// <summary>
    /// R-3, corrected per F-1. Taking <c>HttpRequest</c> instead of a typed parameter took ASP.NET's
    /// model binding out of the path, and model binding was also enforcing Content-Type. The original
    /// version of this test sent <c>text/plain</c> — but that case is rejected by the routing-level
    /// <c>.Accepts&lt;MemoryEntry&gt;("application/json")</c> matcher (added for R-2) BEFORE
    /// <c>PatchEntry</c> ever runs, so it passed even with <c>HasJsonContentType()</c> deleted from
    /// the handler — testing R-2's fix a second time, not R-3's. Measured: with <c>.Accepts()</c>
    /// present and the handler's own guard removed, a <c>text/plain</c> body still 415s (routing
    /// rejects first), but a body with NO <c>Content-Type</c> header at all returns <c>200</c> — the
    /// PATCH is silently applied. That is the one case <c>HasJsonContentType()</c> uniquely owns, so
    /// this now sends exactly that.
    /// </summary>
    [Fact]
    public async Task Patch_RejectsAMissingContentTypeWith415()
    {
        var entry = await StoreAndGetEntryAsync("patch-contenttype-probe", ["content type probe body"]);

        string json = JsonSerializer.Serialize(entry,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType = null; // no Content-Type header at all — not text/plain

        var patchResp = await _client.PatchAsync($"{_base}/memories/patch-contenttype-probe", content);

        patchResp.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType,
            because: "a body with no Content-Type header at all must be rejected (415) the same way " +
                     "model binding used to — routing's .Accepts() matcher does not reject this case " +
                     "on its own, so PatchEntry's own HasJsonContentType() guard is what must catch it");
    }

    /// <summary>
    /// M-3. <c>FileHashes</c> and <c>Attributes["file"]</c> are two views of one thing
    /// (<c>ArtifactEntry.cs:16-21</c>), so a PATCH that edits <c>file</c> values without
    /// re-deriving <c>FileHashes</c> can leave them disagreeing. Deliberate, documented in
    /// <c>PatchEntry</c>'s doc comment: re-hashing needs filesystem access that is a
    /// <c>StoreAsync</c>-time concern, and doing it here would be a second drift-baseline
    /// computation site. Pinned here so the behaviour cannot silently change to "sometimes
    /// re-derives" without a test noticing.
    /// </summary>
    [Fact]
    public async Task Patch_DoesNotRederiveFileHashesWhenAttributesFileChanges()
    {
        const string fileRelativePath = "patch-m3-drift-probe.txt";
        const string fileContents = "m3 drift baseline contents";

        var store = (FileMemoryStore)_factory.Services.GetRequiredService<StoreManager>()
            .GetStore(_factory.PrimaryStore);
        string fullPath = Path.Combine(store.WorkspaceRoot, fileRelativePath);
        await File.WriteAllTextAsync(fullPath, fileContents);

        var entry = await StoreAndGetEntryAsync("patch-m3-probe", ["m3 probe body"],
            attributes: new Dictionary<string, string[]> { ["file"] = [fileRelativePath] });
        entry.FileHashes.Should().NotBeNull().And.ContainKey(fileRelativePath);

        var patched = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["file"] = ["a-completely-different-path.txt"],
            },
        };
        var patchResp = await _client.PatchAsJsonAsync($"{_base}/memories/patch-m3-probe", patched);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var reGet = await _client.GetAsync($"{_base}/memories/patch-m3-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();

        reread!.Attributes!["file"].Should().BeEquivalentTo(["a-completely-different-path.txt"],
            because: "the attribute write itself must have applied");
        reread.FileHashes.Should().BeEquivalentTo(entry.FileHashes,
            because: "PatchEntry deliberately does not re-derive FileHashes — the drift baseline " +
                     "stays exactly as it was, even though Attributes[\"file\"] now points elsewhere, " +
                     "until the memory is re-stored");
    }

    /// <summary>
    /// M-1. <c>PatchEntry</c>'s <c>IsWritable</c> check had no endpoint-level coverage anywhere in
    /// this codebase — deleting it would leave the whole suite green, the exact "new road inherits
    /// no guard coverage" pattern flagged against this branch before. <c>ReadOnlyAccessTests.cs</c>
    /// only unit-tests the <c>IsWritable</c> helper directly, because no API key issued by
    /// <c>ApiKeyAuthHandler</c> ever carries the <c>store_access</c> claim the check reads — that
    /// claim is documented (<c>IsWritable</c>'s own doc comment) as arriving from SSO, and this repo
    /// has no SSO authentication path today (confirmed: no <c>IClaimsTransformation</c> is
    /// registered anywhere, and <c>ApiKeyAuthHandler</c> only ever emits <c>store</c>/<c>permission</c>
    /// claims). This test builds the smallest real stand-in: an <c>IClaimsTransformation</c> — the
    /// actual extension point real SSO claims would arrive through — registered by
    /// <see cref="ReadOnlyClaimsServerFactory"/>, marking one specific test identity's stores
    /// read-only so <c>PatchEntry</c>'s <c>IsWritable</c> branch is exercised through a genuine HTTP
    /// request. Mutation-verified: commenting out the <c>IsWritable</c> check in <c>PatchEntry</c>
    /// makes this test fail (200 instead of 403) while every other test in this file stays green.
    ///
    /// <para>Uses a dedicated <see cref="WebApplicationFactory{TEntryPoint}"/> subclass (mirroring
    /// <c>ThrowingStoreServerFactory</c> in <c>ApiVersionHeaderOn500Tests.cs</c>) rather than
    /// <c>ScriniaServerFactory.WithWebHostBuilder</c> — measured (R-4): <c>WithWebHostBuilder</c>
    /// ITSELF is what makes <c>UseKestrel(0)</c> stop binding an ephemeral port and fall back to
    /// Kestrel's hard-coded default (<c>http://127.0.0.1:5000</c>), colliding with every other
    /// Kestrel-backed test in the suite — an EARLIER version of this comment blamed the extra
    /// <c>ConfigureServices</c>/<c>ConfigureTestServices</c> call specifically, which is wrong: a
    /// bare <c>WithWebHostBuilder(b => b.UseSetting(...))</c> clone with no extra service
    /// registration ALSO binds <c>:5000</c> (measured base addresses: plain factory →
    /// <c>127.0.0.1:&lt;ephemeral&gt;</c>; any <c>WithWebHostBuilder</c> clone, config or no config →
    /// <c>localhost:5000</c>; this factory → <c>127.0.0.1:&lt;ephemeral&gt;</c>). A factory that does
    /// its extra DI registration inside its own <c>ConfigureWebHost</c> override, with
    /// <c>UseKestrel(0)</c> called directly on the instance and no <c>WithWebHostBuilder</c> in the
    /// chain at all, does not hit that failure — the same shape every other custom-backend Kestrel
    /// fixture in this project (<c>ThrowingStoreServerFactory</c>, <c>BundleEndpointTests</c>'
    /// <c>NonLocalStoreServerFactory</c>) already uses. See
    /// <c>RemoteLoadIndexTests.PagingRateLimitServerFactory</c> for the same fix applied to a
    /// pre-existing <c>WithWebHostBuilder</c> use this bug also affected.</para>
    /// </summary>
    [Fact]
    public async Task Patch_DeniedWhenStoreIsReadOnlyForThisKey()
    {
        using var factory = new ReadOnlyClaimsServerFactory();
        factory.UseKestrel(0);
        factory.StartServer();

        string baseUrl = $"/api/v1/stores/{factory.PrimaryStore}";
        var writableClient = factory.CreateAuthenticatedClient();

        var storeResp = await writableClient.PostAsJsonAsync($"{baseUrl}/memories",
            new StoreRequest(["read-only probe body"], "patch-readonly-probe", "read-only probe body"));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var entryResp = await writableClient.GetAsync($"{baseUrl}/memories/patch-readonly-probe/entry");
        var entry = await entryResp.Content.ReadFromJsonAsync<MemoryEntry>();

        // A second client, under the identity ReadOnlyClaimsServerFactory's transform singles out,
        // scoped to the same store with "store" permission — so the 403 below is provably
        // IsWritable's, not the permission check's.
        using var readOnlyClient = factory.CreateReadOnlyTargetClient();

        var patchResp = await readOnlyClient.PatchAsJsonAsync($"{baseUrl}/memories/patch-readonly-probe", entry);

        patchResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "IsWritable must deny a PATCH against a store the caller's claims mark read-only");
        var body = await patchResp.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Contain("read-only",
            because: "the 403 must genuinely be IsWritable's rejection, not an unrelated lockout");
    }

    /// <summary>
    /// R-7. <see cref="IClaimsTransformation"/>'s documented contract is that it may run more than
    /// once per request; the original <see cref="MarkTargetUserReadOnlyTransform"/> called
    /// <c>identity.AddClaim</c> directly on the incoming <see cref="ClaimsIdentity"/>, which would
    /// accumulate a duplicate <c>store_access</c> claim on a second invocation and mutate a principal
    /// object the transform does not own. This is a unit-level test of the transform itself (no HTTP
    /// needed) proving two invocations against the SAME principal instance leave it with exactly one
    /// <c>store_access</c> claim, and that the ORIGINAL principal instance is untouched (the returned
    /// principal is a different object).
    /// </summary>
    [Fact]
    public async Task MarkTargetUserReadOnlyTransform_IsIdempotent_AndDoesNotMutateTheIncomingPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId),
            new Claim("store", "test-store"),
        ], "test-scheme");
        var original = new ClaimsPrincipal(identity);

        var transform = new MarkTargetUserReadOnlyTransform();
        var firstPass = await transform.TransformAsync(original);
        var secondPass = await transform.TransformAsync(firstPass);

        secondPass.FindAll("store_access").Should().ContainSingle(
            because: "two TransformAsync invocations must not accumulate a duplicate store_access claim");
        original.FindAll("store_access").Should().BeEmpty(
            because: "the original principal instance passed in must not have been mutated — the " +
                     "transform must clone rather than call AddClaim on the incoming identity");
    }
}

/// <summary>
/// Provisions two API keys against one store: the factory's default (writable) test key, and a
/// second key under <see cref="ReadOnlyTargetUserId"/> whose <c>store_access</c> claim is stamped
/// read-only by <see cref="MarkTargetUserReadOnlyTransform"/> — the smallest real stand-in for an
/// SSO claims transformer this repo has, since none exists in production code yet.
/// </summary>
public sealed class ReadOnlyClaimsServerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    public const string ReadOnlyTargetUserId = "patch-readonly-probe-user";
    public string PrimaryStore { get; } = "test-store";
    private string TestApiKey { get; set; } = "";
    private string ReadOnlyTargetApiKey { get; set; } = "";

    public ReadOnlyClaimsServerFactory()
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
            var (rawKey, _, _) = keyStore.CreateKey(
                "readonly-test-seed-user", [PrimaryStore], ["store", "read"], "writable-seed-key");
            TestApiKey = rawKey;
            var (readOnlyRawKey, _, _) = keyStore.CreateKey(
                ReadOnlyTargetUserId, [PrimaryStore], ["store"], "readonly-probe-key");
            ReadOnlyTargetApiKey = readOnlyRawKey;
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
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

/// <summary>Test-only stand-in for an SSO claims transformer: marks every <c>store</c> claim on
/// <see cref="ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId"/>'s principal read-only, and leaves
/// every other identity (including the factory's own writable seed key) untouched.</summary>
public sealed class MarkTargetUserReadOnlyTransform : IClaimsTransformation
{
    /// <summary>R-7: <see cref="IClaimsTransformation"/>'s documented contract is that it may run
    /// more than once per request, and implementations must not mutate the incoming principal — the
    /// original shape called <c>identity.AddClaim</c> directly on the passed-in
    /// <see cref="ClaimsIdentity"/>, which would accumulate duplicate <c>store_access</c> claims
    /// across repeated invocations. This clones the identity and skips adding a claim that is
    /// already present, so a double-invocation is a no-op rather than a duplicate.</summary>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity
            || identity.FindFirst(ClaimTypes.NameIdentifier)?.Value != ReadOnlyClaimsServerFactory.ReadOnlyTargetUserId)
            return Task.FromResult(principal);

        var clonedIdentity = (ClaimsIdentity)identity.Clone();
        foreach (var storeClaim in identity.FindAll("store").ToList())
        {
            string accessClaimValue = $"{storeClaim.Value}:read-only";
            if (!clonedIdentity.HasClaim("store_access", accessClaimValue))
                clonedIdentity.AddClaim(new Claim("store_access", accessClaimValue));
        }
        return Task.FromResult(new ClaimsPrincipal(clonedIdentity));
    }
}
