using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class HttpMemoryStoreWriteFailureTests
{
    /// <summary>Test handler that returns a fixed status (and optional body) for every request.</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Stamped());
        }

        // Both overrides are required: LoadIndex goes through SendSync, which calls the
        // synchronous HttpClient.Send. A handler that only overrides SendAsync throws
        // NotSupportedException from HttpMessageHandler's own Send default — which would look
        // exactly like the regression the LoadIndex test below exists to catch. Same note as
        // tests/Scrinia.Tests/RemoteStoreToolTests.cs's StubBackendHandler.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Stamped();
        }

        // Stamped with a matching API version header (remote-write-correctness Task 7):
        // HttpMemoryStore now fails closed on a response missing this header, and these tests are
        // about the status-code behaviour below that check, not about the check itself.
        private HttpResponseMessage Stamped()
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, RemoteApiVersion.Current);
            if (body is not null)
                response.Content = new StringContent(body);
            return response;
        }
    }

    private static HttpMemoryStore MakeStore(
        HttpStatusCode status, out FixedStatusHandler handler, string? body = null)
    {
        handler = new FixedStatusHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        // MaxRetries: 0 keeps transient-status tests fast (no backoff sleeps).
        return new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));
    }

    private static string Artifact() => Nmp2ChunkedEncoder.Encode("hello world");

    [Fact]
    public async Task WriteArtifactAsync_NonTransientFailure_Throws()
    {
        using var store = MakeStore(HttpStatusCode.Forbidden, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WriteArtifactAsync_TransientFailure_ThrowsAfterRetries()
    {
        using var store = MakeStore(HttpStatusCode.ServiceUnavailable, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task WriteArtifactAsync_Success_DoesNotThrow()
    {
        using var store = MakeStore(HttpStatusCode.OK, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Pins the deliberate design decision in HttpMemoryStore.LoadIndex (remote-write-correctness
    /// Task 6): a non-2xx response from the listing endpoint must throw, not return <c>[]</c>.
    /// Returning <c>[]</c> on failure would look exactly like "this scope has no entries" — the
    /// same lie LoadIndex told unconditionally before that task, just gated behind a failed
    /// request instead of always. Without this test, a future refactor back to <c>return []</c>
    /// on failure — the original defect, reintroduced — would leave the whole suite green.
    /// </summary>
    [Fact]
    public void LoadIndex_NonSuccessStatus_Throws()
    {
        using var store = MakeStore(HttpStatusCode.Forbidden, out _);

        var act = () => store.LoadIndex("local");

        act.Should().Throw<HttpRequestException>()
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Mirrors <see cref="LoadIndex_NonSuccessStatus_Throws"/> for the other listing entry point:
    /// <c>ListScoped</c> throws-on-failure rather than returning an empty list that would be
    /// indistinguishable from "this store has nothing in it".
    /// </summary>
    [Fact]
    public void ListScoped_NonSuccessStatus_Throws()
    {
        using var store = MakeStore(HttpStatusCode.Forbidden, out _);

        var act = () => store.ListScoped(null);

        act.Should().Throw<HttpRequestException>()
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// <c>WriteArtifactAsync</c> reads the server's error body via <c>HttpErrorBody.Truncate</c>
    /// specifically so a self-describing rejection ("attributes may not exceed N values per
    /// key") reaches the caller instead of a bare status code. The existing failure tests use a
    /// handler that never sets <c>Content</c>, so nothing pins that the body actually makes it
    /// into the thrown message.
    /// </summary>
    [Fact]
    public async Task WriteArtifactAsync_NonTransientFailureWithBody_MessageContainsBody()
    {
        const string serverMessage = "attributes may not exceed 256 values per key";
        using var store = MakeStore(HttpStatusCode.BadRequest, out _, body: serverMessage);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain(serverMessage,
                because: "the server's error body must reach the caller, not just the status code");
    }
}
