namespace Scrinia.Agent.Benchmarks.Tests;

[CollectionDefinition("Benchmark orchestration")]
public sealed class BenchmarkOrchestrationTestsGroup : ICollectionFixture<WorkspaceCatalogFixture>;

public sealed class WorkspaceCatalogFixture : IAsyncLifetime, IDisposable
{
    private readonly ScratchDirectory _temp = new();
    public string CatalogHash { get; private set; } = "";
    public async Task InitializeAsync()
    {
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(_temp.Root, "probe"), 1, 1, settings);
        await using var events = new CapturingEvents();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        CatalogHash = session.CatalogHash;
    }
    public Task DisposeAsync() { _temp.Dispose(); return Task.CompletedTask; }
    public void Dispose() => _temp.Dispose();
    internal RunManifest Manifest() => RunJournalTests.Manifest() with
    { ContentHashes = new() { ["localInputs"] = "local", ["mcpCatalog"] = CatalogHash, ["agentModel"] = "unavailable", ["judgeModel"] = "unavailable" } };
}

[Collection("Benchmark orchestration")]
public sealed class CaseRunnerTests(WorkspaceCatalogFixture fixture)
{
    [Theory]
    [InlineData("provider", CaseState.ExecutionFailed, "PROVIDER_FAILED")]
    [InlineData("deadline", CaseState.ExecutionFailed, "EPISODE_DEADLINE")]
    [InlineData("cancel", CaseState.Cancelled, "CANCELLED")]
    [InlineData("outage-deadline", CaseState.ExecutionFailed, "PROVIDER_UNAVAILABLE")]
    [InlineData("outage-cancel", CaseState.Cancelled, "CANCELLED")]
    public async Task Episode_failures_preserve_typed_outcomes(string mode, CaseState state, string code)
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload(), Limits = new(4, mode == "deadline" ? 1 : 45, 180, 1) };
        var item = Assert.Single((await CaseSamples.Dataset()).Cases);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, fixture.Manifest(), false, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var episode = new RecordingEpisode([]) { Behavior = async (_, _, ct) =>
        {
            if (mode.StartsWith("outage-", StringComparison.Ordinal))
            {
                var fake = new ScriptedChatClient(new HttpRequestException("offline", null, System.Net.HttpStatusCode.ServiceUnavailable));
                await using var events = new CapturingEvents();
                using var observed = new ObservedChatClient(fake, config.Agent, config.Limits with { MaxModelAttempts = 5 }, events, "learn");
                using var networkDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (mode == "outage-deadline") networkDeadline.CancelAfter(TimeSpan.FromMilliseconds(100));
                if (mode == "outage-cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
                await observed.GetResponseAsync([new(Microsoft.Extensions.AI.ChatRole.User, "input")], cancellationToken: networkDeadline.Token);
                throw new InvalidOperationException("unreachable");
            }
            if (mode == "provider") throw new HttpRequestException("private provider message");
            if (mode == "cancel") cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        } };
        var outcome = await new CaseRunner(episode, new RecordingGrader([])).RunAsync(item, 1, 1, config, journal, config.OutputDirectory, cancel.Token);
        Assert.Equal(state, outcome.State);
        Assert.Equal(code, outcome.FailureCode);
        Assert.Equal(outcome, Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None)));
        Assert.DoesNotContain("private provider message", File.ReadAllText(Path.Combine(journal.PathsFor(1, 1).Evidence, "events.ndjson")));
    }

    [Fact]
    public async Task Catalog_drift_stops_before_episode_dispatch()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        var manifest = fixture.Manifest();
        manifest.ContentHashes["mcpCatalog"] = "changed";
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, manifest, false, CancellationToken.None);
        var episode = new RecordingEpisode([]);
        var item = (await CaseSamples.Dataset()).Cases[0];
        var error = await Assert.ThrowsAsync<BenchmarkFailureException>(() => new CaseRunner(episode, new RecordingGrader([])).RunAsync(
            item, 1, 1, config, journal, config.OutputDirectory, CancellationToken.None));
        Assert.Equal(BenchmarkFailureCode.CATALOG_DRIFT, error.Code);
        Assert.Empty(episode.Inputs);
    }

    [Fact]
    public async Task Runs_history_then_fresh_answer_then_judge_and_disposes_between_episodes()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload(), Limits = new(4, 45, 180, 1) };
        var item = Assert.Single((await CaseSamples.Dataset()).Cases);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, fixture.Manifest(), false, CancellationToken.None);
        var order = new List<string>();
        var episode = new RecordingEpisode(order);
        var grader = new RecordingGrader(order);
        var outcome = await new CaseRunner(episode, grader).RunAsync(item, 1, 1, config, journal, config.OutputDirectory, CancellationToken.None);
        Assert.Equal(CaseState.Correct, outcome.State);
        Assert.Equal(["Learn", "Learn", "Answer", "judge"], order);
        Assert.All(episode.Inputs.Take(2), input => Assert.DoesNotContain(item.Answer.Question, input.UserMessage));
        Assert.DoesNotContain(item.Sessions[0].Turns[0].Content, episode.Inputs[^1].UserMessage);
        Assert.All(episode.Sessions, session => Assert.NotNull(session.CompletedProcessId));
        Assert.Equal("candidate", grader.Answer!.Text);
        Assert.Equal(CaseState.Correct, Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None)).State);
    }
}

internal sealed class RecordingEpisode(List<string> order) : IAgentEpisode
{
    public List<EpisodeInput> Inputs { get; } = [];
    public List<ScriniaMcpSession> Sessions { get; } = [];
    public Func<EpisodeInput, ScriniaMcpSession, CancellationToken, Task<EpisodeResult>>? Behavior { get; set; }
    public async Task<EpisodeResult> RunAsync(EpisodeInput input, ScriniaMcpSession session, ModelSettings settings, RunLimits limits, IRunEvents events, CancellationToken ct)
    {
        if (Sessions.Count > 0) Assert.NotNull(Sessions[^1].CompletedProcessId);
        order.Add(input.Phase.ToString()); Inputs.Add(input); Sessions.Add(session);
        return Behavior is null ? new(input.Phase == EpisodePhase.Answer ? "candidate" : "learned", new(null, null), 0, 0, 0) : await Behavior(input, session, ct);
    }
}

internal sealed class RecordingGrader(List<string> order) : IAnswerGrader
{
    public AnswerRecord? Answer { get; private set; }
    public bool Fail { get; set; }
    public Task<GradeResult> GradeAsync(EvaluationInput evaluation, AnswerInput input, AnswerRecord answer, ModelSettings settings, RunLimits limits, IRunEvents events, CancellationToken ct)
    {
        order.Add("judge"); Answer = answer;
        return Task.FromResult(Fail ? new GradeResult(null, "malformed", new(null, null), "JUDGE_INVALID_VERDICT") : new(true, "yes", new(null, null), null));
    }
}
