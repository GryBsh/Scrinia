using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Finding H3. Six in-process copies of embed-and-index disagreed on the chunking unit: Core
/// and Server windowed by character and stamped 0-based window ordinals; the plugin never
/// windowed and stamped 1-based content-element indices. This class pins the one policy that
/// replaces them — elements are the addressable unit, windows nest inside them.
/// </summary>
public sealed class MemoryVectorIndexerTests
{
    /// <summary>Returns a distinct unit vector per input so callers can tell vectors apart.</summary>
    private sealed class CountingProvider : IEmbeddingProvider
    {
        public int BatchCalls { get; private set; }
        public List<string> LastTexts { get; private set; } = [];
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:counting";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            BatchCalls++;
            LastTexts = [.. texts];
            return Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
        }

        // IEmbeddingProvider extends IDisposable — omitting this is CS0535.
        public void Dispose() { }
    }

    /// <summary>Returns one fewer vector than asked for — the count-guard case.</summary>
    private sealed class ShortProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:short";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Skip(1).Select(_ => new[] { 1f, 0f })]);
        public void Dispose() { }
    }

    private static (MemoryVectorIndexer Indexer, VectorStore Store, string Dir) NewIndexer(
        IEmbeddingProvider provider, EmbeddingOptions? options = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-mvi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new VectorStore(dir);
        var indexer = new MemoryVectorIndexer(
            provider, store, NullLogger.Instance, options ?? new EmbeddingOptions());
        return (indexer, store, dir);
    }

    [Fact]
    public async Task ShortElements_ProduceOneVectorEach_KeyedByOneBasedElementIndex()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "beta", "gamma"], default);

        var vectors = store.GetVectors("local");
        vectors.Should().HaveCount(3);
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Should().BeEquivalentTo([1, 2, 3],
                because: "content elements are the addressable unit, 1-based, matching ChunkEntry");
    }

    [Fact]
    public async Task LongSingleElement_IsWindowed_IntoSeveralVectorsUnderOneElementIndex()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);
        string longText = new string('a', 500);

        await indexer.IndexAsync("local", "note", [longText], default);

        var vectors = store.GetVectors("local");
        vectors.Should().HaveCountGreaterThan(1,
            because: "a 500-char element at ChunkSize=100 must not collapse to one mean-pooled vector "
                   + "— that is the Vulkan-plugin regression H3 describes");
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().Equal(1);
    }

    [Fact]
    public async Task WindowsWithinOneElement_DoNotOverwriteEachOther()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);

        await indexer.IndexAsync("local", "note", [new string('a', 500)], default);

        var vectors = store.GetVectors("local");
        vectors.Select(v => v.ChunkIndex).Should().OnlyHaveUniqueItems(
            because: "VectorStore.UpsertAsync dedupes on (name, chunkIndex), so two windows "
                   + "sharing an index would silently collapse to the last one written");
    }

    [Fact]
    public async Task ChunkCountAboveCap_TruncatesTheTail()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20, MaxChunksPerMemory = 3 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);

        await indexer.IndexAsync("local", "note", [new string('a', 5000)], default);

        store.GetVectors("local").Should().HaveCount(3);
    }

    [Fact]
    public async Task ChunkCountAboveCap_AcrossMultipleElements_KeepsEarlierElementsAndDropsLaterOnes()
    {
        // ChunkSize=100/ChunkOverlap=20 gives a step of 80; a 200-char element windows into
        // exactly 3 windows (starts at 0, 80, 160). Three such elements plan 9 windows total;
        // MaxChunksPerMemory=4 cuts partway through the second element's windows, not at an
        // element boundary — this pins exactly what the cap does across elements, not just
        // within one.
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20, MaxChunksPerMemory = 4 };
        var (indexer, store, _) = NewIndexer(new CountingProvider(), options);
        string[] content = [new string('a', 200), new string('b', 200), new string('c', 200)];

        await indexer.IndexAsync("local", "note", content, default);

        var vectors = store.GetVectors("local");
        vectors.Should().HaveCount(4,
            because: "MaxChunksPerMemory caps the TOTAL window count across the whole memory, not per element");

        vectors.Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 1).Should().Be(3,
            because: "the cap falls after the first element's windows are fully embedded");
        vectors.Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 2).Should().Be(1,
            because: "the cap cuts partway through the second element's windows");
        vectors.Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 3).Should().Be(0,
            because: "the third element's windows never make it into the plan once the cap is reached");
    }

    [Fact]
    public async Task MismatchedVectorCount_LeavesExistingVectorsIntact()
    {
        var (good, store, dir) = NewIndexer(new CountingProvider());
        await good.IndexAsync("local", "note", ["alpha", "beta"], default);
        int before = store.GetVectors("local").Count;

        var bad = new MemoryVectorIndexer(
            new ShortProvider(), store, NullLogger.Instance, new EmbeddingOptions());
        await bad.IndexAsync("local", "note", ["alpha", "beta"], default);

        store.GetVectors("local").Should().HaveCount(before,
            because: "the guard must return before RemoveAsync, or a miscounting provider "
                   + "wipes the memory's vectors and writes nothing back");
    }

    [Fact]
    public async Task Reindexing_ReplacesRatherThanAccumulates()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "beta", "gamma"], default);
        await indexer.IndexAsync("local", "note", ["alpha"], default);

        store.GetVectors("local").Should().HaveCount(1,
            because: "remove-then-upsert is what stops a shrunk memory keeping stale higher chunks");
    }

    [Fact]
    public async Task BlankElements_AreSkippedWithoutShiftingLaterIndices()
    {
        var (indexer, store, _) = NewIndexer(new CountingProvider());

        await indexer.IndexAsync("local", "note", ["alpha", "   ", "gamma"], default);

        var vectors = store.GetVectors("local");
        vectors.Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Should().BeEquivalentTo([1, 3],
                because: "element index is the memory's own addressing — a blank element still "
                       + "occupies its slot, and ChunkEntry numbering does not renumber around it");
    }

    [Fact]
    public void Construction_RejectsACapThatCouldOverflowTheStride()
    {
        var options = new EmbeddingOptions { MaxChunksPerMemory = MemoryVectorIndexer.ChunkIndexStride };
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-mvi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        Action act = () => new MemoryVectorIndexer(
            new CountingProvider(), new VectorStore(dir), NullLogger.Instance, options);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RetryOnUnavailableFalse_PropagatesTheExceptionRatherThanRetryingAndSwallowingToFalse()
    {
        // Bulk reindex loops (EmbeddingReindexer.ReindexAsync, BuiltInEmbeddingsService.
        // ReindexStoreAsync) pass retryOnUnavailable:false so an unreadable vector file surfaces
        // as VectorStoreUnavailableException to their own catch ladder (counted as "failed"),
        // rather than being retried by VectorWriteRetry for up to 42s and then swallowed to
        // false (which their code would otherwise mis-count as "skipped").
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-mvi-" + Guid.NewGuid().ToString("N"));
        string scopeDir = Path.Combine(dir, "local");
        Directory.CreateDirectory(scopeDir);
        string vectorsPath = Path.Combine(scopeDir, "vectors.bin");

        // Write a real vectors.bin so there is something for a fresh VectorStore instance to
        // try (and fail) to load.
        var seedStore = new VectorStore(dir);
        await seedStore.UpsertAsync("local", "note", null, [1f, 0f]);

        // A fresh instance with a COLD scope cache: its first operation against "local" must go
        // to disk. Locking the file exclusively for the duration of the call makes that disk
        // read fail the way a held handle or a slow network filesystem would.
        var freshStore = new VectorStore(dir);
        var indexer = new MemoryVectorIndexer(new CountingProvider(), freshStore, NullLogger.Instance);

        using var lockHandle = new FileStream(vectorsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Func<Task> act = async () =>
            await indexer.IndexAsync("local", "note", ["alpha"], default, retryOnUnavailable: false);

        await act.Should().ThrowAsync<VectorStoreUnavailableException>();
    }
}
