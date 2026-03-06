using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Xunit;
using Xunit.Abstractions;

namespace Scrinia.Tests.Embeddings;

public class UnigramTokenizerTests(ITestOutputHelper output)
{
    private static readonly string MiniLmDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");

    private static bool IsAvailable => File.Exists(Path.Combine(MiniLmDir, "model.safetensors"))
                                     && File.Exists(Path.Combine(MiniLmDir, "vocab.txt"));

    [SkippableFact]
    public void LoadsVocab()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        var tok = UnigramTokenizer.FromVocabFile(Path.Combine(MiniLmDir, "vocab.txt"));
        tok.VocabSize.Should().Be(29524);
    }

    [SkippableFact]
    public void TokenizesSimpleText()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        var tok = UnigramTokenizer.FromVocabFile(Path.Combine(MiniLmDir, "vocab.txt"));
        var ids = tok.TokenizeRaw("hello world");
        ids.Count.Should().BeGreaterThan(0);
        output.WriteLine($"'hello world' -> {ids.Count} tokens: [{string.Join(", ", ids)}]");
    }

    [SkippableFact]
    public async Task Model2VecProvider_LoadsMiniLmDistillation()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        using var provider = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);
        provider.Dimensions.Should().Be(384);
        provider.IsAvailable.Should().BeTrue();

        var vec = await provider.EmbedAsync("hello world");
        vec.Should().NotBeNull();
        vec!.Length.Should().Be(384);

        // Should be L2 normalized
        float normSq = 0;
        foreach (float f in vec) normSq += f * f;
        MathF.Sqrt(normSq).Should().BeApproximately(1.0f, 0.01f);

        // Compare first 5 values against Python reference
        // Python: [-0.06778, 0.06367, 0.01368, 0.05210, -0.03108]
        output.WriteLine($"C# first 5:    [{string.Join(", ", vec.Take(5).Select(f => $"{f:F5}"))}]");
        output.WriteLine($"Python first 5: [-0.06778, 0.06367, 0.01368, 0.05210, -0.03108]");
    }

    [SkippableFact]
    public async Task SemanticSimilarity_WorksCorrectly()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        using var provider = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);

        var vecCat = await provider.EmbedAsync("the cat sat on the mat");
        var vecKitten = await provider.EmbedAsync("a kitten rested on a rug");
        var vecPhysics = await provider.EmbedAsync("quantum entanglement in particle physics");

        vecCat.Should().NotBeNull();
        vecKitten.Should().NotBeNull();
        vecPhysics.Should().NotBeNull();

        float simCatKitten = VectorIndex.CosineSimilarity(vecCat!, vecKitten!);
        float simCatPhysics = VectorIndex.CosineSimilarity(vecCat!, vecPhysics!);

        output.WriteLine($"cat vs kitten:  {simCatKitten:F3}");
        output.WriteLine($"cat vs physics: {simCatPhysics:F3}");

        simCatKitten.Should().BeGreaterThan(simCatPhysics,
            because: "cat/kitten should be more similar than cat/physics");
    }
}
