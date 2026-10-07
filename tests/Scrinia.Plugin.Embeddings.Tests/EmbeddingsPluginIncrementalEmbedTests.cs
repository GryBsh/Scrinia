using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Regression coverage for the incremental embed path (docs task B2): re-storing a memory with
/// fewer content elements must not leave stale higher-index chunk vectors behind, and appending
/// to a memory must re-embed the FULL current content (not just the appended fragment). Both
/// bugs share the same root cause: the plugin's write path previously upserted without first
/// removing prior vectors for the name, and its append handlers embedded only the fragment.
///
/// <para>Task 5 routed the plugin's write/reindex/remove paths through the shared
/// <see cref="MemoryVectorIndexer"/> (Task 3), which retired the whole-memory
/// <c>chunkIndex: null</c> "document vector" this plugin used to write in addition to its
/// per-element vectors: the memory-level score key is now computed at read time as the maximum
/// across a memory's window vectors (<see cref="VectorScoreProjection"/>). These tests were
/// rewritten to assert the invariant that vector: a memory ends up with one vector per content
/// element (short elements produce exactly one window each, per <c>TextChunker.SliceWindows</c>),
/// and re-indexing replaces rather than accumulates.</para>
///
/// This plugin has no Vulkan model available in the test environment (no GGUF at the expected
/// path), so tests use <see cref="EmbeddingsPlugin.InitializeForTests"/> — the internal seam that
/// performs the exact same field assignments <c>EnsureInitialized</c> does, in the same order —
/// to inject a deterministic provider and store instead of the real Vulkan one. This replaced
/// hand-rolled reflection that reimplemented (and could silently drift from) that construction
/// sequence.
/// </summary>
public sealed class EmbeddingsPluginIncrementalEmbedTests : IDisposable
{
    private static readonly string[] ExpectedAaaBbbCcc = new[] { "aaa", "bbb", "ccc" };
    private static readonly string[] ExpectedSingle = new[] { "single" };
    private static readonly string[] Expected0123456789 = new[] { "0123456789" };
    private static readonly string[] ExpectedDdd = new[] { "ddd" };

    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly string _vecDir;
    private readonly VectorStore _vectors;

