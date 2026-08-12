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

    // ── The write surface (StoreAsync / AppendAsync) ────────────────────────────

    /// <summary>
    /// <c>StoreAsync</c> is a NEW code path alongside <c>WriteArtifactAsync</c>, and the tests above
    /// name that method specifically — so every guarantee it was carrying (throw on non-2xx, the
    /// server's error body in the message, the version-header check underneath) would have been
    /// free to go missing here while all of them stayed green. It shares
    /// <c>PostCheckedAsync</c> with <c>WriteArtifactAsync</c> today, and asserting on it directly is
    /// what keeps that true: a future implementation that inlines its own send loop has to satisfy
    /// this too.
    /// </summary>
    [Fact]
    public async Task StoreAsync_NonTransientFailureWithBody_ThrowsAndCarriesTheBody()
    {
        const string serverMessage = "Permission 'store' required.";
        using var store = MakeStore(HttpStatusCode.Forbidden, out _, body: serverMessage);

        var act = async () => await store.StoreAsync(
            new Core.Models.MemoryWrite("note", "local", ["the body"]));

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.Message.Should().Contain(serverMessage);
    }

    /// <inheritdoc cref="StoreAsync_NonTransientFailureWithBody_ThrowsAndCarriesTheBody"/>
    [Fact]
    public async Task AppendAsync_NonTransientFailureWithBody_ThrowsAndCarriesTheBody()
    {
        const string serverMessage = "Permission 'append' required.";
        using var store = MakeStore(HttpStatusCode.Forbidden, out _, body: serverMessage);

        var act = async () => await store.AppendAsync(
            new Core.Models.MemoryWrite("note", "local", ["the body"]));

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.Message.Should().Contain(serverMessage);
    }

    /// <summary>
    /// A 2xx with no entry in it must throw rather than yield an <c>ArtifactEntry</c> this side
    /// invented. Completing the entry locally is precisely the derivation this store may not do, and
    /// the fabricated result — zero bytes, no keywords, no chunk entries — would be indistinguishable
    /// from a real memory that happens to be empty, then get persisted by whatever consumed it.
    /// </summary>
    [Fact]
    public async Task StoreAsync_SuccessWithNoEntryInTheBody_Throws()
    {
        using var store = MakeStore(HttpStatusCode.OK, out _, body: "{}");

        var act = async () => await store.StoreAsync(
            new Core.Models.MemoryWrite("note", "local", ["the body"]));

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("returned no entry",
                because: "the alternative is fabricating one, which is the derivation this store " +
                         "exists not to do");
    }
}
