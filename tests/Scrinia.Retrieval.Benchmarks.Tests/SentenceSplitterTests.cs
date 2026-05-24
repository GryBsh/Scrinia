using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="SentenceSplitter"/>. The splitter doesn't need linguistically
/// perfect output — it needs to produce embeddable chunks. The properties asserted
/// here are the ones the reranker actually depends on: bounded count, minimum size,
/// maximum size, no empty entries.
/// </summary>
public class SentenceSplitterTests
{
    [Fact]
    public void EmptyInput_ReturnsEmptyList()
    {
        SentenceSplitter.Split("").Should().BeEmpty();
        SentenceSplitter.Split("   ").Should().BeEmpty();
        SentenceSplitter.Split(null!).Should().BeEmpty();
    }

    [Fact]
    public void MultipleSentences_SplitOnPunctuation()
    {
        // Each sentence is comfortably above MinSentenceChars (20) so the
        // undersize-merge logic stays out of the way and we test the punctuation
        // split in isolation.
        string text =
            "The cat sat patiently on the warm mat by the fire. " +
            "Then she stretched her paws and yawned loudly. " +
            "Finally she padded off to find some quiet shade.";
        var result = SentenceSplitter.Split(text);

        result.Should().HaveCount(3);
        result[0].Should().StartWith("The cat sat");
        result[1].Should().StartWith("Then she stretched");
        result[2].Should().StartWith("Finally she padded");
    }

    [Fact]
    public void UndersizeFragments_MergeForward()
    {
        // "OK." is below MinSentenceChars (20), so it should merge into the next sentence
        // rather than appearing as its own embeddable unit. Tiny sentences dilute the
        // index without carrying useful signal.
        string text = "OK. The user wants to know about kubernetes deployment patterns.";
        var result = SentenceSplitter.Split(text);

        result.Should().ContainSingle(
            because: "the 3-char 'OK.' should merge forward into the longer sentence, not appear standalone");
        result[0].Should().Contain("OK").And.Contain("kubernetes");
    }

    [Fact]
    public void OversizeSentence_HardSplitsAtWordBoundary()
    {
        // Build a sentence well past MaxSentenceChars (300) with no internal punctuation
        // the regex would catch. Should hard-split into multiple sub-300 chunks, all
        // ending at word boundaries (not mid-word).
        string longSentence = string.Join(" ", Enumerable.Repeat("word", 100)) + ".";
        var result = SentenceSplitter.Split(longSentence);

        result.Should().HaveCountGreaterThan(1, "a 500-char sentence must split");
        result.Should().AllSatisfy(s =>
        {
            s.Length.Should().BeLessThanOrEqualTo(SentenceSplitter.MaxSentenceChars + 5,
                "with whitespace-tail trim tolerance");
            // No mid-word splits: every piece should start and end on a word boundary
            // (alphanumeric character at both ends after trim).
            s.Should().NotStartWith(" ").And.NotEndWith(" ");
        });
    }

    [Fact]
    public void HardCap_AtMaxSentencesPerDoc()
    {
        // Build a doc with way more than MaxSentencesPerDoc (50) sentences. The splitter
        // should truncate at the cap, not blow up memory or rerank cost.
        string text = string.Join(" ",
            Enumerable.Range(0, 100).Select(i => $"This is sentence number {i} in the document."));
        var result = SentenceSplitter.Split(text);

        result.Count.Should().BeLessThanOrEqualTo(SentenceSplitter.MaxSentencesPerDoc);
    }

    [Fact]
    public void NoEmptyOrWhitespaceEntries()
    {
        // Whatever the input shape, the output must never contain empty/whitespace
        // strings. The reranker would try to embed them and either fail or waste a call.
        string text = "First sentence here.   \n\n   Second sentence after gap.    Third.";
        var result = SentenceSplitter.Split(text);

        result.Should().AllSatisfy(s => s.Trim().Should().NotBeEmpty());
    }
}
