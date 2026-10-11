using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class AgentEpisodeTests
{
    [Fact]
    public async Task Tool_completion_evidence_failure_is_recorded_and_stops_without_replay()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents { FailOnceOnKind = "tool-result" };
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        var fake = new ScriptedChatClient(ScriptedChatClient.Call("write", "remember", new() { ["path"] = "/facts/retained", ["content"] = (string[])["committed before evidence failure"] }), ScriptedChatClient.Text("unexpected"));
        await Assert.ThrowsAsync<IOException>(() => new AgentEpisode(_ => fake).RunAsync(new(EpisodePhase.Learn, "Input"), session,
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token));
        Assert.Single(fake.Requests);
        Assert.Contains(events.Items, e => e.Kind == "tool-error");
    }

    [Fact]
    public async Task Sdk_dispatches_real_tools_and_answering_starts_without_learning_context()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        var learningClient = new ScriptedChatClient(
            ScriptedChatClient.Call("guide", "guide", []),
            ScriptedChatClient.Call("remember", "remember", new() { ["path"] = "/facts/favorite", ["content"] = (string[])["Favorite color is blue."] }, "I will save that."),
            ScriptedChatClient.Text("Learned."));
        var input = AgentInstructions.Learn(new(1, "2024-06-13", [new("assistant", "LEARNING_ONLY Favorite color is blue.")]));
        await using (var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token))
        {
            var result = await new AgentEpisode(_ => learningClient).RunAsync(input, session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
            Assert.Equal("Learned.", result.FinalText);
            Assert.Equal(2, result.ToolCalls);
            Assert.Equal(3, result.ModelRequests);
            Assert.Contains("Favorite color is blue.", await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/favorite" }, timeout.Token));
        }
        Assert.True(learningClient.Disposed);
        var answerClient = new ScriptedChatClient(ScriptedChatClient.Text("Blue."));
        await using (var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token))
        {
            var result = await new AgentEpisode(_ => answerClient).RunAsync(AgentInstructions.Answer(new("What is my favorite color?", "2024-06-15")),
                session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
            Assert.Equal("Blue.", result.FinalText);
            Assert.DoesNotContain(answerClient.Requests[0].Messages, m => m.Text.Contains("LEARNING_ONLY", StringComparison.Ordinal));
            Assert.Equal(0, result.ToolCalls);
            var noWrites = new ScriptedChatClient(ScriptedChatClient.Text("Nothing useful to retain."));
            var learned = await new AgentEpisode(_ => noWrites).RunAsync(input, session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
            Assert.Equal(0, learned.ToolCalls);
            Assert.Equal("Nothing useful to retain.", learned.FinalText);
        }
    }

    [Fact]
    public async Task Ordinary_tool_error_is_visible_and_the_agent_can_correct_it()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        var fake = new ScriptedChatClient(
            ScriptedChatClient.Call("missing", "recall", new() { ["path"] = "/missing" }),
            ScriptedChatClient.Call("corrected", "remember", new() { ["path"] = "/facts/corrected", ["content"] = (string[])["Retained after error."] }),
            ScriptedChatClient.Text("done"));
        var result = await new AgentEpisode(_ => fake).RunAsync(new(EpisodePhase.Learn, "Test conversation"),
            session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
        Assert.Equal(1, result.ToolErrors);
        Assert.Equal(2, result.ToolCalls);
        Assert.Contains(events.Items, e => e.Kind == "tool-result" && e.Data.GetRawText().Contains("status: error", StringComparison.Ordinal));
        Assert.Contains("Retained after error.", await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/corrected" }, timeout.Token));
    }

    [Fact]
    public async Task Repeated_calls_stop_at_budget_and_truncated_calls_never_mutate_memory()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        var repeated = new ScriptedChatClient(ScriptedChatClient.Call("one", "guide", []), ScriptedChatClient.Call("two", "guide", []));
        await Assert.ThrowsAsync<EpisodeLimitException>(() => new AgentEpisode(_ => repeated).RunAsync(new(EpisodePhase.Learn, "Input"), session,
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits with { MaxModelTurnsPerEpisode = 1 }, events, timeout.Token));
        Assert.Single(repeated.Requests);
        var truncated = ScriptedChatClient.Call("never", "remember", new() { ["path"] = "/facts/never", ["content"] = (string[])["MUST_NOT_BE_STORED"] });
        truncated.FinishReason = ChatFinishReason.Length;
        await Assert.ThrowsAsync<InvalidDataException>(() => new AgentEpisode(_ => new ScriptedChatClient(truncated)).RunAsync(new(EpisodePhase.Learn, "Input"),
            session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token));
        Assert.DoesNotContain("MUST_NOT_BE_STORED", await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/never" }, timeout.Token));
    }
}
