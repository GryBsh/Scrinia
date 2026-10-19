using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class RuntimeEpisodeTests
{
    [Fact]
    public async Task Explicit_instructions_images_and_exact_tools_reach_the_sdk_without_V1_prompt_selection()
    {
        using var temp = new ScratchDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, timeout.Token);
        byte[] image = [137, 80, 78, 71, 13, 10, 26, 10, 42];
        var sidecar = Path.Combine(paths.Evidence, "synthetic.png");
        await File.WriteAllBytesAsync(sidecar, image, timeout.Token);
        var fake = new ScriptedChatClient(ScriptedChatClient.Text("saved"));
        var result = await new RuntimeAgentEpisode(_ => fake).RunAsync(new("learn", "Use only these explicit image instructions.",
            [new(ChatRole.User, [new TextContent("Synthetic image"), new DataContent(image, "image/png")])],
            new HashSet<string>(["remember", "recall"], StringComparer.Ordinal), true, true, null),
            session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, timeout.Token);
        Assert.Equal("saved", result.FinalText);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("Use only these explicit image instructions.", request.Options!.Instructions);
        Assert.Equal(["recall", "remember"], request.Options.Tools!.Select(t => t.Name).Order(StringComparer.Ordinal));
        var actualImage = Assert.Single(request.Messages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Equal(image, actualImage.Data.ToArray());
        var content = Assert.Single(events.Items, e => e.Kind == "model-request").Data
            .GetProperty("messages")[0].GetProperty("content")[1];
        Assert.Equal("image", content.GetProperty("kind").GetString());
        Assert.Equal("image/png", content.GetProperty("mediaType").GetString());
        Assert.Equal(image.Length, content.GetProperty("byteCount").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sidecar, timeout.Token))),
            content.GetProperty("sha256").GetString());
        Assert.DoesNotContain(Convert.ToBase64String(image), content.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_allowed_tools_fail_before_creating_an_inference_client()
    {
        using var temp = new ScratchDirectory();
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, CancellationToken.None);
        bool created = false;
        var runner = new RuntimeAgentEpisode(_ => { created = true; return new ScriptedChatClient(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(new("ingest", "Explicit instructions",
            [new(ChatRole.User, "input")], new HashSet<string>(["not_a_tool"]), true, true, null),
            session, ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None));
        Assert.False(created);
        Assert.DoesNotContain(events.Items, e => e.Kind == "model-request");
    }

    [Fact]
    public async Task Rejected_tool_call_never_mutates_memory_or_records_completed_dispatch()
    {
        using var temp = new ScratchDirectory();
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, CancellationToken.None);
        var fake = new ScriptedChatClient(ScriptedChatClient.Call("write", "remember", new()
        { ["path"] = "/facts/blocked", ["description"] = "Blocked synthetic fact", ["content"] = (string[])["must never be stored"] }),
            ScriptedChatClient.Text("unexpected"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new RuntimeAgentEpisode(_ => fake).RunAsync(
            new("ingest", "Explicit instructions", [new(ChatRole.User, "input")], new HashSet<string>(["remember"]), true, true,
                (_, _) => throw new InvalidDataException("Rejected tool arguments.")), session,
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None));
        Assert.Single(fake.Requests);
        Assert.DoesNotContain(events.Items, e => e.Kind is "tool-intent" or "tool-result");
        Assert.DoesNotContain("must never be stored", await ScriniaMcpSessionTests.Call(session, "recall",
            new() { ["path"] = "/facts/blocked" }, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_recovery_policy_applies_to_arbitrary_phase_names(bool recover)
    {
        using var temp = new ScratchDirectory();
        var settings = ScriniaMcpSessionTests.Payload();
        var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, settings);
        await using var events = new CapturingEvents();
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, CancellationToken.None);
        var truncated = ScriptedChatClient.Call("partial", "remember", []);
        truncated.FinishReason = ChatFinishReason.Length;
        var fake = new ScriptedChatClient(truncated, ScriptedChatClient.Text("recovered"));
        var runner = new RuntimeAgentEpisode(_ => fake);
        var input = new RuntimeEpisodeRequest("ingest", "Explicit instructions", [new(ChatRole.User, "input")],
            new HashSet<string>(), recover, null, null);
        if (recover) Assert.Equal("recovered", (await runner.RunAsync(input, session, ObservedChatClientTests.Settings,
            ObservedChatClientTests.Limits, events, CancellationToken.None)).FinalText);
        else await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(input, session,
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None));
        Assert.Equal(recover ? 2 : 1, fake.Requests.Count);
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 20)]
    [InlineData(null, 20)]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public async Task Thinking_is_explicit_on_the_wire_and_V1_null_omits_it(bool? thinking, int? topK)
    {
        string variable = "OFFLINE_RUNTIME_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "offline-placeholder");
        try
        {
            using var handler = new OfflineHandler();
            using var http = new HttpClient(handler);
            await using var events = new CapturingEvents();
            var settings = ObservedChatClientTests.Settings with { ApiKeyEnvironmentVariable = variable, TopK = topK };
            using var temp = new ScratchDirectory();
            var mcp = ScriniaMcpSessionTests.Payload();
            var paths = new AttemptWorkspace().Create(Path.Combine(temp.Root, "run"), 1, 1, mcp);
            await using var session = await ScriniaMcpSession.OpenAsync(mcp, paths, events, CancellationToken.None);
            var runner = new RuntimeAgentEpisode(_ => ModelClientFactory.Create(settings, http));
            await runner.RunAsync(new("ingest", "Explicit instructions",
                [new(ChatRole.User, [new TextContent("synthetic input"), new DataContent((byte[])[1, 2, 3], "image/png")])],
                new HashSet<string>(), true, thinking, null), session, settings,
                ObservedChatClientTests.Limits, events, CancellationToken.None);
            using var body = JsonDocument.Parse(handler.Body!);
            Assert.Contains("data:image/png;base64,AQID", handler.Body!, StringComparison.Ordinal);
            if (topK is { } k) Assert.Equal(k, body.RootElement.GetProperty("top_k").GetInt32());
            else Assert.False(body.RootElement.TryGetProperty("top_k", out _));
            if (thinking is { } enabled)
                Assert.Equal(enabled, body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            else Assert.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task Transport_and_one_bad_completion_share_five_attempts_and_the_episode_budget()
    {
        await using var events = new CapturingEvents();
        var failure = new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
        var script = Enumerable.Range(0, 8).SelectMany(_ => new object[]
            { failure, ReasoningLoopRecoveryTests.Loop(), failure, failure, ScriptedChatClient.Text("recovered") }).ToArray();
        var fake = new ScriptedChatClient(script.Concat([ScriptedChatClient.Text("must not run")]).ToArray());
        using var observed = new ObservedChatClient(fake, ObservedChatClientTests.Settings, new(40, 300, 3600, 5),
            events, "ingest", timeProvider: new ImmediateTimeProvider(), enableResponseRecovery: true);
        for (int i = 0; i < 8; i++) Assert.Equal("recovered", (await observed.GetResponseAsync([new(ChatRole.User, "input")])).Text);
        await Assert.ThrowsAsync<EpisodeLimitException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(40, observed.ModelRequests);
        Assert.Equal(40, fake.Requests.Count);
        Assert.Equal(Enumerable.Repeat((int[])[1, 2, 3, 4, 5], 8).SelectMany(x => x),
            events.Items.Where(e => e.Kind == "model-request").Select(e => e.Data.GetProperty("attempt").GetInt32()));
        Assert.Equal(8, events.Items.Count(e => e.Kind == "reasoning-loop" && e.Data.GetProperty("retry").GetBoolean()));
    }

    [Fact]
    public async Task Fifth_shared_attempt_cannot_start_a_second_response_recovery()
    {
        await using var events = new CapturingEvents();
        var failure = new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable);
        var fake = new ScriptedChatClient(failure, ReasoningLoopRecoveryTests.Loop(), failure, failure,
            ReasoningLoopRecoveryTests.Loop(), ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, ObservedChatClientTests.Settings, new(40, 300, 3600, 5),
            events, "ingest", timeProvider: new ImmediateTimeProvider(), enableResponseRecovery: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(5, observed.ModelRequests);
        Assert.Equal([true, false], events.Items.Where(e => e.Kind == "reasoning-loop").Select(e => e.Data.GetProperty("retry").GetBoolean()));
    }

    [Fact]
    public async Task Cancellation_during_transport_backoff_emits_no_later_request()
    {
        await using var events = new CapturingEvents();
        using var cancellation = new CancellationTokenSource();
        var fake = new ScriptedChatClient(new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable),
            ScriptedChatClient.Text("must not run"));
        using var observed = new ObservedChatClient(fake, ObservedChatClientTests.Settings, new(40, 300, 3600, 5),
            events, "ingest", timeProvider: new CancellingTimeProvider(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observed.GetResponseAsync(
            [new(ChatRole.User, "input")], cancellationToken: cancellation.Token));
        Assert.Single(fake.Requests);
        Assert.Single(events.Items, e => e.Kind == "model-request");
    }

    private sealed class CancellingTimeProvider(CancellationTokenSource cancellation) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            cancellation.Cancel();
            return System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(
                """{"id":"offline","object":"chat.completion","created":1,"model":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""",
                System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
