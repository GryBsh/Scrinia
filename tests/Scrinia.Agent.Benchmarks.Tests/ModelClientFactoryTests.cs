using System.Net;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ModelClientFactoryTests
{
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
