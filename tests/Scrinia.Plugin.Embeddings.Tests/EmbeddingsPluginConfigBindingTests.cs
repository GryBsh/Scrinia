using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// <c>EmbeddingsPlugin.ConfigureServices</c> used to read only <c>SemanticWeight</c> out of
/// <c>Scrinia:Embeddings</c>, so a configured <c>ChunkSize</c> / <c>ChunkOverlap</c> never
/// reached the <see cref="ChunkedSignature"/> the vector store is signed with, nor the
/// <see cref="MemoryVectorIndexer"/> the write path uses. Binding the whole section is the fix;
/// these tests fail against the un-bound version.
/// </summary>
public class EmbeddingsPluginConfigBindingTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private sealed class StubAvailableProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "stub";
        public Task<float[]?> EmbedAsync(string t, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> t, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([[1f, 0f]]);
        public void Dispose() { }
    }

    [Fact]
    public void ConfigureServices_BindsTheWholeSection_NotJustSemanticWeight()
    {
        using var plugin = new EmbeddingsPlugin();

        plugin.ConfigureServices(new ServiceCollection(), Config(
            ("Scrinia:Embeddings:SemanticWeight", "37"),
            ("Scrinia:Embeddings:ChunkSize", "400"),
            ("Scrinia:Embeddings:ChunkOverlap", "40"),
            ("Scrinia:Embeddings:MaxChunksPerMemory", "250")));

        var bound = plugin.BoundOptionsForTests;

        using var scope = new FluentAssertions.Execution.AssertionScope();
        bound.SemanticWeight.Should().Be(37);
        bound.ChunkSize.Should().Be(400);
        bound.ChunkOverlap.Should().Be(40);
        bound.MaxChunksPerMemory.Should().Be(250);
    }

    [Fact]
    public void TheBoundOptions_ReachTheSignatureTheVectorStoreIsSignedWith()
    {
        using var plugin = new EmbeddingsPlugin();
        plugin.ConfigureServices(new ServiceCollection(), Config(
            ("Scrinia:Embeddings:ChunkSize", "400"),
            ("Scrinia:Embeddings:ChunkOverlap", "40")));

        // EnsureInitialized composes exactly this. Asserting on the composition rather than on a
        // constructed store because the test environment has no GGUF model, so the real provider
        // is unavailable and ForProvider correctly returns null there.
        ChunkedSignature.ForProvider(new StubAvailableProvider(), plugin.BoundOptionsForTests)
            .Should().Contain("c400o40",
                because: "a default-constructed EmbeddingOptions here meant configured chunk "
                       + "settings never affected which vectors the store considered valid");
    }

    [Fact]
    public void AChunkCapAboveTheStride_IsClamped_NotLeftToThrowInsideEnsureInitialized()
    {
        using var plugin = new EmbeddingsPlugin();

        plugin.ConfigureServices(new ServiceCollection(), Config(
            ("Scrinia:Embeddings:MaxChunksPerMemory", "5000")));

        plugin.BoundOptionsForTests.MaxChunksPerMemory
            .Should().Be(MemoryVectorIndexer.ChunkIndexStride - 1,
                because: "an out-of-range cap would otherwise throw from MemoryVectorIndexer's "
                       + "constructor inside EnsureInitialized — after the GPU provider was "
                       + "created but before the init sentinel latches, so every retry leaks "
                       + "another GPU context");
    }

    [Fact]
    public void AMalformedValue_FallsBackToDefaults_RatherThanCrashingStartup()
    {
        using var plugin = new EmbeddingsPlugin();

        var act = () => plugin.ConfigureServices(new ServiceCollection(), Config(
            ("Scrinia:Embeddings:SemanticWeight", "not-a-number")));

        act.Should().NotThrow();
        plugin.BoundOptionsForTests.ChunkSize.Should().Be(new EmbeddingOptions().ChunkSize);
    }
}
