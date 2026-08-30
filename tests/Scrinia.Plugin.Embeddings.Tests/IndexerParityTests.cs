using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Finding H3. Installing the Vulkan plugin — the component marketed as better embeddings —
/// made semantic recall on long memories worse or absent, because the plugin's private copy of
/// embed-and-index never windowed. One input through both paths must now produce one layout.
///
/// <para>Side A drives the actual <see cref="EmbeddingsPlugin"/> entry point
/// (<see cref="EmbeddingsPlugin.OnStoredAsync"/>) via the <see cref="EmbeddingsPlugin.InitializeForTests"/>
/// seam; side B drives <see cref="MemoryVectorIndexer"/> directly. An earlier version of this
/// test drove BOTH sides through <see cref="MemoryVectorIndexer"/> directly, which cannot fail
/// regardless of what the plugin does — it was proving the indexer is deterministic against
/// itself, not that the plugin still delegates to it. See task-5-report.md, Finding 3, for the
/// before/after validation run.</para>
/// </summary>
public sealed class IndexerParityTests
{
    private sealed class StubProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:stub";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
        public void Dispose() { }
    }

    [Fact]
    public async Task PluginAndBuiltIn_ProduceTheSameChunkIndicesForTheSameInput()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        string[] content = ["short", new string('x', 400)];

        string root = Path.Combine(Path.GetTempPath(), "scrinia-parity-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root);

        var a = NewStore();
        var b = NewStore();

        // Side A: the plugin's real entry point. "/note" parses (FileMemoryStore.ParseQualifiedName)
        // to scope "local", subject "note" — matching side B's direct (scope, subject) call below.
        using var plugin = new EmbeddingsPlugin();
        plugin.InitializeForTests(new StubProvider(), a, NullLogger.Instance, options);
        await plugin.OnStoredAsync(MemoryAddress.Parse("/note"), content, store, default);

        // Side B: the shared indexer directly, over an independent store.
        await new MemoryVectorIndexer(new StubProvider(), b, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);

        try
        {
            a.GetVectors("local").Select(v => v.ChunkIndex)
                .Should().BeEquivalentTo(b.GetVectors("local").Select(v => v.ChunkIndex));
            a.GetVectors("local").Should().HaveCountGreaterThan(content.Length,
                because: "the long element must window rather than mean-pool");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static VectorStore NewStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VectorStore(dir);
    }
}
