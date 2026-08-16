using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Finding H3. Installing the Vulkan plugin — the component marketed as better embeddings —
/// made semantic recall on long memories worse or absent, because the plugin's private copy of
/// embed-and-index never windowed. One input through both paths must now produce one layout.
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

        var a = NewStore();
        var b = NewStore();
        await new MemoryVectorIndexer(new StubProvider(), a, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);
        await new MemoryVectorIndexer(new StubProvider(), b, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);

        a.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo(b.GetVectors("local").Select(v => v.ChunkIndex));
        a.GetVectors("local").Should().HaveCountGreaterThan(content.Length,
            because: "the long element must window rather than mean-pool");
    }

    private static VectorStore NewStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VectorStore(dir);
    }
}
