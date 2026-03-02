using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for chunk-level indexing: per-chunk keywords/TF/preview in the index,
/// chunk-level search results, and removal of auto-chunking.
/// </summary>
public sealed class ChunkIndexingTests
{
    private static ScriniaMcpTools Tools() => new();

    // ── Store chunk entries ──────────────────────────────────────────────────

    [Fact]
    public async Task Store_MultiElement_CreatesChunkEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["## Auth\nOAuth2 JWT flow.", "## Users\nCRUD endpoint roles."];

        await Tools().Store(chunks, "chunk-idx");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "chunk-idx").Which;
        entry.ChunkEntries.Should().NotBeNull();
        entry.ChunkEntries.Should().HaveCount(2);
        entry.ChunkEntries![0].ChunkIndex.Should().Be(1);
        entry.ChunkEntries![1].ChunkIndex.Should().Be(2);
        entry.ChunkEntries![0].ContentPreview.Should().NotBeNullOrEmpty();
        entry.ChunkEntries![1].ContentPreview.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Store_SingleElement_NoChunkEntries()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["Just one chunk."], "single-idx");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "single-idx").Which;
        entry.ChunkEntries.Should().BeNull();
    }

    [Fact]
    public async Task Store_ChunkKeywords_AreDistinct()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks =
        [
            "Kubernetes pods containers orchestration cluster deployment",
            "PostgreSQL database tables indexes queries transactions"
        ];

        await Tools().Store(chunks, "distinct-kw");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "distinct-kw").Which;
        entry.ChunkEntries.Should().HaveCount(2);

        var kw1 = entry.ChunkEntries![0].Keywords;
        var kw2 = entry.ChunkEntries![1].Keywords;
        kw1.Should().NotBeNull();
        kw2.Should().NotBeNull();

        // Chunk 1 should have kubernetes-related keywords but not postgresql
        kw1.Should().Contain(k => k.Contains("kubernetes", StringComparison.OrdinalIgnoreCase));
        kw2.Should().Contain(k => k.Contains("postgresql", StringComparison.OrdinalIgnoreCase));
    }

    // ── Append chunk entries ─────────────────────────────────────────────────

    [Fact]
    public async Task Append_AddsChunkEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["Initial single chunk."], "append-ce");

        await Tools().Append("Appended new chunk with unique xylophone content.", "append-ce");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "append-ce").Which;
        entry.ChunkEntries.Should().NotBeNull();
        // Append to a single-chunk creates a chunk entry for the appended chunk
        entry.ChunkEntries.Should().ContainSingle();
        entry.ChunkEntries![0].ChunkIndex.Should().Be(2);
        entry.ChunkEntries![0].Keywords.Should().NotBeNull();
    }

    [Fact]
    public async Task Append_ToMultiChunk_ExtendsChunkEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] origChunks = ["Chunk alpha content.", "Chunk bravo content."];
        await Tools().Store(origChunks, "append-multi");

        await Tools().Append("Chunk charlie content.", "append-multi");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "append-multi").Which;
        entry.ChunkEntries.Should().NotBeNull();
        entry.ChunkEntries.Should().HaveCount(3);
        entry.ChunkEntries![0].ChunkIndex.Should().Be(1);
        entry.ChunkEntries![1].ChunkIndex.Should().Be(2);
        entry.ChunkEntries![2].ChunkIndex.Should().Be(3);
    }

    // ── Search chunk results ─────────────────────────────────────────────────

    [Fact]
    public async Task Search_ChunkMatch_ReturnsChunkEntryResult()
    {
        using var scope = new TestHelpers.StoreScope();
        // Chunk 1 has "authentication", chunk 2 has "xylophone" (a unique keyword)
        string[] chunks =
        [
            "Authentication OAuth2 bearer tokens JWT refresh flow login",
            "Xylophone marimba vibraphone percussion instruments musical"
        ];
        await Tools().Store(chunks, "search-chunk");

        // Search for a keyword unique to chunk 2
        var results = ScriniaArtifactStore.SearchAll("xylophone");
        results.Should().HaveCountGreaterThan(0);

        var chunkResult = results.OfType<ChunkEntryResult>().FirstOrDefault();
        chunkResult.Should().NotBeNull("search should find the specific chunk");
        chunkResult!.Chunk.ChunkIndex.Should().Be(2);
        chunkResult.TotalChunks.Should().Be(2);
    }

    [Fact]
    public async Task Search_Dedup_KeepsBestResult()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store with a name and chunk content that both match the query
        string[] chunks = ["Alpha content here.", "Beta content here."];
        await Tools().Store(chunks, "dedup-test", description: "alpha beta test");

        var results = ScriniaArtifactStore.SearchAll("alpha");
        // Should only have one result for dedup-test (not both entry + chunk)
        var dedupResults = results
            .Where(r => r is EntryResult er && er.Item.Entry.Name == "dedup-test"
                     || r is ChunkEntryResult cr && cr.ParentItem.Entry.Name == "dedup-test")
            .ToList();
        dedupResults.Should().HaveCount(1, "deduplication should keep only the best result per memory");
    }

    [Fact]
    public async Task Search_ChunkOutput_ContainsChunkLabel()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks =
        [
            "Authentication OAuth2 bearer tokens refresh",
            "Xylophone marimba vibraphone percussion musical"
        ];
        await Tools().Store(chunks, "label-test");

        string output = await Tools().Search("xylophone");
        output.Should().Contain("[chunk");
        output.Should().Contain("chunk");
    }

    // ── Copy preserves chunk entries ─────────────────────────────────────────

    [Fact]
    public async Task Copy_PreservesChunkEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks = ["Part one content.", "Part two content."];
        await Tools().Store(chunks, "copy-src");

        await Tools().Copy("copy-src", "copy-dst", overwrite: true);

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var dst = entries.Should().ContainSingle(e => e.Name == "copy-dst").Which;
        dst.ChunkEntries.Should().NotBeNull();
        dst.ChunkEntries.Should().HaveCount(2);
    }

    // ── Chunk scoring with parent metadata (PR 5) ─────────────────────────────

    [Fact]
    public async Task Search_ChunkInheritsParentKeywords()
    {
        using var scope = new TestHelpers.StoreScope();
        // Store with parent keywords and chunks that don't directly mention those keywords
        string[] chunks =
        [
            "Configuration patterns for container orchestration systems.",
            "Monitoring alerting dashboards and observability."
        ];
        await Tools().Store(chunks, "infra-guide",
            keywords: ["kubernetes", "docker", "infrastructure"]);

        // Search for "kubernetes" — chunk should inherit parent keyword score
        var results = ScriniaArtifactStore.SearchAll("kubernetes");
        results.Should().HaveCountGreaterThan(0);

        // The parent-level result should have a positive score from the keyword match
        var anyResult = results.FirstOrDefault(r =>
            (r is EntryResult er && er.Item.Entry.Name == "infra-guide") ||
            (r is ChunkEntryResult cr && cr.ParentItem.Entry.Name == "infra-guide"));
        anyResult.Should().NotBeNull("parent keyword 'kubernetes' should produce a match");
        anyResult!.Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Search_ChunkInheritsParentTags()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] chunks =
        [
            "Endpoint handler implementation details.",
            "Database migration scripts and procedures."
        ];
        await Tools().Store(chunks, "backend-docs",
            tags: ["backend", "api"]);

        // Search for "backend" — should find via parent tag
        var results = ScriniaArtifactStore.SearchAll("backend");
        results.Should().HaveCountGreaterThan(0);
    }

    // ── Encode no longer auto-chunks ─────────────────────────────────────────

    [Fact]
    public void Encode_LargeContent_SingleChunk()
    {
        // 40K chars — previously would have been auto-chunked
        string large = new string('X', 40_000);
        string artifact = Nmp2ChunkedEncoder.Encode(large);

        Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(1,
            because: "Encode() no longer auto-chunks — single element always produces single chunk");

        // Verify roundtrip
        string decoded = Nmp2ChunkedEncoder.DecodeChunk(artifact, 1);
        decoded.Should().Be(large);
    }
}
