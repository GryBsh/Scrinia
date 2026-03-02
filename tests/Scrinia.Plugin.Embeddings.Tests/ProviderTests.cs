using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Plugin.Embeddings.Providers;

namespace Scrinia.Plugin.Embeddings.Tests;

public class ProviderTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ── Voyage AI ───────────────────────────────────────────────────────────

    [Fact]
    public void VoyageAi_ThrowsWithoutApiKey()
    {
        var act = () => new VoyageAiEmbeddingProvider(null, "voyage-3.5", "https://api.voyageai.com/v1", Logger);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void VoyageAi_DefaultDimensions()
    {
        using var provider = new VoyageAiEmbeddingProvider("test-key", "voyage-3.5", "https://api.voyageai.com/v1", Logger);
        provider.Dimensions.Should().Be(1024);
        provider.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void VoyageAi_FactoryCreatesCorrectType()
    {
        var options = new EmbeddingOptions { Provider = "voyageai", VoyageAiApiKey = "test-key" };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<VoyageAiEmbeddingProvider>();
    }

    [Fact]
    public void VoyageAi_FactoryFallsBackWithoutKey()
    {
        var options = new EmbeddingOptions { Provider = "voyageai" };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<NullEmbeddingProvider>();
    }

    [Fact]
    public void VoyageAi_OptionsDefaults()
    {
        var options = new EmbeddingOptions();
        options.VoyageAiModel.Should().Be("voyage-3.5");
        options.VoyageAiBaseUrl.Should().Be("https://api.voyageai.com/v1");
        options.VoyageAiApiKey.Should().BeNull();
    }

    // ── Azure AI Foundry ────────────────────────────────────────────────────

    [Fact]
    public void Azure_ThrowsWithoutEndpoint()
    {
        var act = () => new AzureAiEmbeddingProvider(null, "key", "dep", "model", "2024-10-21", false, Logger);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Azure_ThrowsWithoutApiKey()
    {
        var act = () => new AzureAiEmbeddingProvider("https://test.openai.azure.com", null, "dep", "model", "2024-10-21", false, Logger);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Azure_DefaultDimensions()
    {
        using var provider = new AzureAiEmbeddingProvider(
            "https://test.openai.azure.com", "test-key", "dep", "model", "2024-10-21", false, Logger);
        provider.Dimensions.Should().Be(1536);
        provider.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Azure_FactoryCreatesCorrectType()
    {
        var options = new EmbeddingOptions
        {
            Provider = "azure",
            AzureEndpoint = "https://test.openai.azure.com",
            AzureApiKey = "test-key"
        };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<AzureAiEmbeddingProvider>();
    }

    [Fact]
    public void Azure_FactoryFallsBackWithoutConfig()
    {
        var options = new EmbeddingOptions { Provider = "azure" };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<NullEmbeddingProvider>();
    }

    [Fact]
    public void Azure_OptionsDefaults()
    {
        var options = new EmbeddingOptions();
        options.AzureDeployment.Should().Be("text-embedding-3-small");
        options.AzureModel.Should().Be("text-embedding-3-small");
        options.AzureApiVersion.Should().Be("2024-10-21");
        options.AzureUseV1.Should().BeFalse();
        options.AzureEndpoint.Should().BeNull();
        options.AzureApiKey.Should().BeNull();
    }

    // ── Google Gemini ───────────────────────────────────────────────────────

    [Fact]
    public void Google_ThrowsWithoutApiKey()
    {
        var act = () => new GoogleGeminiEmbeddingProvider(null, "gemini-embedding-001", "https://generativelanguage.googleapis.com", 0, Logger);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Google_DefaultDimensions()
    {
        using var provider = new GoogleGeminiEmbeddingProvider(
            "test-key", "gemini-embedding-001", "https://generativelanguage.googleapis.com", 0, Logger);
        provider.Dimensions.Should().Be(3072);
        provider.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Google_ConfiguredDimensions()
    {
        using var provider = new GoogleGeminiEmbeddingProvider(
            "test-key", "gemini-embedding-001", "https://generativelanguage.googleapis.com", 768, Logger);
        provider.Dimensions.Should().Be(768);
    }

    [Fact]
    public void Google_FactoryCreatesCorrectType()
    {
        var options = new EmbeddingOptions { Provider = "google", GoogleApiKey = "test-key" };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<GoogleGeminiEmbeddingProvider>();
    }

    [Fact]
    public void Google_FactoryFallsBackWithoutKey()
    {
        var options = new EmbeddingOptions { Provider = "google" };
        var provider = EmbeddingProviderFactory.Create(options, Path.GetTempPath(), Logger);
        provider.Should().BeOfType<NullEmbeddingProvider>();
    }

    [Fact]
    public void Google_OptionsDefaults()
    {
        var options = new EmbeddingOptions();
        options.GoogleModel.Should().Be("gemini-embedding-001");
        options.GoogleBaseUrl.Should().Be("https://generativelanguage.googleapis.com");
        options.GoogleDimensions.Should().Be(0);
        options.GoogleApiKey.Should().BeNull();
    }
}
