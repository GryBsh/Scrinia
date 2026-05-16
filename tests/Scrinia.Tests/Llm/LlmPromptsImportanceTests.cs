using FluentAssertions;
using Scrinia.Core.Llm;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for the <c>ParseImportance</c> response parser. Small Tier 2 models occasionally
/// embed the rating in surrounding prose despite the "output only the number" instruction
/// — the parser is engineered to tolerate that without losing the score.
/// </summary>
public sealed class LlmPromptsImportanceTests
{
    [Theory]
    [InlineData("7", 7)]
    [InlineData("  7  ", 7)]
    [InlineData("10", 10)]
    [InlineData("1", 1)]
    [InlineData("7.", 7)]
    [InlineData("7/10", 7)]
    [InlineData("7 out of 10", 7)]
    [InlineData("I'd rate this 5.", 5)]
    [InlineData("Rating: 8", 8)]
    public void ParseImportance_ExtractsFirstDigitRun(string raw, int expected)
    {
        LlmPrompts.ParseImportance(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no number here")]
    public void ParseImportance_NullOrEmptyOrTextOnly_ReturnsNull(string? raw)
    {
        LlmPrompts.ParseImportance(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("100")]
    [InlineData("-5")]
    public void ParseImportance_OutOfRange_ReturnsNull(string raw)
    {
        // The parser refuses to silently clamp — clamping happens at the ranker level
        // via ComputeImportanceTerm. If the LLM responds out of range we treat it as
        // "didn't follow instructions" and leave the field null so the ranker uses the
        // neutral midpoint instead of accepting a possibly-misinterpreted score.
        LlmPrompts.ParseImportance(raw).Should().BeNull();
    }
}
