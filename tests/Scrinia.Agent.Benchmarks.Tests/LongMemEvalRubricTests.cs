using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class LongMemEvalRubricTests
{
    [Fact]
    public void Every_category_and_abstention_matches_the_pinned_upstream_function()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/rubric-golden.json")));
        Assert.Equal(12, golden.RootElement.GetArrayLength());
        foreach (var item in golden.RootElement.EnumerateArray())
        {
            var evaluation = new EvaluationInput("synthetic", item.GetProperty("category").GetString()!, item.GetProperty("abstention").GetBoolean(), "Reference {answer}");
            Assert.Equal(item.GetProperty("prompt").GetString(), LongMemEvalRubric.Render(evaluation, new("Question {x}?", "2024-06-15"), "Candidate {question}"));
        }
        Assert.Equal(64, LongMemEvalRubric.ContentHash.Length);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData(" NO \n", false)]
    [InlineData("yes or no", null)]
    [InlineData("not yes", null)]
    [InlineData("", null)]
    [InlineData("yes.", true)]
    [InlineData("No.", false)]
    [InlineData(" YES. \n", true)]
    [InlineData(" no. \n", false)]
    [InlineData("yes..", null)]
    [InlineData("no...", null)]
    [InlineData("yes. no.", null)]
    [InlineData("no. explanation", null)]
    [InlineData("yes .", null)]
    [InlineData("no!", null)]
    [InlineData("Yes\n\nThe answer matches the reference.", true)]
    [InlineData("No.\r\nThe answer invents information.", false)]
    [InlineData(" \r\n YES. \r\n\r\nExplanation.\r\n", true)]
    [InlineData("\n no \rExplanation.", false)]
    [InlineData("Yes\nThe phrase 'no evidence' appears in the explanation.", true)]
    [InlineData("No\nThe explanation discusses a yes/no question.", false)]
    [InlineData("Yes\nExplanation.\nYES.", true)]
    [InlineData("No\nExplanation.\nno.", false)]
    [InlineData("Yes\nExplanation.\nNo.", null)]
    [InlineData("No.\nExplanation.\nYES", null)]
    [InlineData("Yesterday the answer was yes.", null)]
    [InlineData("Nobody knows.", null)]
    [InlineData("Yes, because it matches.", null)]
    [InlineData("No. The answer is wrong.", null)]
    [InlineData("Explanation first.\nYes", null)]
    [InlineData(" \r\n\t\r\n ", null)]
    public void Verdict_is_unambiguous(string text, bool? expected) => Assert.Equal(expected, LongMemEvalRubric.ParseVerdict(text));

    [Fact]
    public void Unknown_category_and_missing_reference_fail_explicitly()
    {
        Assert.Throws<InvalidDataException>(() => LongMemEvalRubric.Render(new("id", "unknown", false, "reference"), new("question", "date"), "candidate"));
        Assert.Throws<InvalidDataException>(() => LongMemEvalRubric.Render(new("id", "single-session-user", false, ""), new("question", "date"), "candidate"));
    }
}
