using System.Net;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ObservedChatClientTests
{
    [Fact]
    public async Task Usage_sums_known_values_and_stays_unknown_after_missing_usage()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(
            ScriptedChatClient.Text("one", new() { InputTokenCount = 7, OutputTokenCount = 2 }),
            ScriptedChatClient.Text("two", new() { InputTokenCount = 3, OutputTokenCount = 1 }),
            ScriptedChatClient.Text("unknown"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await observed.GetResponseAsync([new(ChatRole.User, "input")]);
        await observed.GetResponseAsync([new(ChatRole.User, "input")]);
        Assert.Equal(new UsageTotals(10, 3), observed.Usage);
        await observed.GetResponseAsync([new(ChatRole.User, "input")]);
        Assert.Equal(new UsageTotals(null, null), observed.Usage);
    }

    internal static ModelSettings Settings => new(new Uri("http://localhost:1234/v1"), "configured-model", "BENCH_AGENT_API_KEY", 0.25f, 333);
    internal static RunLimits Limits => new(4, 30, 60, 2);

    [Fact]
    public void Budget_refuses_a_request_before_overrun()
    {
        var budget = new EpisodeBudget(1);
        budget.BeforeModelRequest();
        Assert.Throws<EpisodeLimitException>(() => budget.BeforeModelRequest());
    }

    [Fact]
    public async Task Applies_options_and_preserves_unknown_usage_and_clears_service_context()
    {
        await using var events = new CapturingEvents();
        var response = ScriptedChatClient.Text("answer");
        response.ConversationId = "server-conversation";
        var fake = new ScriptedChatClient(response);
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "answer");
        var actual = await observed.GetResponseAsync([new(ChatRole.User, "question")], new() { ConversationId = "prior" });
        Assert.Null(actual.ConversationId);
        Assert.Null(observed.Usage.InputTokens);
        Assert.Null(observed.Usage.OutputTokens);
        var request = Assert.Single(fake.Requests);
        Assert.Null(request.Options!.ConversationId);
        Assert.Equal(Settings.Model, request.Options.ModelId);
        Assert.Equal(Settings.Temperature, request.Options.Temperature);
        Assert.Equal(Settings.MaxOutputTokens, request.Options.MaxOutputTokens);
        Assert.Equal(1, observed.ModelRequests);
        Assert.Contains(events.Items, e => e.Kind == "model-response" && e.Data.GetRawText().Contains("reported-fixture-model", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Retries_only_declared_transient_statuses_and_counts_actual_attempts(int status)
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(new HttpRequestException("do not log provider secret", null, (HttpStatusCode)status), ScriptedChatClient.Text("done"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        Assert.Equal("done", (await observed.GetResponseAsync([new(ChatRole.User, "input")])).Text);
        Assert.Equal(2, observed.ModelRequests);
        Assert.Equal(2, fake.Requests.Count);
        Assert.DoesNotContain(events.Items, e => e.Data.GetRawText().Contains("do not log provider secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Does_not_retry_unsupported_context_or_authentication_errors(int status)
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(new HttpRequestException("provider rejected", null, (HttpStatusCode)status), ScriptedChatClient.Text("unexpected"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<HttpRequestException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Retry_exhaustion_and_request_budget_are_independent()
    {
        await using var events = new CapturingEvents();
        var failure = new HttpRequestException("retry", null, HttpStatusCode.TooManyRequests);
        var fake = new ScriptedChatClient(failure, failure, ScriptedChatClient.Text("unexpected"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<HttpRequestException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Equal(2, fake.Requests.Count);
        var budgetFake = new ScriptedChatClient(failure, ScriptedChatClient.Text("unexpected"));
        using var budgeted = new ObservedChatClient(budgetFake, Settings, Limits with { MaxModelTurnsPerEpisode = 1 }, events, "learn");
        await Assert.ThrowsAsync<EpisodeLimitException>(() => budgeted.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Single(budgetFake.Requests);
    }

    [Fact]
    public async Task Cancellation_interrupts_retry_delay()
    {
        await using var events = new CapturingEvents();
        using var cancelled = new CancellationTokenSource();
        var fake = new ScriptedChatClient(new HttpRequestException("retry", null, HttpStatusCode.ServiceUnavailable), ScriptedChatClient.Text("unexpected"))
            { OnRequest = () => cancelled.Cancel() };
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")], cancellationToken: cancelled.Token));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Recording_failure_prevents_provider_dispatch()
    {
        await using var events = new CapturingEvents { Fail = true };
        var fake = new ScriptedChatClient(ScriptedChatClient.Text("unexpected"));
        using var observed = new ObservedChatClient(fake, Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<IOException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Truncation_is_rejected_before_the_sdk_can_dispatch_tools()
    {
        await using var events = new CapturingEvents();
        var response = ScriptedChatClient.Call("id", "remember", []);
        response.FinishReason = ChatFinishReason.Length;
        using var observed = new ObservedChatClient(new ScriptedChatClient(response), Settings, Limits, events, "learn");
        await Assert.ThrowsAsync<InvalidDataException>(() => observed.GetResponseAsync([new(ChatRole.User, "input")]));
    }
}
