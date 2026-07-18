using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings.Providers;
using Xunit;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Covers the two bugs fixed in <see cref="ResilientEmbeddingProvider.EmbedAsync"/>:
/// the response returned by the retry policy must be disposed, and caller cancellation
/// (<see cref="OperationCanceledException"/> tied to the passed-in token) must propagate
/// to the caller rather than being swallowed and reported as a null embedding.
/// </summary>
public class ResilientEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsync_PropagatesCallerCancellation_InsteadOfReturningNull()
    {
        using var provider = new ThrowingProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => provider.EmbedAsync("some text", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EmbedAsync_DisposesSuccessfulResponse()
    {
        using var provider = new TrackingSuccessProvider();

        var vec = await provider.EmbedAsync("some text", CancellationToken.None);

        vec.Should().NotBeNull();
        provider.LastResponse.Should().NotBeNull();
        // If EmbedAsync fails to `using` the response, disposing it a second time here
        // would still succeed (HttpResponseMessage.Dispose is idempotent), so instead we
        // assert on the tracked disposal flag set by the response's finalizer-free Dispose
        // override below.
        provider.LastResponse!.Disposed.Should().BeTrue();
    }

    /// <summary>Test double whose request send throws a caller-cancellation exception directly,
    /// bypassing HTTP entirely — mirrors what HttpClient does when the passed token fires.</summary>
    private sealed class ThrowingProvider : ResilientEmbeddingProvider
    {
        public ThrowingProvider() : base(new HttpClient(), NullLogger.Instance) { }

        public override int Dimensions => 0;
        public override string Signature => "test:throwing";
        protected override string ProviderName => "Throwing";

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
            => throw new OperationCanceledException("caller cancelled", ct);

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
            => Task.FromResult<float[]?>(null);
    }

    /// <summary>Test double that returns a tracked, successful response so the test can
    /// assert the response gets disposed by <see cref="ResilientEmbeddingProvider.EmbedAsync"/>.</summary>
    private sealed class TrackingSuccessProvider : ResilientEmbeddingProvider
    {
        public TrackingSuccessProvider() : base(new HttpClient(), NullLogger.Instance) { }

        public TrackedResponse? LastResponse { get; private set; }

        public override int Dimensions => 1;
        public override string Signature => "test:tracking";
        protected override string ProviderName => "Tracking";

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
        {
            LastResponse = new TrackedResponse(System.Net.HttpStatusCode.OK);
            return Task.FromResult<HttpResponseMessage>(LastResponse);
        }

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct)
            => Task.FromResult<float[]?>([1f]);
    }

    private sealed class TrackedResponse(System.Net.HttpStatusCode statusCode) : HttpResponseMessage(statusCode)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
