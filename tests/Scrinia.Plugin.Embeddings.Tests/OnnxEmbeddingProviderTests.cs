using FluentAssertions;
using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Embeddings.Onnx;
using Scrinia.Plugin.Embeddings.Providers;

namespace Scrinia.Plugin.Embeddings.Tests;

public class OnnxEmbeddingProviderTests
{
    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "scrinia-server", "models", "all-MiniLM-L6-v2");

    [SkippableFact]
    public async Task EmbedAsync_ReturnsVector()
    {
        Skip.IfNot(ModelManager.IsModelAvailable(ModelDir),
            "Model not downloaded. Run with model available to test.");

        using var logger = LoggerFactory.Create(b => b.AddConsole());
        using var provider = new OnnxEmbeddingProvider(ModelDir, HardwareAcceleration.Cpu, logger.CreateLogger<OnnxEmbeddingProviderTests>());

        var vec = await provider.EmbedAsync("The cat sat on the mat.");

        vec.Should().NotBeNull();
        vec!.Length.Should().Be(384);

        // Verify L2 normalized
        float norm = 0;
        foreach (float f in vec) norm += f * f;
        MathF.Sqrt(norm).Should().BeApproximately(1.0f, 0.01f);
    }

    [SkippableFact]
    public async Task SimilarTexts_HaveHighSimilarity()
    {
        Skip.IfNot(ModelManager.IsModelAvailable(ModelDir),
            "Model not downloaded. Run with model available to test.");

        using var logger = LoggerFactory.Create(b => b.AddConsole());
        using var provider = new OnnxEmbeddingProvider(ModelDir, HardwareAcceleration.Cpu, logger.CreateLogger<OnnxEmbeddingProviderTests>());

        var vec1 = await provider.EmbedAsync("The cat sat on the mat.");
        var vec2 = await provider.EmbedAsync("A feline rested on the rug.");
        var vec3 = await provider.EmbedAsync("Quantum physics explains particle behavior.");

        vec1.Should().NotBeNull();
        vec2.Should().NotBeNull();
        vec3.Should().NotBeNull();

        float simSimilar = VectorIndex.CosineSimilarity(vec1!, vec2!);
        float simDifferent = VectorIndex.CosineSimilarity(vec1!, vec3!);

        simSimilar.Should().BeGreaterThan(simDifferent,
            "similar sentences should have higher cosine similarity than unrelated ones");
    }
}
