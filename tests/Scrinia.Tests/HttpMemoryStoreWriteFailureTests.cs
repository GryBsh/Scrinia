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
}
