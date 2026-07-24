using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class MentionSpanExtractionTests
{
    private static string[] Spans(string content) =>
        AttributeExtractor.ExtractMentionSpans(content)?["mention"] ?? [];

    [Fact]
    public void ExtractsProperNouns_MultiWordKeptWhole_Lowercased()
    {
        var spans = Spans("Jolene bought Seraphim in New York last week.");
        spans.Should().BeEquivalentTo(["jolene", "seraphim", "new york"]);
    }

    [Fact]
    public void LeadingSubjectsKept_CommonOpenerWordsDropped()
    {
        // Sentence-leading SUBJECTS are kept (no positional drop).
        Spans("Jolene arrived early.").Should().BeEquivalentTo(["jolene"]);
        Spans("France is nice.").Should().BeEquivalentTo(["france"]);
        // Sentence-leading common OPENER words are stop-listed, so nothing survives here.
        Spans("Set the flag. Run the migration.").Should().BeEmpty();
    }

    [Fact]
    public void CodeFencesAndInlineCode_Excluded()
    {
        Spans("```\nSeraphim Config\n```\nWe met Alice.").Should().BeEquivalentTo(["alice"]);
        Spans("Set `MaxRetries` and call Bob.").Should().BeEquivalentTo(["bob"]);
    }

    [Fact]
    public void StopWords_NeverEmitted()
    {
        Spans("The Monday meeting. I called Bob.").Should().BeEquivalentTo(["bob"]);
    }

    [Fact]
    public void NoProperNouns_ReturnsNull()
    {
        AttributeExtractor.ExtractMentionSpans("just some plain prose here").Should().BeNull();
        AttributeExtractor.ExtractMentionSpans(null).Should().BeNull();
        AttributeExtractor.ExtractMentionSpans("   ").Should().BeNull();
    }

    [Fact]
    public void CapsPerMemory()
    {
        var many = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"We saw Name{i} there."));
        Spans(many).Length.Should().Be(AttributeExtractor.MaxMentionSpans);
    }
}
