using System.Net;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ReasoningLoopRecoveryTests
{
    // Long repeated planning paragraphs, with no task-specific facts or benchmark answers.
    private const string Paragraph =
        "I have identified the relevant facts and should now store them using the available memory tool. " +
        "Before doing so I will reconsider the same selection once more, although no new information has appeared.";
    internal static ChatResponse Loop() => new(new ChatMessage(ChatRole.Assistant,
        [new TextReasoningContent(string.Join("\n\n", Enumerable.Repeat(Paragraph, 30))), new TextContent("")]))
    {
        FinishReason = ChatFinishReason.Length, ModelId = "reported-fixture-model",
        Usage = new() { InputTokenCount = 100, OutputTokenCount = 16384 }
    };
    private static ModelSettings Settings => ObservedChatClientTests.Settings with { MaxOutputTokens = 16384, TopP = .95f, TopK = 20 };
    private static RunLimits Limits => new(8, 300, 3600, 5);

    [Fact]
    public async Task Loop_retry_keeps_history_and_settings_and_counts_both_responses()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(Loop(), ScriptedChatClient.Text("Learned.",
            new() { InputTokenCount = 100, OutputTokenCount = 3 }));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        var result = await observed.GetResponseAsync([new(ChatRole.User, "Historical input")],
            new() { Instructions = "Store facts.", ConversationId = "must-not-reuse" });
        Assert.Equal("Learned.", result.Text);
        Assert.Equal(2, observed.ModelRequests);
        Assert.Equal(new UsageTotals(200, 16387), observed.Usage);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal("Historical input", Assert.Single(r.Messages).Text);
            Assert.Equal("Store facts.", r.Options!.Instructions);
            Assert.Equal(Settings.Temperature, r.Options.Temperature);
            Assert.Equal(Settings.TopP, r.Options.TopP);
            Assert.Equal(Settings.TopK, r.Options.TopK);
            Assert.Equal(16384, r.Options.MaxOutputTokens);
            Assert.Null(r.Options.ConversationId);
        });
        var detected = Assert.Single(events.Items, e => e.Kind == "reasoning-loop");
        Assert.True(detected.Data.GetProperty("retry").GetBoolean());
        Assert.Equal(2, events.Items.Count(e => e.Kind == "model-response"));
        Assert.DoesNotContain(events.Items, e => e.Kind == "model-error");
    }

    [Fact]
    public async Task Persistent_loop_stops_after_one_recovery_and_is_not_an_endpoint_outage()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(Loop(), Loop(), ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(2, observed.ModelRequests);
        Assert.Equal(new UsageTotals(200, 32768), observed.Usage);
        Assert.Equal([true, false], events.Items.Where(e => e.Kind == "reasoning-loop")
            .Select(e => e.Data.GetProperty("retry").GetBoolean()));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(8, 1)]
    public async Task Loop_recovery_respects_existing_request_and_attempt_limits(int turns, int attempts)
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(Loop(), ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, Settings, Limits with
            { MaxModelTurnsPerEpisode = turns, MaxModelAttempts = attempts }, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(1, observed.ModelRequests);
        Assert.False(Assert.Single(events.Items, e => e.Kind == "reasoning-loop").Data.GetProperty("retry").GetBoolean());
    }

    [Theory]
    [InlineData("ordinary-truncation")]
    [InlineData("visible-answer")]
    [InlineData("short-repetition")]
    [InlineData("mostly-unique")]
    public async Task Other_truncated_responses_are_not_retried(string kind)
    {
        await using var events = new CapturingEvents();
        var response = Loop();
        if (kind == "ordinary-truncation")
            response.Messages[0].Contents = [new TextReasoningContent(string.Join("\n\n",
                Enumerable.Range(0, 50).Select(i => $"Record {i}: " + Paragraph)))];
        if (kind == "visible-answer") response.Messages[0].Contents.Add(new TextContent("An actual answer."));
        if (kind == "short-repetition") response.Messages[0].Contents = [new TextReasoningContent(string.Join("\n\n", Enumerable.Repeat("Consider this.", 1000)))];
        if (kind == "mostly-unique")
            response.Messages[0].Contents = [new TextReasoningContent(string.Join("\n\n",
                Enumerable.Repeat(Paragraph, 6).Concat(Enumerable.Range(0, 50).Select(i => $"Record {i}: " + Paragraph))))];
        var fake = new ScriptedChatClient(response, ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Single(fake.Requests);
        Assert.DoesNotContain(events.Items, e => e.Kind == "reasoning-loop");
    }

    [Fact]
    public async Task Normal_completion_is_not_rejected_for_repetitive_reasoning()
    {
        await using var events = new CapturingEvents();
        var response = Loop();
        response.FinishReason = ChatFinishReason.Stop;
        response.Messages[0].Contents.Add(new TextContent("Learned."));
        using var observed = new ObservedChatClient(new ScriptedChatClient(response), Settings, Limits, events, "learn");
        Assert.Equal("Learned.", (await observed.GetResponseAsync([new(ChatRole.User, "input")])).Text);
        Assert.Equal(1, observed.ModelRequests);
    }

    [Fact]
    public async Task Identity_drift_is_checked_before_loop_recovery()
    {
        await using var events = new CapturingEvents();
        using var observed = new ObservedChatClient(new ScriptedChatClient(Loop()), Settings, Limits,
            events, "learn", expectedModelId: "another-model");
        var error = await Assert.ThrowsAsync<BenchmarkFailureException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(BenchmarkFailureCode.MODEL_DRIFT, error.Code);
        Assert.Equal(1, observed.ModelRequests);
        Assert.DoesNotContain(events.Items, e => e.Kind == "reasoning-loop");
    }

    [Fact]
    public async Task Failure_to_record_loop_decision_prevents_retry()
    {
        await using var events = new CapturingEvents { FailOnceOnKind = "reasoning-loop" };
        using var observed = new ObservedChatClient(new ScriptedChatClient(Loop(), ScriptedChatClient.Text("must not run")),
            Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<IOException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(1, observed.ModelRequests);
    }

    [Fact]
    public async Task Cancellation_prevents_loop_retry()
    {
        await using var events = new CapturingEvents();
        using var cancellation = new CancellationTokenSource();
        var fake = new ScriptedChatClient(Loop(), ScriptedChatClient.Text("must not run")) { OnRequest = cancellation.Cancel };
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observed.GetResponseAsync(
            [new(ChatRole.User, "input")], cancellationToken: cancellation.Token));
        Assert.Equal(1, observed.ModelRequests);
    }

    [Fact]
    public async Task Endpoint_and_loop_retries_share_the_same_attempt_allowance()
    {
        await using var events = new CapturingEvents();
        var time = new ImmediateTimeProvider();
        var fake = new ScriptedChatClient(new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable),
            Loop(), ScriptedChatClient.Text("recovered"));
        using var observed = new ObservedChatClient(fake, Settings, Limits with { MaxModelAttempts = 3 }, events, "learn", timeProvider: time);
        Assert.Equal("recovered", (await observed.GetResponseAsync([new(ChatRole.User, "input")])).Text);
        Assert.Equal(3, observed.ModelRequests);
        Assert.Equal([5d], time.Delays.Select(d => d.TotalSeconds));
        Assert.Null(observed.Usage.OutputTokens);
    }

    [Fact]
    public async Task Loop_after_endpoint_retry_cannot_exceed_shared_attempt_limit()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable),
            Loop(), ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, Settings, Limits with { MaxModelAttempts = 2 },
            events, "learn", timeProvider: new ImmediateTimeProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(2, observed.ModelRequests);
        Assert.False(Assert.Single(events.Items, e => e.Kind == "reasoning-loop").Data.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public async Task Recovery_allowance_is_per_request_and_tolerates_whitespace_variation()
    {
        await using var events = new CapturingEvents();
        var varied = Loop();
        varied.Messages[0].Contents = [new TextReasoningContent(string.Join("\r\n \r\n",
            Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? Paragraph : Paragraph.Replace(" ", "  "))))];
        using var observed = new ObservedChatClient(new ScriptedChatClient(varied, ScriptedChatClient.Text("first"),
            Loop(), ScriptedChatClient.Text("second")), Settings, Limits, events, "learn");
        Assert.Equal("first", (await observed.GetResponseAsync([new(ChatRole.User, "first input")])).Text);
        Assert.Equal("second", (await observed.GetResponseAsync([new(ChatRole.User, "next input")])).Text);
        Assert.Equal(4, observed.ModelRequests);
    }

    [Fact]
    public async Task Recovery_after_a_successful_write_does_not_replay_that_write()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        var fake = new ScriptedChatClient(
            ScriptedChatClient.Call("write", "remember", new() { ["path"] = "/facts/recovery", ["description"] = "Stored facts for this test.", ["content"] = (string[])["stored once"] }),
            Loop(), ScriptedChatClient.Text("Learned."));
        var result = await new AgentEpisode(_ => fake).RunAsync(new(EpisodePhase.Learn, "Input"), session, Settings, Limits, events, timeout.Token);
        Assert.Equal("Learned.", result.FinalText);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(3, result.ModelRequests);
        Assert.Single(events.Items, e => e.Kind == "tool-intent");
        Assert.Contains("stored once", await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/recovery" }, timeout.Token));
        Assert.Equal(fake.Requests[1].Messages.Length, fake.Requests[2].Messages.Length);
        Assert.DoesNotContain(fake.Requests[2].Messages.SelectMany(m => m.Contents), c => c is TextReasoningContent);
    }
}
