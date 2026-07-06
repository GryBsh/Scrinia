using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="AgentPrompts"/> — the parsers translate freeform LLM output
/// into clean anchor/rewrite/HyDE strings. The agent harness's output quality
/// hinges on these so they get explicit coverage for the formats LLMs actually
/// emit (bulleted, numbered, quoted, fenced).
/// </summary>
public class AgentPromptsTests
{
    [Fact]
    public void ParseAnchors_Splits_OneAnchorPerLine()
    {
        string raw = "how do I undo my last commit\nwhat is the merge strategy\nrolling back to a tag";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().HaveCount(3);
        anchors[0].Should().Be("how do I undo my last commit");
        anchors[1].Should().Be("what is the merge strategy");
        anchors[2].Should().Be("rolling back to a tag");
    }

    [Fact]
    public void ParseAnchors_Strips_DashBullet_Prefix()
    {
        string raw = "- how do I undo\n- what's the policy\n- when was it changed";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().Equal("how do I undo", "what's the policy", "when was it changed");
    }

    [Fact]
    public void ParseAnchors_Strips_AsteriskBullet_Prefix()
    {
        string raw = "* foo bar baz\n*  another one";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().Equal("foo bar baz", "another one");
    }

    [Fact]
    public void ParseAnchors_Strips_Numbered_Prefix()
    {
        string raw = "1. first anchor here\n2) second anchor here\n3. third anchor here";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().HaveCount(3);
        anchors[0].Should().Be("first anchor here");
        anchors[1].Should().Be("second anchor here");
        anchors[2].Should().Be("third anchor here");
    }

    [Fact]
    public void ParseAnchors_Strips_Surrounding_Quotes()
    {
        string raw = "\"how do I undo my last commit\"\n'what is the merge policy'";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().Equal("how do I undo my last commit", "what is the merge policy");
    }

    [Fact]
    public void ParseAnchors_Dedups_CaseInsensitively()
    {
        string raw = "How Do I Undo\nhow do I undo\nHOW DO I UNDO\nsomething else entirely";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().HaveCount(2);
        anchors[0].Should().Be("How Do I Undo");
        anchors[1].Should().Be("something else entirely");
    }

    [Fact]
    public void ParseAnchors_DropsAnchors_BelowMinLength()
    {
        // "yes" and "no" are below MinAnchorChars=5
        string raw = "yes\nno\nhow do I undo my commit";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().ContainSingle().Which.Should().Be("how do I undo my commit");
    }

    [Fact]
    public void ParseAnchors_Truncates_AnchorsAbove_MaxLength()
    {
        string longAnchor = new string('x', AgentPrompts.MaxAnchorChars + 50);
        var anchors = AgentPrompts.ParseAnchors(longAnchor);
        anchors.Should().ContainSingle();
        anchors[0].Length.Should().BeLessThanOrEqualTo(AgentPrompts.MaxAnchorChars);
    }

    [Fact]
    public void ParseAnchors_CapsTotal_At_MaxAnchorsPerDoc()
    {
        // 10 distinct lines; should be capped at MaxAnchorsPerDoc (7).
        string raw = string.Join('\n',
            Enumerable.Range(0, 10).Select(i => $"anchor number {i}"));
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().HaveCount(AgentPrompts.MaxAnchorsPerDoc);
    }

    [Fact]
    public void ParseAnchors_Handles_Empty_AndWhitespace()
    {
        AgentPrompts.ParseAnchors(null).Should().BeEmpty();
        AgentPrompts.ParseAnchors(string.Empty).Should().BeEmpty();
        AgentPrompts.ParseAnchors("   \n\n   \r\n").Should().BeEmpty();
    }

    [Fact]
    public void ParseAnchors_DoubleBullet_StripsBoth()
    {
        // Some smaller models emit "- 1. foo" — both markers should peel.
        string raw = "- 1. how do I undo my last commit";
        var anchors = AgentPrompts.ParseAnchors(raw);
        anchors.Should().ContainSingle().Which.Should().Be("how do I undo my last commit");
    }

    [Fact]
    public void ParseRewrite_TakesFirst_NonEmpty_Line()
    {
        string raw = "\n\nhow can I undo my last git commit (revert, reset)\n\nmore stuff";
        var result = AgentPrompts.ParseRewrite(raw);
        result.Should().Be("how can I undo my last git commit (revert, reset)");
    }

    [Fact]
    public void ParseRewrite_StripsCode_Fence()
    {
        string raw = "```\nrewritten query here\n```";
        var result = AgentPrompts.ParseRewrite(raw);
        result.Should().Be("rewritten query here");
    }

    [Fact]
    public void ParseRewrite_StripsSurroundingQuotes()
    {
        AgentPrompts.ParseRewrite("\"how to undo a commit\"").Should().Be("how to undo a commit");
        AgentPrompts.ParseRewrite("'how to undo a commit'").Should().Be("how to undo a commit");
    }