    // Deterministic provider: vector encodes the input length so we can tell WHICH text was embedded.
    private sealed class LengthProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 1;
        public string Signature => "fake:len";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { text.Length });
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>(texts.Select(t => new float[] { t.Length }).ToArray());
        public void Dispose() { }
    }

    // Fake that always returns an EMPTY batch (0 vectors, regardless of input count) to exercise
    // the mismatch guard. IEmbeddingProvider.EmbedBatchAsync has a default interface implementation
    // that fans out to EmbedAsync 1:1, so it must be explicitly overridden here to force a
    // mismatched result. An empty result is the cleanest reproduction of the finding's "RemoveAsync
    // then zero vectors written" failure mode: with a partial-but-nonzero mismatch (e.g. 2 vectors
    // for 1 input) the old code's loop indexes past `items` and throws mid-loop, which — because
    // EmbedAndIndexAsync's try/catch swallows the exception — can leave a corrupted-but-nonzero
    // count that isn't a reliable regression signal. Zero vectors removes that ambiguity: the loop
    // body never executes, so without the guard the memory ends up with exactly zero vectors.
    private sealed class EmptyBatchProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 1;
        public string Signature => "fake:empty";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { text.Length });
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>(Array.Empty<float[]>());
        public void Dispose() { }
    }

    public EmbeddingsPluginIncrementalEmbedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_inc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
        _vecDir = Path.Combine(_root, "vec");
        Directory.CreateDirectory(_vecDir);
        _vectors = new VectorStore(_vecDir, expectedSignature: null);
    }

    public void Dispose()
    {
        _store.Dispose();
        _vectors.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private EmbeddingsPlugin BuildPlugin(IEmbeddingProvider? provider = null)
    {
        var plugin = new EmbeddingsPlugin();
        var activeProvider = provider ?? new LengthProvider();
        plugin.InitializeForTests(
            activeProvider, _vectors, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return plugin;
    }

    /// <summary>Swaps the plugin's provider in place, used mid-test to trigger a mismatch guard.</summary>
    private static void SwapProvider(EmbeddingsPlugin plugin, IEmbeddingProvider provider, VectorStore store)
    {
        plugin.InitializeForTests(
            provider, store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private void SeedArtifact(string scope, string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.FindArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task ReStore_WithFewerChunks_LeavesNoStaleChunkVectors()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        // v2 path syntax ("/memory/notes") is required: ParseQualifiedName rejects the legacy
        // "topic:subject" colon form ("local:memory/notes" is NOT valid input here — it throws
        // ArgumentException, which EmbedAndIndexAsync's catch-and-log swallows, silently indexing
        // nothing). "/memory/notes" parses to scope "local:memory", subject "notes".
        const string qn = "/memory/notes";

        // First store: 3 short content elements -> one window each -> 3 vectors, one per element
        // (chunk indices 1000, 2000, 3000 — elementIndex * ChunkIndexStride + windowOrdinal 0).
        await plugin.OnStoredAsync(MemoryAddress.Parse(qn), ExpectedAaaBbbCcc, _store, CancellationToken.None);
        _vectors.GetVectors(scope).Where(v => v.Name == "notes").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1000, 2000, 3000],
                because: "a memory must end up with one vector per content element");

        // Re-store: 1 element -> exactly one vector. Without Remove-first, the 3 old chunk
        // vectors would remain (count 4: the new one plus the 3 stale). With the fix: exactly 1 —
        // re-indexing replaces rather than accumulates.
        await plugin.OnStoredAsync(MemoryAddress.Parse(qn), ExpectedSingle, _store, CancellationToken.None);
        var after = _vectors.GetVectors(scope).Where(v => v.Name == "notes").ToList();
        after.Should().HaveCount(1);
        after[0].ChunkIndex.Should().Be(1000);
    }

    [Fact]
    public async Task Append_ReEmbedsFullMemory_NotJustTheFragment()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string subject = "log";
        const string qn = "/memory/log"; // parses to scope "local:memory", subject "log"

        SeedArtifact(scope, subject, "0123456789"); // length 10
        await plugin.OnStoredAsync(MemoryAddress.Parse(qn), Expected0123456789, _store, CancellationToken.None);

        // Append 5 chars on disk (simulating the store's append behavior), then fire the append
        // event with only the fragment — mirrors how OnAppendedAsync (MCP path) is invoked by the
        // real append path (content = just what was appended, not the full memory).
        string artifact = await _store.ReadArtifactAsync(MemoryAddress.Parse(qn), CancellationToken.None);
        string decoded = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks[0];
        string appendedFull = decoded + "ABCDE";
        SeedArtifact(scope, subject, appendedFull); // full content now length 15
        await plugin.OnAppendedAsync(MemoryAddress.Parse(qn), "ABCDE", _store, CancellationToken.None);

        // The memory has one content element, so exactly one window vector must survive — its
        // value must encode the DECODED full current content length (15), not the 5-char
        // fragment. LengthProvider makes this observable. There is no more null-chunk "document
        // vector": the memory-level score is now the max over window vectors, computed at read
        // time by VectorScoreProjection.
        var vectors = _vectors.GetVectors(scope).Where(v => v.Name == subject).ToList();
        vectors.Should().HaveCount(1);
        vectors[0].ChunkIndex.Should().Be(1000);
        vectors[0].Vector[0].Should().Be(appendedFull.Length);
    }

    [Fact]
    public async Task OnAfterAppend_ReEmbedsFullMemory_NotJustTheFragment()
    {
        // Drives the REST entry point (OnAfterAppendAsync) directly rather than the MCP one
        // (OnAppendedAsync tested above) — the two share ReembedFullMemoryAsync, but a test that
        // only exercises the shared helper does not prove this entry point is wired to it.
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string subject = "log2";
        const string qn = "/memory/log2";

        SeedArtifact(scope, subject, "0123456789"); // length 10
        await plugin.OnStoredAsync(MemoryAddress.Parse(qn), Expected0123456789, _store, CancellationToken.None);

        string artifact = await _store.ReadArtifactAsync(MemoryAddress.Parse(qn), CancellationToken.None);
        string decoded = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks[0];
        string appendedFull = decoded + "ABCDE";
        SeedArtifact(scope, subject, appendedFull); // full content now length 15

        var ctx = new AfterAppendContext
        {
            Store = _store,
            Address = MemoryAddress.Parse(qn),
            ChunkCount = 1,
            OriginalBytes = 5,
            Content = "ABCDE",
        };
        await plugin.OnAfterAppendAsync(ctx, CancellationToken.None);

        var vectors = _vectors.GetVectors(scope).Where(v => v.Name == subject).ToList();
        vectors.Should().HaveCount(1,
            because: "OnAfterAppendAsync must re-read and re-embed the full memory, not just the appended fragment");
        vectors[0].Vector[0].Should().Be(appendedFull.Length);
    }

    [Fact]
    public async Task Store_WithMismatchedBatchCount_PreservesExistingVectors()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string qn = "/memory/notes";

        // Store with the good deterministic provider first: 3 short elements -> 3 vectors, one
        // per element.
        await plugin.OnStoredAsync(MemoryAddress.Parse(qn), ExpectedAaaBbbCcc, _store, CancellationToken.None);
        int before = _vectors.GetVectors(scope).Count(v => v.Name == "notes");
        before.Should().Be(3);

        // Swap in a provider that always returns a mismatched (empty) vector batch. Without the
        // guard, EmbedAndIndexAsync would RemoveAsync the existing 3 vectors FIRST and then the
        // upsert loop would never execute (0 vectors to iterate), leaving the memory with ZERO
        // vectors. With the guard, the mismatch is detected before RemoveAsync runs, so the
        // original 3 vectors are untouched.
        SwapProvider(plugin, new EmptyBatchProvider(), _vectors);

        Func<Task> act = () => plugin.OnStoredAsync(MemoryAddress.Parse(qn), ExpectedDdd, _store, CancellationToken.None);
        await act.Should().NotThrowAsync();

        _vectors.GetVectors(scope).Count(v => v.Name == "notes").Should().Be(before,
            because: "a mismatched embed result must be rejected before RemoveAsync runs, so the existing vectors survive");
    }
}
