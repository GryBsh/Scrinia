using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class HttpMemoryStoreWriteFailureTests
{
    /// <summary>Test handler that returns a fixed status for every request.</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
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
            return new HttpResponseMessage(status);
        }
    }

    private static HttpMemoryStore MakeStore(HttpStatusCode status, out FixedStatusHandler handler)
    {
        handler = new FixedStatusHandler(status);
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
}
