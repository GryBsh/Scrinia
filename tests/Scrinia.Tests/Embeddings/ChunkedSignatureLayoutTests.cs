using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// The element/window layout changed what a stored chunk index means, so vectors written
/// before it must not be read as though they carry element indices. The signature carries a
/// layout marker; VectorStore's existing mismatch path quarantines and reindex rebuilds —
/// the same flow that already handles a model switch or a chunk-config change.
/// </summary>
public sealed class ChunkedSignatureLayoutTests
{
    [Fact]
    public void Compose_CarriesTheLayoutMarker()
    {
        ChunkedSignature.Compose("model2vec:potion-base-8M", 1200, 200)
            .Should().Be("model2vec:potion-base-8M|c1200o200|e1");
    }

    [Fact]
    public void Compose_DiffersFromThePreBranchSignature()
    {
        ChunkedSignature.Compose("p", 1200, 200)
            .Should().NotBe("p|c1200o200",
                because: "identical signatures would leave window-indexed vectors in place and "
                       + "the element key would silently never resolve for them");
    }

    [Fact]
    public void ForProvider_UsesTheSuppliedOptions_NotDefaults()
    {
        var provider = new StubAvailableProvider();
        var options = new EmbeddingOptions { ChunkSize = 400, ChunkOverlap = 40 };

        ChunkedSignature.ForProvider(provider, options)
            .Should().Contain("c400o40",
                because: "EmbeddingsPlugin.cs:118 passed a default-constructed EmbeddingOptions, "
                       + "so configured chunk settings never reached the signature");
    }

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
}
