using FluentAssertions;
using Scrinia.Plugin.Embeddings.Onnx;

namespace Scrinia.Plugin.Embeddings.Tests;

public class BertTokenizerTests
{
    [SkippableFact]
    public void FromVocabFile_LoadsTokens()
    {
        string vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);

        // Should be able to encode a simple sentence
        var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode("hello world");

        inputIds.Should().NotBeEmpty();
        inputIds[0].Should().Be(101); // [CLS]
        inputIds[^1].Should().Be(102); // [SEP]
        attentionMask.All(m => m == 1).Should().BeTrue();
        tokenTypeIds.All(t => t == 0).Should().BeTrue();
    }

    [SkippableFact]
    public void Encode_TruncatesToMaxLength()
    {
        string vocabPath = FindVocabFile();
        Skip.If(vocabPath is null, "vocab.txt not available (model not downloaded)");

        var tokenizer = BertTokenizer.FromVocabFile(vocabPath!);

        // Generate a very long text
        string longText = string.Join(" ", Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 100));

        var (inputIds, _, _) = tokenizer.Encode(longText, maxLength: 64);
        inputIds.Length.Should().BeLessOrEqualTo(64);
    }

    private static string? FindVocabFile()
    {
        string modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "scrinia-server", "models", "all-MiniLM-L6-v2");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");
        return File.Exists(vocabPath) ? vocabPath : null;
    }
}
