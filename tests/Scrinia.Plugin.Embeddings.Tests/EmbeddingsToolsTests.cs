using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Plugin.Embeddings.Cli;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Direct coverage for <see cref="EmbeddingsTools"/>, the out-of-process plugin's MCP wire
/// surface. It had none, though this branch added its <c>index</c> tool (the replacement for
/// per-window <c>upsert</c> calls) and rewrote <c>Search</c> to project through
/// <see cref="VectorScoreProjection"/> — previously it wrote chunk scores over memory scores
/// without aggregating, so a low-scoring chunk hit could overwrite a high-scoring memory score
/// rather than lose to it.
/// </summary>
public class EmbeddingsToolsTests : IDisposable
{
    private readonly string _dir;
    private readonly VectorStore _store;

    public EmbeddingsToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scrinia-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new VectorStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Marker text embeds to the X axis, everything else to the Y axis.</summary>
    private sealed class MarkerProvider(string marker) : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:marker";

        private float[] Encode(string text) =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase) ? [1f, 0f] : [0f, 1f];

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(Encode(text));
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select(Encode)]);
        public void Dispose() { }
    }

    private EmbeddingsTools ToolsWith(IEmbeddingProvider provider, EmbeddingOptions? options = null)
    {
        options ??= new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        return new EmbeddingsTools(
            _store, provider, options,
            new MemoryVectorIndexer(provider, _store, NullLogger.Instance, options));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── index ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_KeysVectorsByOneBasedContentElement()
    {
        var tools = ToolsWith(new MarkerProvider("zzz"));

        string json = await tools.Index("local", "note", ["alpha", "bravo", "charlie"]);

        Parse(json).GetProperty("ok").GetBoolean().Should().BeTrue();
        _store.GetVectors("local")
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Should().BeEquivalentTo([1, 2, 3],
                because: "the index tool exists so the plugin stops stamping raw window ordinals "
                       + "into the position the scorer reads a content-element index from");
    }

    [Fact]
    public async Task Index_ReplacesAMemorysVectorsRatherThanAccumulating()
    {
        var tools = ToolsWith(new MarkerProvider("zzz"));

        await tools.Index("local", "note", ["alpha", "bravo", "charlie"]);
        await tools.Index("local", "note", ["alpha"]);

        _store.GetVectors("local").Should().HaveCount(1,
            because: "an append re-indexes the FULL post-append content, so a shrink must not "
                   + "leave stale higher element indices behind");
    }

    [Fact]
    public async Task Index_WindowsALongElement_UnderASingleElementIndex()
    {
        var tools = ToolsWith(new MarkerProvider("zzz"));

        await tools.Index("local", "note", [new string('a', 500)]);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCountGreaterThan(1,
            because: "the plugin used to mean-pool a whole element into one vector");
        vectors.Select(v => v.ChunkIndex).Should().OnlyHaveUniqueItems();
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().Equal(1);
    }

    [Fact]
    public async Task Index_EmptyContent_ReportsNotWritten_WithoutError()
    {
        var tools = ToolsWith(new MarkerProvider("zzz"));

        var root = Parse(await tools.Index("local", "note", []));

        root.TryGetProperty("error", out _).Should().BeFalse();
        root.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Index_AnUnreadableVectorFile_ReturnsAnError_RatherThanRetryingInProcess()
    {
        // retryOnUnavailable:false at this call site: this tool is the wire surface for a bulk
        // `scri reindex` replay too, and McpPluginHost.CallToolWithRetryAsync already owns retry
        // at the process boundary. Retrying here as well would double it, and a large reindex
        // against a locked file would stall for hours instead of reporting per memory.
        var tools = ToolsWith(new MarkerProvider("zzz"));
        await tools.Index("local", "note", ["seed"]);

        string scopeDir = Path.Combine(_dir, "local");
        using var freshStore = new VectorStore(_dir);
        var provider = new MarkerProvider("zzz");
        var coldTools = new EmbeddingsTools(
            freshStore, provider, new EmbeddingOptions(),
            new MemoryVectorIndexer(provider, freshStore, NullLogger.Instance));

        using var lockHandle = new FileStream(
            Path.Combine(scopeDir, "vectors.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        var started = DateTimeOffset.UtcNow;
        var root = Parse(await coldTools.Index("local", "note", ["alpha"]));
        var elapsed = DateTimeOffset.UtcNow - started;

        root.TryGetProperty("error", out _).Should().BeTrue(
            because: "the caller retries or falls back to BM25; it cannot do either if the tool "
                   + "swallows the failure into ok:false");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            because: "VectorWriteRetry's in-process backoff runs up to ~42s and must not apply here");
    }

    // ── search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ProjectsBothTheMemoryKeyAndTheElementKey()
    {
        const string marker = "xylophone";
        var tools = ToolsWith(new MarkerProvider(marker));
        await tools.Index("local", "note", ["alpha bravo", $"{marker} marimba", "charlie delta"]);

        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(
            await tools.Search(marker, ["local"]))!;

        using var scope = new FluentAssertions.Execution.AssertionScope();
        scores.Should().ContainKey("local|note");
        scores.Should().ContainKey("local|note|2",
            because: "the marker is in content element 2, and the scorer probes "
                   + "\"{scope}|{name}|{chunkIndex}\" with ChunkEntry's 1-based element index");
        scores.Keys.Should().NotContain("local|note|0",
            because: "element indices are 1-based; a 0 would mean a raw window ordinal leaked out");
    }

    [Fact]
    public async Task Search_TakesTheMaximum_SoALowChunkHitCannotOverwriteAHighMemoryScore()
    {
        // The regression this rewrite fixed: the old loop assigned rather than max'd, so whichever
        // vector came last won the memory key. Element 1 matches the query, element 2 does not;
        // the memory key must carry element 1's score, not element 2's.
        const string marker = "xylophone";
        var tools = ToolsWith(new MarkerProvider(marker));
        await tools.Index("local", "note", [$"{marker} first", "unrelated second", "unrelated third"]);

        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(
            await tools.Search(marker, ["local"]))!;

        scores["local|note"].Should().Be(scores["local|note|1"],
            because: "the memory key carries the maximum over its elements — the best-matching "
                   + "chunk's score wins, rather than the last one written");
        scores["local|note"].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Search_AnUnembeddableQuery_ReturnsAnError()
    {
        var tools = ToolsWith(new NullEmbeddingProvider());

        Parse(await tools.Search("anything", ["local"]))
            .TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Search_AScopeWithNoVectors_ReturnsAnEmptyMap_NotAnError()
    {
        var tools = ToolsWith(new MarkerProvider("zzz"));

        string json = await tools.Search("anything", ["empty-scope"]);

        JsonSerializer.Deserialize<Dictionary<string, double>>(json).Should().BeEmpty();
    }
}
