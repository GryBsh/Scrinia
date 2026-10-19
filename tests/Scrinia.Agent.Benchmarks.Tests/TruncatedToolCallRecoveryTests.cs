using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class TruncatedToolCallRecoveryTests
{
    private static ModelSettings Settings => ObservedChatClientTests.Settings with
        { MaxOutputTokens = 16384, TopP = .95f, TopK = 20 };
    private static RunLimits Limits => new(8, 300, 3600, 5);

    internal static ChatResponse Truncated() => new(new ChatMessage(ChatRole.Assistant,
        [new TextContent("I should correct that assumption."),
         new TextReasoningContent("The user did not confirm this fact."),
         new FunctionCallContent("partial", "remember", null)]))
    {
        FinishReason = ChatFinishReason.Length, ModelId = "reported-fixture-model",
        Usage = new() { InputTokenCount = 100, OutputTokenCount = 16384 }
    };

    [Theory]
    [InlineData("{\"path\":\"/facts/test\",\"content\":[\"unfinished")]
    [InlineData("{ \"content\": [\"complete\"], \"path\": \"/facts/test\" }")]
    public async Task Wire_arguments_and_parse_errors_survive_normalization(string raw)
    {
        string variable = "BENCH_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "offline-placeholder");
        try
        {
            using var handler = new ToolResponseHandler(raw);
            using var http = new HttpClient(handler);
            var settings = Settings with { ApiKeyEnvironmentVariable = variable };
            await using var events = new CapturingEvents();
            using var observed = new ObservedChatClient(ModelClientFactory.Create(settings, http), settings,
                Limits with { MaxModelAttempts = 1 }, events, "learn");
            await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
            var response = Assert.Single(events.Items, e => e.Kind == "model-response").Data;
            var call = response.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
                .Single(c => c.GetProperty("kind").GetString() == "function-call");
            Assert.Equal(raw, call.GetProperty("rawArguments").GetString());
            if (raw.EndsWith('}'))
            {
                Assert.Equal("/facts/test", call.GetProperty("arguments").GetProperty("path").GetString());
                Assert.Equal(JsonValueKind.Null, call.GetProperty("parseError").ValueKind);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, call.GetProperty("arguments").ValueKind);
                Assert.Equal("System.InvalidOperationException", call.GetProperty("parseError").GetProperty("type").GetString());
                var cause = call.GetProperty("parseError").GetProperty("innerError");
                Assert.Contains("Json", cause.GetProperty("type").GetString());
                Assert.True(cause.GetProperty("bytePositionInLine").GetInt64() > 0);
                Assert.False(string.IsNullOrWhiteSpace(call.GetProperty("parseError").GetProperty("message").GetString()));
            }
            Assert.DoesNotContain("offline-placeholder", response.GetRawText());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task Retry_preserves_history_settings_and_counts_all_usage()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(Truncated(), ScriptedChatClient.Text("Learned.",
            new() { InputTokenCount = 100, OutputTokenCount = 3 }));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        var result = await observed.GetResponseAsync([new(ChatRole.User, "Historical input")],
            new() { Instructions = "Store facts.", ConversationId = "discard" });
        Assert.Equal("Learned.", result.Text);
        Assert.Equal(new UsageTotals(200, 16387), observed.Usage);
        Assert.Equal(2, observed.ModelRequests);
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
        Assert.True(Assert.Single(events.Items, e => e.Kind == "truncated-tool-call").Data.GetProperty("retry").GetBoolean());
        Assert.Equal(2, events.Items.Count(e => e.Kind == "model-response"));
        Assert.DoesNotContain(events.Items, e => e.Kind is "reasoning-loop" or "model-error");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Persistent_truncation_or_alternating_loop_gets_only_one_response_retry(bool loop, bool loopFirst)
    {
        await using var events = new CapturingEvents();
        using var observed = new ObservedChatClient(new ScriptedChatClient(loopFirst ? ReasoningLoopRecoveryTests.Loop() : Truncated(),
            loop && !loopFirst ? ReasoningLoopRecoveryTests.Loop() : Truncated(), ScriptedChatClient.Text("must not run")),
            Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(2, observed.ModelRequests);
        Assert.Equal([true, false], events.Items.Where(e => e.Kind is "truncated-tool-call" or "reasoning-loop")
            .Select(e => e.Data.GetProperty("retry").GetBoolean()));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(8, 1)]
    public async Task Retry_respects_existing_request_and_attempt_limits(int turns, int attempts)
    {
        await using var events = new CapturingEvents();
        using var observed = new ObservedChatClient(new ScriptedChatClient(Truncated(), ScriptedChatClient.Text("must not run")),
            Settings, Limits with { MaxModelTurnsPerEpisode = turns, MaxModelAttempts = attempts }, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(1, observed.ModelRequests);
        Assert.False(Assert.Single(events.Items, e => e.Kind == "truncated-tool-call").Data.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public async Task Endpoint_retries_share_attempt_limit_with_truncated_call_recovery()
    {
        await using var events = new CapturingEvents();
        using var observed = new ObservedChatClient(new ScriptedChatClient(
            new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable),
            Truncated(), ScriptedChatClient.Text("must not run")),
            Settings, Limits with { MaxModelAttempts = 2 }, events, "learn", timeProvider: new ImmediateTimeProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(2, observed.ModelRequests);
        Assert.False(Assert.Single(events.Items, e => e.Kind == "truncated-tool-call").Data.GetProperty("retry").GetBoolean());
    }

    [Theory]
    [InlineData("model-response")]
    [InlineData("truncated-tool-call")]
    public async Task Evidence_failure_prevents_retry(string kind)
    {
        await using var events = new CapturingEvents { FailOnceOnKind = kind };
        using var observed = new ObservedChatClient(new ScriptedChatClient(Truncated(), ScriptedChatClient.Text("must not run")),
            Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<IOException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(1, observed.ModelRequests);
    }

    [Fact]
    public async Task Cancellation_prevents_retry()
    {
        await using var events = new CapturingEvents();
        using var cancellation = new CancellationTokenSource();
        var fake = new ScriptedChatClient(Truncated(), ScriptedChatClient.Text("must not run")) { OnRequest = cancellation.Cancel };
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observed.GetResponseAsync(
            [new(ChatRole.User, "input")], cancellationToken: cancellation.Token));
        Assert.Equal(1, observed.ModelRequests);
    }

    [Fact]
    public async Task Model_drift_prevents_retry()
    {
        await using var events = new CapturingEvents();
        using var observed = new ObservedChatClient(new ScriptedChatClient(Truncated()), Settings, Limits, events, "learn", "different");
        var error = await Assert.ThrowsAsync<BenchmarkFailureException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(BenchmarkFailureCode.MODEL_DRIFT, error.Code);
        Assert.DoesNotContain(events.Items, e => e.Kind == "truncated-tool-call");
    }

    [Theory]
    [InlineData("informational")]
    [InlineData("tool-result")]
    [InlineData("non-assistant")]
    [InlineData("judge")]
    public async Task Responses_without_proof_of_undispatched_calls_are_not_retried(string kind)
    {
        await using var events = new CapturingEvents();
        var response = Truncated();
        if (kind == "informational") response.Messages[0].Contents.OfType<FunctionCallContent>().Single().InformationalOnly = true;
        if (kind == "tool-result") response.Messages[0].Contents.Add(new FunctionResultContent("partial", "already done"));
        if (kind == "non-assistant") response.Messages[0].Role = ChatRole.Tool;
        using var observed = new ObservedChatClient(new ScriptedChatClient(response), Settings, Limits, events, kind == "judge" ? "judge" : "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(1, observed.ModelRequests);
        Assert.DoesNotContain(events.Items, e => e.Kind == "truncated-tool-call");
    }

    [Fact]
    public async Task Real_tool_dispatch_excludes_partial_batch_and_does_not_replay_prior_write()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        var partial = Truncated();
        partial.Messages[0].Contents.Add(new FunctionCallContent("complete-in-batch", "remember",
            new Dictionary<string, object?> { ["path"] = "/facts/partial", ["description"] = "Must not be stored.", ["content"] = (string[])["partial batch"] }));
        var fake = new ScriptedChatClient(
            ScriptedChatClient.Call("prior-write", "remember", new() { ["path"] = "/facts/recovery", ["description"] = "Recovery test.", ["content"] = (string[])["stored once"] }),
            partial,
            ScriptedChatClient.Call("replacement", "remember", new() { ["path"] = "/facts/recovery", ["description"] = "Recovery test.", ["content"] = (string[])["corrected once"] }),
            ScriptedChatClient.Text("Learned."));
        var result = await new AgentEpisode(_ => fake).RunAsync(new(EpisodePhase.Learn, "input"),
            session, Settings, Limits, events, timeout.Token);
        Assert.Equal("Learned.", result.FinalText);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(4, result.ModelRequests);
        Assert.Equal(2, events.Items.Count(e => e.Kind == "tool-intent"));
        string recalled = await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/recovery" }, timeout.Token);
        Assert.Equal(1, recalled.Split("stored once").Length - 1);
        Assert.Equal(1, recalled.Split("corrected once").Length - 1);
        Assert.Contains("NOT_FOUND", await ScriniaMcpSessionTests.Call(session, "recall", new() { ["path"] = "/facts/partial" }, timeout.Token));
        Assert.Equal(fake.Requests[1].Messages.Length, fake.Requests[2].Messages.Length);
        Assert.DoesNotContain(fake.Requests[2].Messages.SelectMany(m => m.Contents),
            c => c is FunctionCallContent call && call.CallId == "partial");
    }

    private sealed class ToolResponseHandler(string raw) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = JsonSerializer.Serialize(new
            {
                id = "offline", @object = "chat.completion", created = 1, model = "fixture",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "Correcting a memory.",
                    tool_calls = new[] { new { id = "partial", type = "function", function = new { name = "remember", arguments = raw } } } },
                    finish_reason = "length" } },
                usage = new { prompt_tokens = 100, completion_tokens = 16384, total_tokens = 16484 }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
