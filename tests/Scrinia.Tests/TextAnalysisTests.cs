using FluentAssertions;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class TextAnalysisTests
{
    // ── Tokenize ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_SimpleText_ReturnsLowercaseTokens()
    {
        var tokens = TextAnalysis.Tokenize("Hello World");

        tokens.Should().Equal("hello", "world");
    }

    [Fact]
    public void Tokenize_FiltersStopWords()
    {
        var tokens = TextAnalysis.Tokenize("The quick brown fox and the lazy dog");

        tokens.Should().NotContain("the");
        tokens.Should().NotContain("and");
        tokens.Should().Contain("quick");
        tokens.Should().Contain("brown");
        tokens.Should().Contain("fox");
        tokens.Should().Contain("lazy");
        tokens.Should().Contain("dog");
    }

    [Fact]
    public void Tokenize_FiltersShortTokens()
    {
        var tokens = TextAnalysis.Tokenize("I am a go x do");

        // "i", "a", "x" are <2 chars, "am", "go", "do" are stop words
        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_SplitsOnNonAlphanumeric()
    {
        var tokens = TextAnalysis.Tokenize("api:auth-flow_v2.0");

        tokens.Should().Contain("api");
        tokens.Should().Contain("auth");
        tokens.Should().Contain("flow");
        tokens.Should().Contain("v2");
    }

    [Fact]
    public void Tokenize_EmptyText_ReturnsEmpty()
    {
        TextAnalysis.Tokenize("").Should().BeEmpty();
        TextAnalysis.Tokenize("   ").Should().BeEmpty();
        TextAnalysis.Tokenize(null!).Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_PreservesDigits()
    {
        var tokens = TextAnalysis.Tokenize("HTTP 200 response code 404");

        tokens.Should().Contain("http");
        tokens.Should().Contain("200");
        tokens.Should().Contain("response");
        tokens.Should().Contain("code");
        tokens.Should().Contain("404");
    }

    // ── ComputeTermFrequencies ───────────────────────────────────────────────

    [Fact]
    public void ComputeTermFrequencies_CountsOccurrences()
    {
        var tf = TextAnalysis.ComputeTermFrequencies("auth auth auth login login token");

        tf["auth"].Should().Be(3);
        tf["login"].Should().Be(2);
        tf["token"].Should().Be(1);
    }

    [Fact]
    public void ComputeTermFrequencies_CaseInsensitive()
    {
        var tf = TextAnalysis.ComputeTermFrequencies("Auth AUTH auth");

        tf.Should().ContainKey("auth");
        tf["auth"].Should().Be(3);
    }

    [Fact]
    public void ComputeTermFrequencies_EmptyText_ReturnsEmpty()
    {
        TextAnalysis.ComputeTermFrequencies("").Should().BeEmpty();
    }

    // ── ExtractKeywords ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractKeywords_ReturnsTopNByFrequency()
    {
        string text = string.Join(" ", Enumerable.Repeat("authentication", 10))
            + " " + string.Join(" ", Enumerable.Repeat("token", 5))
            + " " + string.Join(" ", Enumerable.Repeat("refresh", 3))
            + " singleton";

        var keywords = TextAnalysis.ExtractKeywords(text, topN: 3);

        keywords.Should().HaveCount(3);
        keywords[0].Should().Be("authentication");
        keywords[1].Should().Be("token");
        keywords[2].Should().Be("refresh");
    }

    [Fact]
    public void ExtractKeywords_DefaultTopN_Returns25OrLess()
    {
        string text = "small text with authentication tokens";
        var keywords = TextAnalysis.ExtractKeywords(text);

        keywords.Length.Should().BeLessThanOrEqualTo(25);
        keywords.Length.Should().BeGreaterThan(0);
    }

    // ── MergeKeywords ────────────────────────────────────────────────────────

    [Fact]
    public void MergeKeywords_AgentFirst_ThenAuto()
    {
        var merged = TextAnalysis.MergeKeywords(
            ["auth", "jwt"],
            ["token", "refresh", "auth"]);

        merged[0].Should().Be("auth");
        merged[1].Should().Be("jwt");
        // "auth" not duplicated, so auto fills in token and refresh
        merged.Should().Contain("token");
        merged.Should().Contain("refresh");
        merged.Should().HaveCount(4);
    }

    [Fact]
    public void MergeKeywords_Deduplicates_CaseInsensitive()
    {
        var merged = TextAnalysis.MergeKeywords(
            ["Auth", "JWT"],
            ["auth", "jwt", "token"]);

        merged.Where(k => k == "auth").Should().HaveCount(1);
        merged.Where(k => k == "jwt").Should().HaveCount(1);
        merged.Should().Contain("token");
    }

    [Fact]
    public void MergeKeywords_CapsAtMaxTotal()
    {
        var agent = Enumerable.Range(0, 20).Select(i => $"agent{i}").ToArray();
        var auto = Enumerable.Range(0, 20).Select(i => $"auto{i}").ToArray();

        var merged = TextAnalysis.MergeKeywords(agent, auto, maxTotal: 10);

        merged.Should().HaveCount(10);
    }

    [Fact]
    public void MergeKeywords_NullAgentKeywords_UsesAutoOnly()
    {
        var merged = TextAnalysis.MergeKeywords(null, ["token", "auth"]);

        merged.Should().Equal("token", "auth");
    }
}
