using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for the append bug fixed in Task 4: <c>OnAppendedAsync</c> used to embed
/// only the appended fragment (<c>[content]</c>), and since <see cref="MemoryVectorIndexer.IndexAsync"/>
/// replaces a memory's vectors atomically, that deleted the memory's existing embeddings and
/// left only vectors for the fragment. The brief's prescribed test called
/// <c>IndexAsync</c> twice directly and never touched an append path at all, so it would still
/// pass with the fix reverted — this drives the actual handler method against a real
/// <see cref="FileMemoryStore"/>-backed artifact.
/// </summary>
public sealed class CoreEmbeddingEventHandlerAppendTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public CoreEmbeddingEventHandlerAppendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_core_append_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task OnAppendedAsync_IndexesTheFullOnDiskContent_NotJustTheAppendedFragment()
    {
        const string scope = "local";
        const string name = "note";

        // A two-element memory, the way a store() with a multi-element content array lands on
        // disk (Nmp2ChunkedEncoder.EncodeChunks with >1 element — matches how CompactEntryToSingleChunk
        // and the other reindex-path tests seed a real multi-chunk artifact).
        string[] content = ["first element", "second element"];
        var entry = new ArtifactEntry(name, "", content.Sum(s => s.Length), content.Length, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.EncodeChunks(content));

        string embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(embeddingsDir);
        using var vectorStore = new VectorStore(embeddingsDir, expectedSignature: null);
        var handler = new CoreEmbeddingEventHandler(new FakeProvider(), vectorStore, NullLogger.Instance);

        string qualifiedName = _store.FormatQualifiedName(scope, name);

        // Only the appended fragment is passed in — exactly what the real append call site
        // does. The fix must ignore this parameter's shape and re-read the memory's actual,
        // full on-disk content instead.
        await handler.OnAppendedAsync(qualifiedName, "second element", _store, CancellationToken.None);

        vectorStore.GetVectors(scope)
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().BeEquivalentTo([1, 2],
                because: "indexing only the appended fragment would produce vectors for element 1 "
                       + "only ([fragment] has length 1) — element 2 would have none at all, because "
                       + "the indexer replaces a memory's vectors atomically");
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
