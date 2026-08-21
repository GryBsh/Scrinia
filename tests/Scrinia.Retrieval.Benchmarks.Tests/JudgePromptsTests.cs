using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Judge;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class JudgePromptsTests
{
    [Fact]
    public void AnswererUser_IncludesQuestionAndEveryRetrievedItem()
    {
        string u = JudgePrompts.AnswererUser("Where did Maria go?", ["Maria booked Lisbon.", "Flight on Tuesday."]);
        u.Should().Contain("Where did Maria go?");
        u.Should().Contain("Maria booked Lisbon.").And.Contain("Flight on Tuesday.");
    }

    [Fact]
    public void GraderUser_IncludesAllThreeInputs()
    {
        string u = JudgePrompts.GraderUser("Q?", "Lisbon", "Lisbon, Portugal");
        u.Should().Contain("Q?").And.Contain("Lisbon").And.Contain("Lisbon, Portugal");
    }

    // The grader system prompt mandates "exactly one word: correct or incorrect", so the
    // verdict is matched exactly rather than by substring. Anything off-contract returns
    // null and is excluded from the aggregate instead of being guessed at -- see
    // ParseGrade_OffContractReplies_AreUnparseable for why guessing was wrong in both
    // directions.
    [Theory]
    [InlineData("correct", true)]
    [InlineData("CORRECT", true)]
    [InlineData("  correct  ", true)]
    [InlineData("correct.", true)]
    [InlineData("incorrect", false)]
    [InlineData("INCORRECT", false)]
    public void ParseGrade_ReadsAOneWordVerdict(string raw, bool expected)
        => JudgePrompts.ParseGrade(raw).Should().Be(expected);

    // A grader that restates the prompt's own "Verdict (correct/incorrect):" rubric before
    // answering was misgraded by the previous substring parser: the rubric contains BOTH
    // tokens, and checking "incorrect" first meant an echoed *correct* verdict scored
    // incorrect. Measured over the 3,079 cached grader replies, this shape had fired twice.
    [Theory]
    [InlineData("Verdict (correct/incorrect): correct", true)]
    [InlineData("Verdict (correct/incorrect): incorrect", false)]
    [InlineData("verdict: correct", true)]
    [InlineData("verdict: incorrect", false)]
    [InlineData("Let me compare the two answers.\nThey agree.\nVerdict: correct", true)]
    public void ParseGrade_TakesTheVerdictAfterAnEchoedRubric(string raw, bool expected)
        => JudgePrompts.ParseGrade(raw).Should().Be(expected);

    // Each of these was scored as a definite verdict by the substring parser, in one
    // direction or the other, with no counter and no failure-sink record:
    //   "not correct"  contains "correct" and not "incorrect"  -> scored CORRECT
    //   free prose     contains neither token                  -> scored INCORRECT
    //   echoed options contains both, undecided                -> scored INCORRECT
    // None is a verdict. Null routes them to the unparseable bucket where a human sees them.
    [Theory]
    [InlineData("This is not correct")]
    [InlineData("the candidate is not correct")]
    [InlineData("The candidate matches the gold answer.")]
    [InlineData("The answer is correct.")]
    [InlineData("Verdict: correct/incorrect")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseGrade_OffContractReplies_AreUnparseable(string? raw)
        => JudgePrompts.ParseGrade(raw).Should().BeNull();
}
