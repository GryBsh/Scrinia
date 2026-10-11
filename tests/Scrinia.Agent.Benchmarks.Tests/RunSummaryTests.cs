namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class RunSummaryTests
{
    [Fact]
    public async Task Report_includes_failed_attempt_cost_and_exports_incorrect_answer()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, RunJournalTests.Manifest(), false, CancellationToken.None);
        var first = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.CreateEvents(first).WriteAsync("learn", "model-request", RunJournalTests.Data(), CancellationToken.None);
        await journal.CommitOutcomeAsync(first, new("sample_abs", 1, CaseState.ExecutionFailed, null, "learn", "PROVIDER_FAILED"), CancellationToken.None);
        var second = new AttemptWorkspace().Create(config.OutputDirectory, 1, 2, config.Scrinia);
        await journal.SaveAnswerAsync(second, RunJournalTests.Answer("incorrect answer"), CancellationToken.None);
        await journal.CommitOutcomeAsync(second, new("sample_abs", 2, CaseState.Incorrect, RunJournalTests.Answer("incorrect answer").Sha256, null, null), CancellationToken.None);
        await SummaryWriter.WriteAsync(config.OutputDirectory, await CaseSamples.Dataset(), journal, new StringWriter(), CancellationToken.None);
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json")));
        Assert.Equal(2, report.RootElement.GetProperty("attempts").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, report.RootElement.GetProperty("caseUsage").GetProperty("inputTokens").ValueKind);
        var answer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "answers.jsonl")));
        Assert.Equal("sample_abs", answer.RootElement.GetProperty("question_id").GetString());
        Assert.Equal("incorrect answer", answer.RootElement.GetProperty("hypothesis").GetString());
    }

    private static CaseOutcome Outcome(string id, CaseState state) => new(id, 1, state, state is CaseState.Correct or CaseState.Incorrect or CaseState.Answered or CaseState.GradingFailed ? new string('a', 64) : null, null, null);

    [Fact]
    public void Zero_graded_is_unknown_and_partial_success_shows_coverage()
    {
        var empty = RunSummary.Compute(["a", "b"], []);
        Assert.Null(empty.Accuracy);
        Assert.True(empty.Provisional);
        var partial = RunSummary.Compute(["a", "b"], [Outcome("a", CaseState.Correct), Outcome("b", CaseState.ExecutionFailed)]);
        Assert.Equal(1, partial.Correct);
        Assert.Equal(1, partial.Graded);
        Assert.Equal(2, partial.Selected);
        Assert.Equal(1.0, partial.Accuracy);
        Assert.Equal(0.5, partial.EndToEndSuccess);
        Assert.True(partial.Provisional);
    }

    [Fact]
    public void Incorrect_answers_remain_in_the_denominator()
    {
        var summary = RunSummary.Compute(["a", "b"], [Outcome("a", CaseState.Correct), Outcome("b", CaseState.Incorrect)]);
        Assert.Equal(0.5, summary.Accuracy);
        Assert.False(summary.Provisional);
        Assert.Equal(2, summary.Answered);
    }

    [Theory]
    [InlineData(CaseState.Pending)]
    [InlineData(CaseState.Running)]
    [InlineData(CaseState.Cancelled)]
    [InlineData(CaseState.Answered)]
    [InlineData(CaseState.GradingFailed)]
    public void Nonfinal_states_leave_coverage_incomplete(CaseState state)
    {
        var summary = RunSummary.Compute(["a"], [Outcome("a", state)]);
        Assert.Null(summary.Accuracy);
        Assert.True(summary.Provisional);
    }

    [Fact]
    public void Duplicate_and_unknown_outcomes_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RunSummary.Compute(["a"], [Outcome("a", CaseState.Correct), Outcome("a", CaseState.Incorrect)]));
        Assert.Throws<InvalidDataException>(() => RunSummary.Compute(["a"], [Outcome("other", CaseState.Correct)]));
    }
}
