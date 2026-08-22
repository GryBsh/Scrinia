using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Plugin.Embeddings;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class VulkanEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var provider = VulkanEmbeddingProvider.ForTesting(
            (_, ct) => Task.FromException<float[]>(new OperationCanceledException(ct)),
            dimensions: 1,
            NullLogger.Instance);

        var act = () => provider.EmbedAsync("text", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
