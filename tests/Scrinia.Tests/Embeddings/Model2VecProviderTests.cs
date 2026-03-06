using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public class Model2VecProviderTests
{
    private static string? FindModelDir()
    {
        string exeDir = AppContext.BaseDirectory;
        // Try new MiniLM distillation first, then legacy potion-base-8M
        string miniLmDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
        if (Model2VecModelManager.IsModelAvailable(miniLmDir)) return miniLmDir;
        string legacyDir = Path.Combine(exeDir, "models", "potion-base-8M");
        return Model2VecModelManager.IsModelAvailable(legacyDir) ? legacyDir : null;
    }

    [SkippableFact]
    public async Task EmbedAsync_ReturnsCorrectDimensionVector()
    {
        string? modelDir = FindModelDir();
        Skip.If(modelDir is null, "Model2Vec model not downloaded (run 'scri setup')");

        using var provider = Model2VecProvider.Load(modelDir!, NullLogger.Instance);

        provider.IsAvailable.Should().BeTrue();
        provider.Dimensions.Should().BeOneOf(256, 384);

        var vec = await provider.EmbedAsync("hello world");
        vec.Should().NotBeNull();
        vec!.Length.Should().Be(provider.Dimensions);
    }

    [SkippableFact]
    public async Task EmbedAsync_OutputIsL2Normalized()
    {
        string? modelDir = FindModelDir();
        Skip.If(modelDir is null, "Model2Vec model not downloaded (run 'scri setup')");

        using var provider = Model2VecProvider.Load(modelDir!, NullLogger.Instance);

        var vec = await provider.EmbedAsync("test text for normalization");
        vec.Should().NotBeNull();

        // Compute L2 norm
        float normSq = 0;
        foreach (float f in vec!) normSq += f * f;
        float norm = MathF.Sqrt(normSq);

        norm.Should().BeApproximately(1.0f, 0.01f);
    }

    [SkippableFact]
    public async Task EmbedAsync_IsDeterministic()
    {
        string? modelDir = FindModelDir();
        Skip.If(modelDir is null, "Model2Vec model not downloaded (run 'scri setup')");

        using var provider = Model2VecProvider.Load(modelDir!, NullLogger.Instance);

        var vec1 = await provider.EmbedAsync("determinism test");
        var vec2 = await provider.EmbedAsync("determinism test");

        vec1.Should().NotBeNull();
        vec2.Should().NotBeNull();
        vec1.Should().BeEquivalentTo(vec2);
    }

    [SkippableFact]
    public async Task SimilarTexts_HaveHigherSimilarity()
    {
        string? modelDir = FindModelDir();
        Skip.If(modelDir is null, "Model2Vec model not downloaded (run 'scri setup')");

        using var provider = Model2VecProvider.Load(modelDir!, NullLogger.Instance);

        var vecCat = await provider.EmbedAsync("the cat sat on the mat");
        var vecKitten = await provider.EmbedAsync("a kitten rested on a rug");
        var vecPhysics = await provider.EmbedAsync("quantum entanglement in particle physics");

        vecCat.Should().NotBeNull();
        vecKitten.Should().NotBeNull();
        vecPhysics.Should().NotBeNull();

        float simCatKitten = VectorIndex.CosineSimilarity(vecCat!, vecKitten!);
        float simCatPhysics = VectorIndex.CosineSimilarity(vecCat!, vecPhysics!);

        simCatKitten.Should().BeGreaterThan(simCatPhysics,
            because: "cat/kitten should be more similar than cat/physics");
    }
}
