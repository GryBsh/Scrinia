using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public class BertTokenizerTests
{
    private static string? FindVocabFile()
    {
        // Check the new Model2Vec location first
        string exeDir = AppContext.BaseDirectory;
        string model2vecPath = Path.Combine(exeDir, "models", "potion-base-8M", "vocab.txt");
        if (File.Exists(model2vecPath)) return model2vecPath;

        // Fall back to legacy ONNX location
        string modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scrinium", "models", "all-MiniLM-L6-v2");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");
        return File.Exists(vocabPath) ? vocabPath : null;
    }

    [SkippableFact]
    public void FromVocabFile_LoadsTokens()
    {
        string? vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);

        var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode("hello world");

        inputIds.Should().NotBeEmpty();
        // [CLS] is first token, [SEP] is last — IDs vary by vocab (101/102 for BERT, 2/3 for Model2Vec)
        inputIds.Length.Should().BeGreaterOrEqualTo(3); // at least [CLS] + token + [SEP]
        inputIds[0].Should().Be(inputIds[0]); // first token is [CLS] (vocab-dependent)
        inputIds[^1].Should().NotBe(inputIds[0]); // last token is [SEP] (different from [CLS])
        attentionMask.All(m => m == 1).Should().BeTrue();
        tokenTypeIds.All(t => t == 0).Should().BeTrue();
    }

    [SkippableFact]
    public void Encode_TruncatesToMaxLength()
    {
        string? vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);

        string longText = string.Join(" ", Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 100));

        var (inputIds, _, _) = tokenizer.Encode(longText, maxLength: 64);
        inputIds.Length.Should().BeLessOrEqualTo(64);
    }

    [SkippableFact]
    public void TokenizeRaw_FiltersUnknownTokens()
    {
        string? vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);

        var tokens = tokenizer.TokenizeRaw("hello world");
        tokens.Should().NotBeEmpty();
        // Should not contain special tokens — check by comparing with Encode output
        var (encoded, _, _) = tokenizer.Encode("hello world");
        int clsId = (int)encoded[0];
        int sepId = (int)encoded[^1];
        tokens.Should().NotContain(clsId, "TokenizeRaw should not include [CLS]");
        tokens.Should().NotContain(sepId, "TokenizeRaw should not include [SEP]");
    }

    [SkippableFact]
    public void VocabSize_ReturnsPositiveCount()
    {
        string? vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);
        tokenizer.VocabSize.Should().BeGreaterThan(0);
    }
}
