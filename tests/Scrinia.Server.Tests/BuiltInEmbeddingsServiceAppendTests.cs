using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Regression coverage for the append bug fixed alongside Task 4's consolidation: appending to
/// a memory previously embedded only the appended fragment, and since
/// <see cref="MemoryVectorIndexer.IndexAsync"/> replaces a memory's vectors atomically, that
/// deleted the memory's existing embeddings and left only vectors for the fragment.
///
/// <para><see cref="BuiltInEmbeddingsService"/> has two separate entry points into the fix —
/// the MCP <c>OnAppendedAsync</c> and the REST <c>OnAfterAppendAsync</c> — which share one
/// private <c>ReembedFullMemoryAsync</c> helper. Only a test that drives each entry point
/// independently proves both are actually wired to it; a test of the shared helper alone would
/// not catch one call site being left on the old <c>[content]</c> shape.</para>
/// </summary>
public sealed class BuiltInEmbeddingsServiceAppendTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly string _embeddingsDir;

    public BuiltInEmbeddingsServiceAppendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_builtin_append_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
        _embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(_embeddingsDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void SeedTwoElementMemory(string scope, string name, string[] content)
    {
        var entry = new ArtifactEntry(name, "", content.Sum(s => s.Length), content.Length, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.EncodeChunks(content));
    }

    [Fact]
    public async Task OnAppendedAsync_McpPath_IndexesTheFullOnDiskContent_NotJustTheFragment()
    {
        const string scope = "local";
        const string name = "note";
        SeedTwoElementMemory(scope, name, ["first element", "second element"]);

        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var service = new BuiltInEmbeddingsService(
            new FakeProvider(), vectorStore, semanticWeight: 50.0, NullLogger<BuiltInEmbeddingsService>.Instance);

        string qualifiedName = _store.FormatQualifiedName(scope, name);

        // Only the appended fragment, exactly as the real MCP append call site passes it.
        await service.OnAppendedAsync(qualifiedName, "second element", _store, CancellationToken.None);

        vectorStore.GetVectors(scope)
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().BeEquivalentTo([1, 2],
                because: "the MCP append path must re-read and index the memory's real full "
                       + "content, not just the fragment passed to OnAppendedAsync");
    }

    [Fact]
    public async Task OnAfterAppendAsync_RestPath_IndexesTheFullOnDiskContent_NotJustTheFragment()
    {
        const string scope = "local";
        const string name = "note";
        SeedTwoElementMemory(scope, name, ["first element", "second element"]);

        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var service = new BuiltInEmbeddingsService(
            new FakeProvider(), vectorStore, semanticWeight: 50.0, NullLogger<BuiltInEmbeddingsService>.Instance);

        string qualifiedName = _store.FormatQualifiedName(scope, name);
        var ctx = new AfterAppendContext
        {
            Store = _store,
            Name = qualifiedName,
            ChunkCount = 2,
            OriginalBytes = 0,
            Content = "second element",
        };

        await service.OnAfterAppendAsync(ctx, CancellationToken.None);

        vectorStore.GetVectors(scope)
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().BeEquivalentTo([1, 2],
                because: "the REST append path must re-read and index the memory's real full "
                       + "content too, proving OnAfterAppendAsync is wired to the same "
                       + "ReembedFullMemoryAsync helper as the MCP path rather than left on the "
                       + "old fragment-only shape");
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
