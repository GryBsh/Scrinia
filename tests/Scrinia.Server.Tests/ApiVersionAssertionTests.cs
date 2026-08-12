using System.Net;
using System.Text;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;

namespace Scrinia.Server.Tests;

/// <summary>
/// Pins the fail-closed contract from remote-write-correctness Task 7: <c>HttpMemoryStore</c> must
/// refuse a response that doesn't carry a recognised <see cref="RemoteApiVersion.HeaderName"/>
/// value, whether the header is present-but-wrong or missing entirely. Absent must fail closed too
/// — a server that simply forgets to stamp the header must not silently reopen the hole that an
/// old/unversioned server represents (it would ignore filter params like excludeTopics/pathPrefix
/// instead of rejecting them, so the client would present an unfiltered result set as filtered).
///
/// <para>Each case drives every guarded send call site <c>HttpMemoryStore</c> has — <see
/// cref="HttpMemoryStore.LoadIndex"/> (the one synchronous path, via <c>HttpClient.Send</c>),
/// <see cref="HttpMemoryStore.ResolveArtifactAsync"/> (its own <c>RetryPolicy.ExecuteAsync</c> and
/// its own <c>Validate</c> call, not routed through anything else), and the write surface —
/// <see cref="HttpMemoryStore.WriteArtifactAsync"/>, <see cref="HttpMemoryStore.StoreAsync"/> and
/// <see cref="HttpMemoryStore.AppendAsync"/>, which share the <c>PostCheckedAsync</c> helper —
/// in one test, so a check missing from any one of them cannot pass this suite while leaving that
/// site unguarded. The three write members are asserted individually rather than trusting the
/// shared helper: a future implementation that stops sharing it must still satisfy this.
/// <see cref="HttpMemoryStore.TryUpdateEntry"/> is deliberately absent — both of its sends go
/// through <c>SendSync</c>, the same call site <c>LoadIndex</c> exercises here.</para>
///
/// <para>Uses an in-process stub handler rather than a real Kestrel server (contrast
/// <c>RemoteLoadIndexTests</c>/<c>StoreConformanceFixture</c>): the real server always stamps a
/// matching header, so the only way to exercise "wrong" or "absent" is to control the header
/// directly.</para>
/// </summary>
public sealed class ApiVersionAssertionTests
{
    /// <summary>What a write endpoint answers with: the complete entry plus a message. Shared by
    /// the store and append responses, which are the same shape.</summary>
    private const string WriteResponseJson = """
        {"entry":{"name":"note","qualifiedName":"note","scope":"local","chunkCount":1,
        "originalBytes":11,"createdAt":"2026-01-01T00:00:00+00:00","updatedAt":null,
        "description":"a note","attributes":null,"keywords":null,"termFrequencies":null,
        "chunkEntries":null,"fileHashes":null},"message":"Remembered: note"}
        """;

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
            // Each path needs a body its caller can actually parse, or the matching-version case
            // would throw from JSON/NMP2 decoding instead of exercising the version guard at all:
            //   GET  .../memories        → an empty ListApiResponse (LoadIndex)
            //   POST .../memories        → a StoreApiResponse carrying a complete entry
            //                              (WriteArtifactAsync reads only the status; StoreAsync
            //                              refuses a 2xx that carries no entry)
            //   POST .../memories/x/append → an AppendApiResponse, same shape
            //   GET  .../memories/{name} → a ShowApiResponse (ResolveArtifactAsync's Show lookup)
            string path = request.RequestUri!.AbsolutePath;
            bool isMemoriesCollection = path.EndsWith("/memories", StringComparison.Ordinal);
            string body =
                request.Method == HttpMethod.Post
                    && (isMemoriesCollection || path.EndsWith("/append", StringComparison.Ordinal))
                    ? WriteResponseJson
                : isMemoriesCollection
                    ? """{"memories":[],"total":0}"""
                    : """{"name":"note","content":["hello world"],"originalBytes":11}""";

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

    /// <summary>One chunk, so the same value serves both StoreAsync and AppendAsync (an append
    /// refuses any other count).</summary>
    private static Scrinia.Core.Models.MemoryWrite Write() => new("note", "local", ["hello world"]);

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

        var storeAct = async () => await store.StoreAsync(Write());
        (await storeAct.Should().ThrowAsync<RemoteApiVersionMismatchException>())
            .Which.Message.Should().Contain("999");

        var appendAct = async () => await store.AppendAsync(Write());
        (await appendAct.Should().ThrowAsync<RemoteApiVersionMismatchException>())
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

        var storeAct = async () => await store.StoreAsync(Write());
        await storeAct.Should().ThrowAsync<RemoteApiVersionMismatchException>();

        var appendAct = async () => await store.AppendAsync(Write());
        await appendAct.Should().ThrowAsync<RemoteApiVersionMismatchException>();
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

        var storeAct = async () => await store.StoreAsync(Write());
        await storeAct.Should().NotThrowAsync();

        var appendAct = async () => await store.AppendAsync(Write());
        await appendAct.Should().NotThrowAsync();
    }
}
