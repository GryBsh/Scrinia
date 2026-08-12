using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Task 6 review finding I-1: nothing in the suite asserted <c>termFrequencies</c> /
/// <c>chunkEntries</c> / <c>fileHashes</c> on the wire, so a regression to <c>null</c>, a dropped
/// mapping, or a wrong <see cref="ChunkEntryDto"/> positional order in
/// <c>MemoryEndpoints.ListMemory</c> would all have passed green. This pins all three against the
/// real <c>GET /memories</c> JSON, over a multi-chunk memory that also carries a <c>file</c>
/// reference — <c>fileHashes</c> is the least-proven field of the three (the reviewer's throwaway
/// probe only ever saw <c>"fileHashes":null</c>), so this seeds a real, hashable file rather than
/// an unresolvable path.
/// </summary>
public sealed class ListResponseCompleteEntryTests
{
    private const string Chunk1 = "Apricot orchard maintenance notes. Apricot trees need pruning every spring.";
    private const string Chunk2 = "Banana plantation records. Banana trees thrive near equatorial banana farms.";
    private const string FileRelativePath = "drift-probe.txt";
    private const string FileContents = "drift baseline contents for the wire-coverage probe";

    [Fact]
    public async Task ListResponse_CarriesTermFrequencies_ChunkEntries_AndFileHashes()
    {
        using var factory = new ScriniaServerFactory();
        var client = factory.CreateAuthenticatedClient();

        // Plant the drift-baseline file inside the store's own workspace root —
        // FileMemoryStore.HashWorkspaceFile resolves `file` attribute values against exactly this
        // path, so a value that does not exist on disk hashes to null and proves nothing about the
        // field actually carrying data.
        var store = (FileMemoryStore)factory.Services.GetRequiredService<StoreManager>()
            .GetStore(factory.PrimaryStore);
        string fullPath = Path.Combine(store.WorkspaceRoot, FileRelativePath);
        await File.WriteAllTextAsync(fullPath, FileContents);
        string expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(FileContents)));

        var storeResp = await client.PostAsJsonAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories",
            new StoreRequest(
                [Chunk1, Chunk2],
                "wire-complete-entry-probe",
                Attributes: new Dictionary<string, string[]> { ["file"] = [FileRelativePath] },
                Keywords: ["portcullis"]));
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResp = await client.GetAsync(
            $"/api/v1/stores/{factory.PrimaryStore}/memories?limit=1000");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("memories").EnumerateArray()
            .Single(m => m.GetProperty("qualifiedName").GetString() == "wire-complete-entry-probe");

        // ── termFrequencies: non-empty, with the expected terms ─────────────────────────────
        item.TryGetProperty("termFrequencies", out var tf).Should().BeTrue(
            because: "the wire must carry the memory-level term frequencies, not drop the field");
        tf.ValueKind.Should().Be(JsonValueKind.Object);
        tf.EnumerateObject().Should().NotBeEmpty();
        tf.GetProperty("apricot").GetInt32().Should().BeGreaterThanOrEqualTo(2,
            because: "\"apricot\" appears twice in chunk 1 alone");
        tf.GetProperty("banana").GetInt32().Should().BeGreaterThanOrEqualTo(3,
            because: "\"banana\" appears three times in chunk 2 alone");

        // ── chunkEntries: one per chunk, IN ORDER, every field in the right slot ────────────
        // This is the assertion that catches a positional-order mistake in the ChunkEntryDto
        // construction (MemoryEndpoints.cs, ListMemory): swapping ChunkIndex/ContentPreview or
        // Keywords/TermFrequencies at construction would put chunk 1's data under chunk 2's slot,
        // or vice versa, and every check below is keyed to one chunk's distinctive vocabulary.
        item.TryGetProperty("chunkEntries", out var chunkEntries).Should().BeTrue(
            because: "the wire must carry per-chunk entries for a multi-chunk memory");
        chunkEntries.ValueKind.Should().Be(JsonValueKind.Array);
        var entries = chunkEntries.EnumerateArray().ToList();
        entries.Should().HaveCount(2, because: "the memory was stored with two chunks");

        entries[0].GetProperty("chunkIndex").GetInt32().Should().Be(1);
        entries[0].GetProperty("contentPreview").GetString().Should().Be(Chunk1);
        entries[0].GetProperty("keywords").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("apricot");
        entries[0].GetProperty("termFrequencies").GetProperty("apricot").GetInt32()
            .Should().BeGreaterThanOrEqualTo(2);
        entries[0].GetProperty("termFrequencies").TryGetProperty("banana", out _).Should().BeFalse(
            because: "chunk 1 never mentions banana — entries[0] holding chunk 2's data (a swapped " +
                     "slot) would fail this");

        entries[1].GetProperty("chunkIndex").GetInt32().Should().Be(2);
        entries[1].GetProperty("contentPreview").GetString().Should().Be(Chunk2);
        entries[1].GetProperty("keywords").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("banana");
        entries[1].GetProperty("termFrequencies").GetProperty("banana").GetInt32()
            .Should().BeGreaterThanOrEqualTo(3);
        entries[1].GetProperty("termFrequencies").TryGetProperty("apricot", out _).Should().BeFalse(
            because: "chunk 2 never mentions apricot — entries[1] holding chunk 1's data (a swapped " +
                     "slot) would fail this");

        // ── fileHashes: non-null, keyed as expected ─────────────────────────────────────────
        item.TryGetProperty("fileHashes", out var fileHashes).Should().BeTrue(
            because: "the wire must carry the drift baseline, not drop it");
        fileHashes.ValueKind.Should().Be(JsonValueKind.Object);
        fileHashes.GetProperty(FileRelativePath).GetString().Should().Be(expectedHash,
            because: "the persisted SHA-256 baseline for the referenced file must reach the wire " +
                     "unchanged, keyed by the exact attribute value");

        // ── agentKeywords: which of the keywords the caller supplied ────────────────────────
        // The list on the wire is the merge of the caller's with the auto-extracted terms, and a
        // compaction on the client side reads this field to decide which of them keep agent
        // weight. Absent, every remote memory falls back to a heuristic that cannot recover a
        // curated term the content also yields — so the store mode would decide the ranking.
        item.TryGetProperty("agentKeywords", out var agentKeywords).Should().BeTrue(
            because: "provenance is derived state like the three fields above, and travels whole");
        agentKeywords.EnumerateArray().Select(e => e.GetString()).Should().Equal(["portcullis"]);
        item.GetProperty("keywords").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("apricot",
                because: "precondition: the keyword list really does hold auto-extracted terms " +
                         "alongside the curated one, or the assertion above proves nothing");
    }
}
