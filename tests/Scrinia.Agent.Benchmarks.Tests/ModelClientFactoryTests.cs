using System.Net;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ModelClientFactoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configured_sampling_reaches_the_wire_and_evidence(bool sampling)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        var variable = "BENCH_TEST_" + Guid.NewGuid().ToString("N");
        json["agent"]!["apiKeyEnvironmentVariable"] = variable;
        json["agent"]!["temperature"] = 0.6;
        json["agent"]!["maxOutputTokens"] = 16384;
        if (sampling)
        {
            json["agent"]!["topP"] = 0.95;
            json["agent"]!["topK"] = 20;
        }
        var settings = new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString())).Agent;
        Environment.SetEnvironmentVariable(variable, "offline-placeholder");
        try
        {
            using var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            await using var events = new CapturingEvents();
            using var observed = new ObservedChatClient(ModelClientFactory.Create(settings, http),
                settings, ObservedChatClientTests.Limits, events, "learn");
            await observed.GetResponseAsync([new(ChatRole.User, "offline request")],
                new() { TopP = 0.1f, TopK = 99, RawRepresentationFactory = _ => throw new InvalidOperationException("caller override") });
            var body = System.Text.Json.Nodes.JsonNode.Parse(handler.Body!)!;
            Assert.Equal(0.6f, body["temperature"]!.GetValue<float>());
            Assert.Equal(16384, body["max_completion_tokens"]!.GetValue<int>());
            var evidence = Assert.Single(events.Items, e => e.Kind == "model-request").Data;
            if (sampling)
            {
                Assert.Equal(0.95f, body["top_p"]!.GetValue<float>());
                Assert.Equal(20, body["top_k"]!.GetValue<int>());
                Assert.Equal(0.95f, evidence.GetProperty("topP").GetSingle());
                Assert.Equal(20, evidence.GetProperty("topK").GetInt32());
            }
            else
            {
                Assert.Null(body["top_p"]);
                Assert.Null(body["top_k"]);
                Assert.False(evidence.TryGetProperty("topP", out _));
                Assert.False(evidence.TryGetProperty("topK", out _));
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"offline","object":"chat.completion","created":1,"model":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public void Missing_named_credential_fails_before_creating_a_client()
    {
        var settings = ObservedChatClientTests.Settings with { ApiKeyEnvironmentVariable = "BENCH_MISSING_" + Guid.NewGuid().ToString("N") };
        Assert.Throws<InvalidOperationException>(() => ModelClientFactory.Create(settings));
    }

    [Fact]
    public async Task Provider_transport_has_no_hidden_retries()
    {
        var variable = "BENCH_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "offline-placeholder");
        try
        {
            using var handler = new RejectingHandler();
            using var http = new HttpClient(handler);
            using var client = ModelClientFactory.Create(ObservedChatClientTests.Settings with { ApiKeyEnvironmentVariable = variable }, http);
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new(ChatRole.User, "offline request")]));
            Assert.Equal(1, handler.Requests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("""{"error":{"message":"offline test","type":"server_error"}}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
