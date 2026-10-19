namespace Scrinia.Agent.Benchmarks.Tests;

[Collection("Benchmark orchestration")]
public sealed class ResumeTests(WorkspaceCatalogFixture fixture)
{
    [Fact]
    public async Task Grading_retry_uses_verified_answer_without_relearning_and_preserves_prior_attempt()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload(), Limits = new(4, 45, 180, 1) };
        var item = Assert.Single((await CaseSamples.Dataset()).Cases);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, fixture.Manifest(), false, CancellationToken.None);
        var old = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.SaveAnswerAsync(old, RunJournalTests.Answer("saved candidate"), CancellationToken.None);
        await journal.CommitOutcomeAsync(old, new("sample_abs", 1, CaseState.GradingFailed, RunJournalTests.Answer("saved candidate").Sha256, "judge", "JUDGE_INVALID_VERDICT"), CancellationToken.None);
        File.WriteAllText(Path.Combine(old.Workspace, "retained.txt"), "old learning");
        var order = new List<string>();
        var episode = new RecordingEpisode(order) { Behavior = (_, _, _) => throw new InvalidOperationException("Must not relearn.") };
        var grader = new RecordingGrader(order);
        var outcome = await new CaseRunner(episode, grader).RunAsync(item, 1, 2, config, journal, config.OutputDirectory, CancellationToken.None);
        Assert.Equal(CaseState.Correct, outcome.State);
        Assert.Equal(["judge"], order);
        Assert.Equal("saved candidate", grader.Answer!.Text);
        Assert.True(File.Exists(Path.Combine(old.Workspace, "retained.txt")));
        Assert.False(File.Exists(Path.Combine(journal.PathsFor(1, 2).Workspace, "retained.txt")));
        Assert.True(File.Exists(Path.Combine(old.Evidence, "outcome.json")));
    }

    [Fact]
    public async Task Interrupted_learning_uses_empty_attempt_and_never_replays_old_write()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload(), Limits = new(4, 45, 180, 1) };
        var item = Assert.Single((await CaseSamples.Dataset()).Cases);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, fixture.Manifest(), false, CancellationToken.None);
        var old = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        File.WriteAllText(Path.Combine(old.Workspace, "retained.txt"), "partial state");
        await journal.CreateEvents(old).WriteAsync("learn", "tool-intent", RunJournalTests.Data(), CancellationToken.None);
        var order = new List<string>();
        var outcome = await new CaseRunner(new RecordingEpisode(order), new RecordingGrader(order)).RunAsync(item, 1, 2, config, journal, config.OutputDirectory, CancellationToken.None);
        Assert.Equal(CaseState.Correct, outcome.State);
        Assert.Equal(["Learn", "Learn", "Answer", "judge"], order);
        Assert.False(File.Exists(Path.Combine(journal.PathsFor(1, 2).Workspace, "retained.txt")));
        Assert.True(File.Exists(Path.Combine(old.Workspace, "retained.txt")));
    }
}
