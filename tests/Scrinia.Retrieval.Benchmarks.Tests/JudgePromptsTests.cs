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

    [Theory]
    [InlineData("correct", true)]
    [InlineData("CORRECT", true)]
    [InlineData("incorrect", false)]
    [InlineData("The answer is correct.", true)]
    [InlineData("This is incorrect", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseGrade_MapsVerdict(string? raw, bool expected)
        => JudgePrompts.ParseGrade(raw).Should().Be(expected);
}
