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
    public void CodeFences_Excluded_InlineCode_ReadAsProse()
    {
        // Fenced blocks are still excluded — the one exclusion rule ProseScanner enforces.
        Spans("```\nSeraphim Config\n```\nWe met Alice.").Should().BeEquivalentTo(["alice"]);
        // Inline code is prose, not stripped: `MaxRetries` is its own run, separate from "Set"
        // (a stopword) and "Bob" (its own run, unaffected by the backtick pair before it).
        Spans("Set `MaxRetries` and call Bob.").Should().BeEquivalentTo(["maxretries", "bob"]);
        // A backtick pair breaks a mention run on BOTH sides — the case above can't tell that
        // apart from "the backtick's contents are simply invisible", because "Set" is a stopword
        // (nothing to its left to fuse with) and "Bob" is whitespace-bounded on the far side.
        // Here real capitalized runs sit on both sides of the backticks: under the old strip this
        // was one fused span "alpha beta" (StripInlineCode blanks the backticks and "Xray" to
        // spaces, and whitespace never breaks a run); under a hypothetical "backtick as whitespace"
        // reading it would also fuse, into "alpha xray beta". Only "backtick is a run-breaking
        // character" produces three separate spans.
        Spans("Alpha `Xray` Beta").Should().BeEquivalentTo(["alpha", "xray", "beta"]);
    }

    /// <summary>Not a <see cref="AttributeExtractor.Extract"/>-only effect either: mention-span
    /// scanning consumes <see cref="ProseScanner"/> the same way, so a capitalized run after a
    /// stray, never-closed ``` now contributes a mention span, where before it would have been
    /// silently excluded to end of content (round 2 ruling: an unterminated fence is not a fence).</summary>
    [Fact]
    public void UnterminatedFence_IsNotAFence_MentionAfterItSurvives()
    {
        Spans("We met Alice.\n```\nCarol appears after the never-closed opener.")
            .Should().BeEquivalentTo(["alice", "carol"]);
    }

    [Fact]
    public void Frontmatter_IsProse_ContributesMentionSpans()
    {
        // ExtractMentionSpans no longer skips frontmatter (ProseScanner treats it as prose, same
        // as Extract) — a capitalized run inside the frontmatter block is now a mention span too,
        // scanned before the body since frontmatter lines come first in the content.
        Spans("---\nperson: Alice Smith\n---\nWe met Bob.").Should().BeEquivalentTo(["alice smith", "bob"]);
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