    [Fact]
    public void ParseRewrite_ReturnsNull_OnEmpty()
    {
        AgentPrompts.ParseRewrite(null).Should().BeNull();
        AgentPrompts.ParseRewrite("").Should().BeNull();
        AgentPrompts.ParseRewrite("   \n  \n").Should().BeNull();
    }

    [Fact]
    public void ParseHyde_StripsFences_AndQuotes()
    {
        string raw = "```\n\"To undo your last commit, run `git reset HEAD~1`.\"\n```";
        var result = AgentPrompts.ParseHyde(raw);
        result.Should().Be("To undo your last commit, run `git reset HEAD~1`.");
    }

    [Fact]
    public void ParseHyde_ReturnsNull_OnEmpty()
    {
        AgentPrompts.ParseHyde(null).Should().BeNull();
        AgentPrompts.ParseHyde("").Should().BeNull();
    }

    [Fact]
    public void AgentModes_Label_ProducesStableTags()
    {
        AgentModes.Off.Label().Should().Be("off");
        AgentModes.AnchorsOnly.Label().Should().Be("anchors");
        AgentModes.RewriteOnly.Label().Should().Be("rewrite");
        AgentModes.HydeOnly.Label().Should().Be("hyde");
        AgentModes.All.Label().Should().Be("all");
    }

    [Fact]
    public void AgentModes_Label_IncludesTypedKeywords()
    {
        AgentModes.TypedKeywordsOnly.Label().Should().Be("typed");
        new AgentModes(true, false, false, true).Label().Should().Be("anchors+typed");
    }

    [Fact]
    public void AgentModes_Parse_HandlesTypedAndCombos()
    {
        AgentModes.Parse("typed").Should().Be(AgentModes.TypedKeywordsOnly);
        AgentModes.Parse("anchors").Should().Be(AgentModes.AnchorsOnly);
        AgentModes.Parse("anchors,typed").Should().Be(new AgentModes(true, false, false, true));
        AgentModes.Parse("off").Should().Be(AgentModes.Off);
        AgentModes.Parse("all").Should().Be(AgentModes.All);
        AgentModes.Parse(null).Should().Be(AgentModes.Off);
    }

    [Fact]
    public void AgentModes_Parse_ThrowsOnUnknownComponent()
    {
        Action act = () => AgentModes.Parse("anchors,bogus");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseTypedKeywords_KeepsRealTypes_Lowercased()
    {
        string raw = "person:Maria\ntopic:Home Renovation\nthing:Toyota Camry";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("person:maria", "topic:home renovation", "thing:toyota camry");
    }

    [Fact]
    public void ParseTypedKeywords_DropsUnknownAndLegacyEntityPrefixes()
    {
        // person/place/topic kept; date:/random: unknown and the legacy generic entity: dropped
        // (the typed design replaced the single entity: bucket with real types).
        string raw = "place:paris\ndate:2027-01-01\nperson:bob\nrandom:thing\nentity:mara\ntopic:cooking";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("place:paris", "person:bob", "topic:cooking");
    }

    [Fact]
    public void ParseTypedKeywords_DropsLinesWithoutPrefixValue()
    {
        string raw = "person:\n:orphan\njust some text\ntopic:music";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("topic:music");
    }

    [Fact]
    public void ParseTypedKeywords_StripsBulletsAndQuotes()
    {
        string raw = "- person:alice\n* topic:travel\n1. \"org:bob inc\"";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("person:alice", "topic:travel", "org:bob inc");
    }

    [Fact]
    public void ParseTypedKeywords_DedupsCaseInsensitively()
    {
        string raw = "person:Maria\nPERSON:maria\ntopic:cooking";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("person:maria", "topic:cooking");
    }

    [Fact]
    public void ParseTypedKeywords_CapsAt_MaxTypedKeywordsPerDoc()
    {
        // Feed more than the cap so the clamp is actually exercised (derive the count
        // from the constant so this stays correct if MaxTypedKeywordsPerDoc is retuned).
        string raw = string.Join('\n',
            Enumerable.Range(0, AgentPrompts.MaxTypedKeywordsPerDoc + 6).Select(i => $"topic:theme{i}"));
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().HaveCount(AgentPrompts.MaxTypedKeywordsPerDoc);
    }

    [Fact]
    public void ParseTypedKeywords_HandlesEmptyAndWhitespace()
    {
        AgentPrompts.ParseTypedKeywords(null).Should().BeEmpty();
        AgentPrompts.ParseTypedKeywords("").Should().BeEmpty();
        AgentPrompts.ParseTypedKeywords("  \n\r\n  ").Should().BeEmpty();
    }
}
