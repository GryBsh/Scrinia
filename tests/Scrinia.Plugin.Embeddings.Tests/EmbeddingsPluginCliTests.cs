using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Plugin.Embeddings;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Tests for the child-process plugin protocol DTOs and the embeddings types
/// used by <c>Scrinia.Plugin.Embeddings.Cli</c>.
///
/// These tests validate that the core types (EmbeddingOptions, EmbeddingProviderFactory,
/// VectorStore) still work correctly now that the CLI entry point is a child process
/// rather than an in-process plugin.
/// </summary>
public class EmbeddingsPluginCliTests
{
    [Fact]
    public void EmbeddingOptions_HasExpectedDefaults()
    {
        var options = new EmbeddingOptions();
        options.Provider.Should().Be("onnx");
        options.Hardware.Should().Be("auto");
        options.SemanticWeight.Should().Be(50.0);
    }

    [Fact]
    public void EmbeddingProviderFactory_NoneProvider_ReturnsNullProvider()
    {
        var options = new EmbeddingOptions { Provider = "none" };
        var provider = EmbeddingProviderFactory.Create(
            options, Path.GetTempPath(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        provider.Should().BeOfType<NullEmbeddingProvider>();
        provider.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingProviderFactory_UnknownProvider_ReturnsNullProvider()
    {
        var options = new EmbeddingOptions { Provider = "nonexistent" };
        var provider = EmbeddingProviderFactory.Create(
            options, Path.GetTempPath(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        provider.Should().BeOfType<NullEmbeddingProvider>();
    }

    [Fact]
    public async Task NullEmbeddingProvider_EmbedAsync_ReturnsNull()
    {
        var provider = new NullEmbeddingProvider();
        var result = await provider.EmbedAsync("test");
        result.Should().BeNull();
    }

    [Fact]
    public async Task NullEmbeddingProvider_EmbedBatchAsync_ReturnsNull()
    {
        var provider = new NullEmbeddingProvider();
        var result = await provider.EmbedBatchAsync(["test1", "test2"]);
        result.Should().BeNull();
    }

    [Fact]
    public void VectorStore_CanBeCreatedWithTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new VectorStore(tempDir);
            store.TotalVectorCount().Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
