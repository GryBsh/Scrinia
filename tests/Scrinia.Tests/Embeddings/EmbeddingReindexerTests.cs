using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Integration coverage for chunked reindex: confirms that long memories produce one
/// vector per slice with sequential chunk indices, short memories produce one chunk-0
/// vector, and a shrink-replace (re-embedding a memory with fewer chunks than last time)
/// leaves no orphaned stale-index vectors.
/// </summary>
public class EmbeddingReindexerTests : IDisposable
{
    private readonly string _root;
    private readonly string _embeddingsDir;
    private readonly FileMemoryStore _store;

    public EmbeddingReindexerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_reidx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(_embeddingsDir);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void AddMemory(string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow, "desc");
        _store.Upsert(entry, "local");
        string path = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task LongMemory_ProducesMultipleChunkVectors()
    {
        AddMemory("long", new string('x', 3000));

        var provider = new FakeBatchProvider();
        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var opts = new EmbeddingOptions { ChunkSize = 1000, ChunkOverlap = 100 };

        var result = await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None, opts);

        result.Total.Should().Be(1);
        result.Embedded.Should().Be(1);

        var vectors = vectorStore.GetVectors("local");
        // 3000-char text with window 1000 / overlap 100 (step 900): chunks at [0..1000],
        // [900..1900], [1800..2800], [2700..3000] → expect 4 chunks.
        vectors.Should().HaveCountGreaterThanOrEqualTo(3);
        vectors.All(v => v.Name == "long").Should().BeTrue();
        vectors.Select(v => v.ChunkIndex).Should().BeEquivalentTo(
            Enumerable.Range(0, vectors.Count).Select(i => (int?)i));
    }

    [Fact]
    public async Task ShortMemory_ProducesSingleChunkVector()
    {
        AddMemory("short", "small content well under one window");

        var provider = new FakeBatchProvider();
        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var opts = new EmbeddingOptions { ChunkSize = 1200, ChunkOverlap = 200 };

        var result = await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None, opts);

        result.Embedded.Should().Be(1);
        var vectors = vectorStore.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].ChunkIndex.Should().Be(0);
    }

    [Fact]
    public async Task Reindex_ShrinkReplace_LeavesNoOrphans()
    {
        // First pass: long content → multiple chunks.
        AddMemory("memo", new string('a', 3000));

        var provider = new FakeBatchProvider();
        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var opts = new EmbeddingOptions { ChunkSize = 1000, ChunkOverlap = 100 };

        await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None, opts);
        int firstCount = vectorStore.GetVectors("local").Count;
        firstCount.Should().BeGreaterThan(1);

        // Shrink the artifact in place (simulate an edit that reduces chunk count to 1).
        string path = _store.ArtifactPath("memo", "local");
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode("tiny replacement content"));

        await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None, opts);

        var vectors = vectorStore.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].ChunkIndex.Should().Be(0);
    }

    [Fact]
    public async Task ChunkCap_HonoredWhenContentVeryLarge()
    {
        AddMemory("huge", new string('z', 20_000));

        var provider = new FakeBatchProvider();
        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var opts = new EmbeddingOptions
        {
            ChunkSize = 500,
            ChunkOverlap = 50,
            MaxChunksPerMemory = 10,
        };

        await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None, opts);

        vectorStore.GetVectors("local").Should().HaveCount(10);
    }

    [Fact]
    public async Task ForceReindex_RebuildsWhenSignaturesMatch()
    {
        // Regression for the broken `scri reindex` flow: the user wants a force rebuild even
        // when the signature on disk matches the active config (suspected corruption / manual
        // recovery). `ReindexIfStaleAsync` short-circuits when nothing was quarantined, so the
        // command MUST use `ForceReindexAsync` to actually rebuild.
        AddMemory("memo", "first content");

        var provider = new FakeBatchProvider();
        var opts = new EmbeddingOptions { ChunkSize = 1200, ChunkOverlap = 200 };
        string signature = ChunkedSignature.Compose(provider.Signature, opts.ChunkSize, opts.ChunkOverlap);

        // Seed the on-disk store with a vector using the matching signature so a stale-only
        // path would skip the rebuild.
        using (var seed = new VectorStore(_embeddingsDir, expectedSignature: signature))
        {
            await seed.UpsertAsync("local", "memo", 0, [99f, 0f, 0f]);
        }

        // ReindexIfStaleAsync would see HasStaleQuarantines == false and bail without writing.
        var staleResult = await EmbeddingReindexer.ReindexIfStaleAsync(
            _store, provider, _embeddingsDir, NullLogger.Instance, progress: null, CancellationToken.None, opts);
        staleResult.Should().BeNull("signature matches so the auto-reindex correctly short-circuits");

        // ForceReindexAsync must rebuild unconditionally.
        var forced = await EmbeddingReindexer.ForceReindexAsync(
            _store, provider, _embeddingsDir, NullLogger.Instance, progress: null, CancellationToken.None, opts);
        forced.Total.Should().Be(1);
        forced.Embedded.Should().Be(1);

        using var verify = new VectorStore(_embeddingsDir, expectedSignature: signature);
        var vectors = verify.GetVectors("local");
        vectors.Should().HaveCount(1);
        // The fresh vector has first component = chunk text length (~13), not the seeded 99f.
        vectors[0].Vector[0].Should().NotBe(99f);
    }

    [Fact]
    public async Task ReindexViaSink_FiresOnStoredPerMemory_WithDecodedContent()
    {
        AddMemory("alpha", "alpha content body");
        AddMemory("beta", "beta content body");

        var sink = new CaptureSink();

        var result = await EmbeddingReindexer.ReindexViaSinkAsync(
            _store, sink, NullLogger.Instance, progress: null, CancellationToken.None);

        result.Total.Should().Be(2);
        result.Embedded.Should().Be(2);
        result.Failed.Should().Be(0);
        sink.Stored.Should().HaveCount(2);
        sink.Stored.Select(s => s.qualifiedName).Should().BeEquivalentTo(["alpha", "beta"]);
        // Decoded payload should be the original raw content, not the encoded artifact.
        sink.Stored.Should().OnlyContain(s => s.content.Single().Contains("content body"));
    }

    [Fact]
    public async Task ReindexViaSink_SinkFailure_CountedAsFailedNotFatal()
    {
        AddMemory("good", "this one succeeds");
        AddMemory("bad", "this one throws");

        var sink = new CaptureSink(failOnQualifiedName: "bad");

        var result = await EmbeddingReindexer.ReindexViaSinkAsync(
            _store, sink, NullLogger.Instance, progress: null, CancellationToken.None);

        result.Total.Should().Be(2);
        result.Embedded.Should().Be(1);
        result.Failed.Should().Be(1);
    }

    private sealed class CaptureSink : IMemoryEventSink
    {
        private readonly string? _failOnQualifiedName;
        public List<(string qualifiedName, string[] content)> Stored { get; } = [];

        public CaptureSink(string? failOnQualifiedName = null)
        {
            _failOnQualifiedName = failOnQualifiedName;
        }

        public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        {
            if (qualifiedName == _failOnQualifiedName)
                throw new InvalidOperationException("simulated sink failure");
            Stored.Add((qualifiedName, content));
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
            => Task.CompletedTask;
        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeBatchProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 3;
        public string Signature => "fake:test";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<float[]?>([(float)text.Length, 0f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<float[][]?>(texts.Select(t => new[] { (float)t.Length, 0f, 0f }).ToArray());

        public void Dispose() { }
    }
}
