using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for Task 24a: <c>consolidate</c>'s chunk-merge step must re-embed the
/// compacted entry through the same <see cref="IMemoryEventSink"/> path a normal store/append
/// uses, so vectors keyed to the pre-compaction (multi-chunk) layout don't survive as orphans.
///
/// Exercises <see cref="ScriniaCommands.CompactEntryToSingleChunk"/> directly (it is <c>internal</c>
/// rather than <c>private</c> specifically to allow this) instead of going through the public
/// <c>Consolidate</c> command, because <c>Consolidate</c> calls <c>WorkspaceSetup.LoadPluginsAsync</c>,
/// whose provider discovery depends on whether embedding model files happen to be present on the
/// machine running the test — not deterministic across dev/CI environments. Testing the compaction
/// routine directly with a real <see cref="CoreEmbeddingEventHandler"/> wired to a fake, always-available
/// <see cref="IEmbeddingProvider"/> gives the same Remove-then-Upsert coverage deterministically.
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ConsolidateReembedTests : IDisposable
{
    private readonly string _root;
    private readonly string _embeddingsDir;

    public ConsolidateReembedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_consolidate_reembed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(_embeddingsDir);
    }

    public void Dispose()
    {
        // MemoryEventSinkContext.Default is a plain process-wide static, not AsyncLocal, and
        // StoreScope does not reset it. Leaving the handler installed points every later test
        // in this process at this test's VectorStore, which is disposed when the test method
        // exits.
        MemoryEventSinkContext.Default = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CompactEntryToSingleChunk_ReembedsThroughSink_NoOrphanedChunkVectors()
    {
        WorkspaceSetup.Configure(_root);
        var store = MemoryStoreContext.Current!;

        const string name = "long-session";
        // Bare "sessions" (no "memory/" namespace prefix) round-trips cleanly through
        // FileMemoryStore's FormatQualifiedName -> ParseQualifiedName pair. A "memory/X"
        // v1-namespaced scope does NOT round-trip (Format collapses it to "/X/subject",
        // and Parse reconstructs that back to bare "local:X", losing the "memory/"
        // prefix) — that's a pre-existing FileMemoryStore quirk unrelated to this fix, so
        // this test avoids it to isolate what's actually under test: the re-embed wiring.
        const string scope = "local:sessions";

        // Seed a genuine multi-chunk artifact on disk (EncodeChunks with >1 element — Encode()
        // alone always produces a single chunk regardless of content length).
        string[] chunkTexts = ["first chunk body", "second chunk body", "third chunk body", "fourth chunk body"];
        string fullText = string.Join(" ", chunkTexts);
        var entry = new ArtifactEntry(
            Name: name, Uri: "", OriginalBytes: fullText.Length, ChunkCount: chunkTexts.Length,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-10), UpdatedAt: DateTimeOffset.UtcNow.AddDays(-10));
        store.Upsert(entry, scope);
        string path = store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.EncodeChunks(chunkTexts));
        Nmp2ChunkedEncoder.GetChunkCount(File.ReadAllText(path)).Should().Be(4);

        // Pre-seed "stale" vectors at chunk indices matching the OLD (pre-compaction) layout.
        // After compaction these must be GONE, not merely joined by a new index-0 vector.
        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        for (int i = 0; i < chunkTexts.Length; i++)
            await vectorStore.UpsertAsync(scope, name, i, [1f, 2f, 3f]);
        vectorStore.GetVectors(scope).Should().HaveCount(4);

        // Wire the real event handler used by WorkspaceSetup for a normal store/append call,
        // backed by a fake always-available provider so the test doesn't depend on real model
        // files being present.
        var provider = new FakeProvider();
        MemoryEventSinkContext.Default = new CoreEmbeddingEventHandler(provider, vectorStore, NullLogger.Instance);

        var item = new ScopedArtifact(scope, entry);

        // Act
        await ScriniaCommands.CompactEntryToSingleChunk(item, store, CancellationToken.None);

        // Assert: exactly one vector remains, at chunk index 0 — the stale indices 1-3 are gone.
        var vectors = vectorStore.GetVectors(scope);
        vectors.Should().HaveCount(1, because: "compaction merges to a single chunk; the old chunk-indexed vectors must not survive as orphans");
        vectors[0].Name.Should().Be(name);
        vectors[0].ChunkIndex.Should().Be(0);
    }

    private sealed class FakeProvider : IEmbeddingProvider
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

/// <summary>
/// Serializes tests that mutate <c>WorkspaceSetup</c>/<c>ScriniaArtifactStore</c>'s truly-global
/// (non-<see cref="AsyncLocal{T}"/>) static config — specifically <c>ScriniaArtifactStore</c>'s
/// configured workspace root, set via <c>WorkspaceSetup.Configure</c> (directly, or transitively
/// through <see cref="ScriniaCommands"/> entry points like <c>Consolidate</c>/<c>MigrateV1</c>/
/// <c>Migrate</c>). Unlike <c>TestHelpers.StoreScope</c> (which uses AsyncLocal overrides and is
/// therefore already safe under parallel execution), this state is a plain static field guarded
/// only by a lock for writes — concurrent tests on different threads can clobber each other's
/// configured root. See Task G code-review followups.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalWorkspaceConfigCollection
{
    public const string Name = "GlobalWorkspaceConfig";
}
