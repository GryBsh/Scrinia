using System.Net;
using FluentAssertions;
using Scrinia.Core.Resilience;
using Scrinia.Server.Chat.Providers;

namespace Scrinia.Server.Tests;

public class ChatProviderHttpTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpClient Client(StubHandler h) =>
        new(h) { BaseAddress = new Uri("https://example.test/") };

    [Fact]
    public async Task Success_ReturnsResponseAndNoError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = Client(handler);

        var (response, error) = await ChatProviderHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Post, "v1/chat"),
            new CircuitBreaker(), new RetryOptions(), CancellationToken.None);

        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(1, "a successful send must not retry");
        response.Dispose();
    }

    [Fact]
    public async Task NonSuccess_ReturnsTruncatedErrorAndNoResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', 600)),
        });
        using var http = Client(handler);

        var (response, error) = await ChatProviderHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Post, "v1/chat"),
            new CircuitBreaker(), new RetryOptions(), CancellationToken.None);

        response.Should().BeNull("a failed response must not leak to the caller undisposed");
        error.Should().StartWith("Provider returned 400: ").And.EndWith("...");
        error!.Length.Should().Be("Provider returned 400: ".Length + 503);
    }

    [Fact]
    public async Task TransientFailure_RecordsBreakerFailure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = Client(handler);
        var breaker = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1));

        var (response, error) = await ChatProviderHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Post, "v1/chat"),
            breaker, new RetryOptions(MaxRetries: 0), CancellationToken.None);

        response.Should().BeNull();
        error.Should().NotBeNull();
        breaker.State.Should().Be(CircuitState.Open,
            because: "a transient 503 must reach CircuitBreaker.RecordFailure, and with " +
                     "Threshold: 1 a single recorded failure opens the circuit");
    }
}
