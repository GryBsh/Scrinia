using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public sealed class ChunkedSignatureForProviderTests
{
    private sealed class FakeProvider(bool available, string signature) : IEmbeddingProvider
    {
        public bool IsAvailable => available;
        public int Dimensions => 3;
        public string Signature => signature;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { 1, 0, 0 });
        public void Dispose() { }
    }

    [Fact]
    public void ForProvider_Available_MatchesReindexerComposition()
    {
        var provider = new FakeProvider(available: true, signature: "vulkan:m2v-MiniLM-L6-v2");
        var options = new EmbeddingOptions(); // defaults, same as the reindexer's `options ??= new()`

        string? sig = ChunkedSignature.ForProvider(provider, options);

        sig.Should().Be(ChunkedSignature.Compose(
            "vulkan:m2v-MiniLM-L6-v2", options.ChunkSize, options.ChunkOverlap));
    }

    [Fact]
    public void ForProvider_Unavailable_ReturnsNull_SoQuarantineIsDisabled()
    {
        var provider = new FakeProvider(available: false, signature: "none");

        ChunkedSignature.ForProvider(provider, new EmbeddingOptions()).Should().BeNull();
    }
}
