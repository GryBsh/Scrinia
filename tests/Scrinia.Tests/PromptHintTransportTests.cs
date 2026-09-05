using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the pure <see cref="PromptHintTransport"/> parser. Covers positional-vs-stdin
/// selection, one-BOM strip, Unicode whitespace trim, strict single-JSON-object <c>prompt</c>
/// extraction, NFKC normalization, maximal letter/digit tokenization against the eight
/// commit/push inflections, case-insensitive membership, non-suppressing negation, near-miss
/// rejection, and the fail-silent contract for malformed, nonobject, missing, nonstring, and
/// case-mismatched input.
/// </summary>
public sealed class PromptHintTransportTests
{
    // ── SelectPrompt ──────────────────────────────────────────────────────

    [Fact]
    public void SelectPrompt_PositionalWins_OverStdin()
    {
        PromptHintTransport.SelectPrompt("commit", "{\"prompt\":\"push\"}").Should().Be("commit");
    }

    [Fact]
    public void SelectPrompt_FallsBackToStdin_WhenPositionalNullOrEmpty()
    {
        PromptHintTransport.SelectPrompt(null, "push").Should().Be("push");
        PromptHintTransport.SelectPrompt("", "push").Should().Be("push");
    }

    // ── Parse: positional wins end-to-end ─────────────────────────────────

    [Fact]
    public void Parse_PositionalWins_StdinJsonIgnored()
    {
        // stdin carries a push intent inside JSON; the positional commit must win and the
        // stdin JSON must never be parsed.
        var result = PromptHintTransport.Parse("commit", "{\"prompt\":\"push\"}");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
        result.PushIntent.Should().BeFalse();
    }

    // ── Parse: raw input classification ───────────────────────────────────

    [Fact]
    public void Parse_RawInput_ClassifiesCommit()
    {
        var result = PromptHintTransport.Parse(null, "please commit my changes");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
        result.PushIntent.Should().BeFalse();
    }

    // ── Parse: BOM / Unicode whitespace ───────────────────────────────────

    [Fact]
    public void Parse_OneBOM_ExtractsAndClassifies()
    {
        var result = PromptHintTransport.Parse(null, "\uFEFF{\"prompt\":\"commit\"}");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
    }

    [Fact]
    public void Parse_UnicodeWhitespaceTrimmed_Classifies()
    {
        var result = PromptHintTransport.Parse(null, "\u00A0commit\u00A0");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
    }

    // ── Parse: empty input ────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespaceInput_ReturnsSilent(string stdin)
    {
        var result = PromptHintTransport.Parse(null, stdin);

        result.Present.Should().BeFalse();
        result.CommitIntent.Should().BeFalse();
        result.PushIntent.Should().BeFalse();
    }

    [Fact]
    public void Parse_NullPositionalAndNullStdin_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, null);

        result.Present.Should().BeFalse();
    }

    // ── Parse: JSON object extraction ─────────────────────────────────────

    [Fact]
    public void Parse_ExactJsonWithTrailingWhitespace_ExtractsPrompt()
    {
        var result = PromptHintTransport.Parse(null, "{\"prompt\":\"commit\"}   ");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
    }

    [Fact]
    public void Parse_JsonWithExtraProperties_Allowed()
    {
        var result = PromptHintTransport.Parse(null, "{\"prompt\":\"push\",\"foo\":1,\"bar\":\"baz\"}");

        result.Present.Should().BeTrue();
        result.PushIntent.Should().BeTrue();
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{\"prompt\":\"commit\"");

        result.Present.Should().BeFalse();
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    [InlineData("true")]
    public void Parse_NonobjectJson_ReturnsSilent(string input)
    {
        PromptHintTransport.Parse(null, input).Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_MissingPromptProperty_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{\"foo\":\"bar\"}");

        result.Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_EmptyObject_MissingProperty_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{}");

        result.Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_NonstringPrompt_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{\"prompt\":123}");

        result.Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_CaseMismatchedPromptKey_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{\"Prompt\":\"commit\"}");

        result.Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_TrailingNonWhitespaceAfterJson_ReturnsSilent()
    {
        var result = PromptHintTransport.Parse(null, "{\"prompt\":\"commit\"}garbage");

        result.Present.Should().BeFalse();
    }

    // ── Parse: NFKC / tokenization ────────────────────────────────────────

    [Fact]
    public void Parse_NFKCFullwidth_Classifies()
    {
        // Fullwidth "commit" collapses to ASCII "commit" only after NFKC normalization.
        var result = PromptHintTransport.Parse(null, "\uFF43\uFF4F\uFF4D\uFF4D\uFF49\uFF54");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
    }

    [Fact]
    public void Parse_PunctuationBoundaries_SeparatesTokens()
    {
        var result = PromptHintTransport.Parse(null, "commit;push,done");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
        result.PushIntent.Should().BeTrue();
    }

    [Theory]
    [InlineData("commit", true, false)]
    [InlineData("commits", true, false)]
    [InlineData("committed", true, false)]
    [InlineData("committing", true, false)]
    [InlineData("push", false, true)]
    [InlineData("pushes", false, true)]
    [InlineData("pushed", false, true)]
    [InlineData("pushing", false, true)]
    public void Parse_AllEightInflections_Classifies(string input, bool commit, bool push)
    {
        var result = PromptHintTransport.Parse(null, input);

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().Be(commit);
        result.PushIntent.Should().Be(push);
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("Commit")]
    [InlineData("PUSH")]
    [InlineData("Push")]
    public void Parse_CaseInsensitiveTokenMatch(string input)
    {
        var result = PromptHintTransport.Parse(null, input);

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().Be(input.Contains("commit", StringComparison.OrdinalIgnoreCase));
        result.PushIntent.Should().Be(input.Contains("push", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_NegationDoesNotSuppress_FlagsCommit()
    {
        var result = PromptHintTransport.Parse(null, "I did not commit this");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
    }

    [Theory]
    [InlineData("commence")]
    [InlineData("pushpin")]
    [InlineData("recommit")]
    [InlineData("committees")]
    public void Parse_NearMissWords_ReturnsSilent(string input)
    {
        PromptHintTransport.Parse(null, input).Present.Should().BeFalse();
    }

    [Fact]
    public void Parse_BothIntents_SetBothFlags()
    {
        var result = PromptHintTransport.Parse(null, "commit then push");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
        result.PushIntent.Should().BeTrue();
    }

    [Fact]
    public void Parse_CommitBeforePush_SetsBothFlags()
    {
        var result = PromptHintTransport.Parse(null, "commit the push");

        result.Present.Should().BeTrue();
        result.CommitIntent.Should().BeTrue();
        result.PushIntent.Should().BeTrue();
    }

    // ── Silent contract ───────────────────────────────────────────────────

    [Fact]
    public void Silent_IsFullyPopulated()
    {
        var silent = PromptTransportResult.Silent;

        silent.Present.Should().BeFalse();
        silent.Prompt.Should().BeNull();
        silent.CommitIntent.Should().BeFalse();
        silent.PushIntent.Should().BeFalse();
    }

    [Fact]
    public void Parse_Silent_WhenNoIntentPresent()
    {
        var result = PromptHintTransport.Parse(null, "just some unrelated notes about bicycles");

        result.Present.Should().BeFalse();
        result.CommitIntent.Should().BeFalse();
        result.PushIntent.Should().BeFalse();
    }
}
