using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures <see cref="MemoryVectorIndexer.IndexAsync"/> writing K vectors for ONE memory —
/// part C's "one locked file operation per memory, not per vector."
///
/// Before, <c>IndexAsync</c> ran <c>RemoveAsync</c> then a loop of K <c>UpsertAsync</c> calls: 1+K
/// scope-lock acquisitions and up to P+2K on-disk append operations, each opening and closing the
/// vector file for itself. After, the whole batch goes through
/// <see cref="VectorStore.ReplaceMemoryVectorsAsync"/>: one lock acquisition, one file probe, one
/// open/close carrying every delete and add op for the memory.
///
/// <para><see cref="FakeProvider"/> embeds every window into a fixed 3-dimensional vector with no
/// model, network, or GPU involved, so the timing isolates the storage write rather than a real
/// embedding call's latency. Content length is chosen so <see cref="TextChunker.SliceWindows"/>
/// under the default chunk size/overlap produces exactly <see cref="Vectors"/> windows — see
/// <see cref="BuildContent"/>.</para>
///
/// <para>Runs under <see cref="MonitoringInProcessConfig"/> for the same reason
/// <c>WritePathBenchmarks</c> does: <see cref="IterationSetup"/>/<see cref="IterationCleanup"/>
/// build and tear down a real temp <see cref="VectorStore"/> per iteration, and Monitoring is what
/// guarantees exactly one invocation runs between them. See that class for why the toolchain is
/// also pinned to in-process.</para>
/// </summary>
[Config(typeof(MonitoringInProcessConfig))]
public class VectorWriteBenchmarks
{
    [Params(1, 10, 50, 200)]
    public int Vectors;

    private string _root = null!;
    private VectorStore _store = null!;
    private MemoryVectorIndexer _indexer = null!;
    private string[] _content = null!;

    [IterationSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_bench_vec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new VectorStore(_root, expectedSignature: null);

        var options = new EmbeddingOptions();
        _indexer = new MemoryVectorIndexer(new FakeProvider(), _store, NullLogger.Instance, options);

        // One content element, sized to produce exactly `Vectors` windows — matches the
        // single-element case MemoryVectorIndexer windows internally.
        _content = [BuildContent(Vectors, options.ChunkSize, options.ChunkOverlap)];
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Benchmark]
    public async Task IndexOneMemory()
    {
        // retryOnUnavailable: false — bulk-style call, matches EmbeddingReindexer's usage and
        // keeps VectorWriteRetry's backoff out of the measured path (see the class doc comment on
        // MemoryVectorIndexer for why the two call shapes exist).
        await _indexer.IndexAsync("local", "bench-memory", _content, CancellationToken.None, retryOnUnavailable: false);
    }

    /// <summary>
    /// Builds a single string whose length makes <see cref="TextChunker.SliceWindows"/> emit
    /// exactly <paramref name="windows"/> windows. The sliding window steps by
    /// <c>windowSize - overlap</c> chars after the first window, so a length of
    /// <c>windowSize + (windows - 1) * step</c> lands the final window's end exactly on the
    /// string's end — verified against <c>TextChunker.SliceWindows</c>'s loop, which breaks the
    /// instant <c>end &gt;= text.Length</c>. Plain 'a' characters avoid the whitespace-trim at
    /// each window boundary changing the count.
    /// </summary>
    private static string BuildContent(int windows, int windowSize, int overlap)
    {
        int step = windowSize - overlap;
        int length = windowSize + (windows - 1) * step;
        return new string('a', length);
    }

    private sealed class FakeProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 3;
        public string Signature => "fake:bench";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<float[]?>([(float)text.Length, 0f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<float[][]?>(texts.Select(t => new[] { (float)t.Length, 0f, 0f }).ToArray());

        public void Dispose() { }
    }
}
