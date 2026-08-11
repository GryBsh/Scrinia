using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// Pins the fail-closed contract from remote-write-correctness Task 7: <c>HttpMemoryStore</c> must
/// refuse a response that doesn't carry a recognised <see cref="RemoteApiVersion.HeaderName"/>
/// value, whether the header is present-but-wrong or missing entirely. Absent must fail closed too
/// — a server that simply forgets to stamp the header must not silently reopen the hole that an
/// old/unversioned server represents (it would ignore filter params like excludeTopics/pathPrefix
/// instead of rejecting them, so the client would present an unfiltered result set as filtered).
///
/// <para>Each case drives all three of <c>HttpMemoryStore</c>'s guarded send call sites — <see
/// cref="HttpMemoryStore.LoadIndex"/> (the one synchronous path, via <c>HttpClient.Send</c>),
/// <see cref="HttpMemoryStore.WriteArtifactAsync"/>, and <see cref="HttpMemoryStore.ResolveArtifactAsync"/>
/// (two independent asynchronous paths, each with its own <c>RetryPolicy.ExecuteAsync</c> call and
/// its own <c>Validate</c> call site — neither is a helper the other routes through) — in one
/// test, so a check missing from any one of the three cannot pass this suite while leaving that
/// site unguarded.</para>
///
/// <para>Uses an in-process stub handler rather than a real Kestrel server (contrast
/// <c>RemoteLoadIndexTests</c>/<c>StoreConformanceFixture</c>): the real server always stamps a
/// matching header, so the only way to exercise "wrong" or "absent" is to control the header
/// directly.</para>
/// </summary>
public sealed class ApiVersionAssertionTests
{
    /// <summary>Answers every request with a fixed status and, when <paramref name="headerValue"/>
    /// is non-null, the given API version header value. A null <paramref name="headerValue"/>
    /// simulates a server that never learned about the version contract at all.</summary>
    private sealed class VersionStubHandler(string? headerValue, HttpStatusCode status) : HttpMessageHandler
    {
        // Both overrides are required: LoadIndex goes through HttpMemoryStore's synchronous send
        // path (HttpClient.Send), and a handler that only overrides SendAsync throws
        // NotSupportedException from HttpMessageHandler's own Send default — see the same note in
        // HttpMemoryStoreWriteFailureTests.FixedStatusHandler and RemoteStoreToolTests.StubBackendHandler.
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            // GET/POST .../memories (LoadIndex, WriteArtifactAsync) need a body that deserializes
            // as an empty ListApiResponse (WriteArtifactAsync only reads the status, but an empty
            // list body is harmless there too). GET .../memories/{name} (ResolveArtifactAsync's
            // Show lookup) needs a ShowApiResponse-shaped body, or the matching-version case would
            // throw from JSON/NMP2 decoding instead of exercising the version guard at all.
            bool isMemoriesCollection =
                request.RequestUri!.AbsolutePath.EndsWith("/memories", StringComparison.Ordinal);
            string body = isMemoriesCollection
                ? """{"memories":[],"total":0}"""
                : """{"name":"note","content":"hello world","chunkCount":1,"originalBytes":11}""";

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (headerValue is not null)
                response.Headers.TryAddWithoutValidation(RemoteApiVersion.HeaderName, headerValue);
            return response;
        }
    }

    private static HttpMemoryStore MakeStore(string? headerValue, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new VersionStubHandler(headerValue, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        // MaxRetries: 0 — every case here is deterministic per call, nothing exercises retry/backoff.
        return new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));
    }

    private static string Artifact() => Nmp2ChunkedEncoder.Encode("hello world");

    [Fact]
    public async Task UnrecognisedVersion_SyncAndAsyncPaths_Throw()
    {
        using var store = MakeStore(headerValue: "999");

        var syncAct = () => store.LoadIndex("local");
        syncAct.Should().Throw<RemoteApiVersionMismatchException>()
            .WithMessage("*999*", because: "a caller who hits this needs to know which version " +
                         "mismatched, not just that something failed");

        var asyncWriteAct = async () => await store.WriteArtifactAsync("note", "local", Artifact());
        (await asyncWriteAct.Should().ThrowAsync<RemoteApiVersionMismatchException>())
            .Which.Message.Should().Contain("999");

        // ResolveArtifactAsync is a distinct async call site (its own RetryPolicy.ExecuteAsync,
        // not routed through WriteArtifactAsync's) — a guard present on the write path only would
        // still satisfy the assertion above while leaving this one open.
        var asyncResolveAct = async () => await store.ResolveArtifactAsync("note");
        (await asyncResolveAct.Should().ThrowAsync<RemoteApiVersionMismatchException>())
            .Which.Message.Should().Contain("999");
    }

    [Fact]
    public async Task MissingVersionHeader_SyncAndAsyncPaths_Throw()
    {
        using var store = MakeStore(headerValue: null);

        var syncAct = () => store.LoadIndex("local");
        syncAct.Should().Throw<RemoteApiVersionMismatchException>(
            because: "a server that never stamps the header must fail closed exactly like a " +
                     "server that stamps the wrong value — silence must not reopen the hole");

        var asyncWriteAct = async () => await store.WriteArtifactAsync("note", "local", Artifact());
        await asyncWriteAct.Should().ThrowAsync<RemoteApiVersionMismatchException>();

        var asyncResolveAct = async () => await store.ResolveArtifactAsync("note");
        await asyncResolveAct.Should().ThrowAsync<RemoteApiVersionMismatchException>();
    }

    [Fact]
    public async Task MatchingVersion_SyncAndAsyncPaths_DoNotThrow()
    {
        using var store = MakeStore(headerValue: RemoteApiVersion.Current);

        var syncAct = () => store.LoadIndex("local");
        syncAct.Should().NotThrow(
            because: "the guard against the check being so strict it breaks a same-version server");

        var asyncWriteAct = async () => await store.WriteArtifactAsync("note", "local", Artifact());
        await asyncWriteAct.Should().NotThrowAsync();

        var asyncResolveAct = async () => await store.ResolveArtifactAsync("note");
        await asyncResolveAct.Should().NotThrowAsync();
    }
}
