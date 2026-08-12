using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
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
    /// THE load-bearing guard. Stores a memory that has <c>TermFrequencies</c>,
    /// <c>ChunkEntries</c> and <c>FileHashes</c>; PATCHes one attribute; re-reads; asserts all
    /// three are unchanged. This is the entire reason the wire uses one complete-entry PATCH body
    /// instead of a field-scoped patch DTO: the body the client sends back necessarily carries
    /// these three fields too (it round-tripped them from the GET), and the server must not let
    /// them overwrite what it alone derives.
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

        var patched = entry with
        {
            Attributes = new Dictionary<string, string[]>(entry.Attributes ?? [])
            {
                ["mention"] = ["survives-the-patch"],
            },
        };
        var patchResp = await _client.PatchAsJsonAsync(
            $"{_base}/memories/patch-derived-survival-probe", patched);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var reGet = await _client.GetAsync($"{_base}/memories/patch-derived-survival-probe/entry");
        var reread = await reGet.Content.ReadFromJsonAsync<MemoryEntry>();

        reread!.TermFrequencies.Should().BeEquivalentTo(entry.TermFrequencies,
            because: "a metadata-only PATCH must not touch the BM25 term-frequency weights the store derived");
        reread.ChunkEntries.Should().BeEquivalentTo(entry.ChunkEntries,
            because: "a metadata-only PATCH must not touch the per-chunk entries the store derived");
        reread.FileHashes.Should().BeEquivalentTo(entry.FileHashes,
            because: "a metadata-only PATCH must not touch the file attribute's drift baseline");

        // The attribute promotion this test carries alongside the guard did apply.
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
    }
}
