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

    [Fact]
    public async Task EmbedAsync_RejectsVectorWhoseLengthDisagreesWithProviderDimension()
    {
        using var provider = new MalformedSingleProvider([1f]);

        var vector = await provider.EmbedAsync("some text", CancellationToken.None);

        vector.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_RejectsLaterVectorWhoseLengthDiffersFromFirstVector()
    {
        using var provider = new MalformedSingleProvider([1f, 2f]);

        (await provider.EmbedAsync("first", CancellationToken.None)).Should().NotBeNull();
        (await provider.EmbedAsync("second", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task EmbedBatchAsync_UsesOneOrderedTransportRequest()
    {
        using var provider = new TrackingBatchProvider();

        var vectors = await provider.EmbedBatchAsync(["first", "second"], CancellationToken.None);

        vectors.Should().NotBeNull();
        vectors!.Should().HaveCount(2);
        vectors[0].Should().Equal(1f, 0f);
        vectors[1].Should().Equal(0f, 1f);
        provider.Requests.Should().ContainSingle().Which.Should().Equal("first", "second");
    }

    [Fact]
    public async Task EmbedBatchAsync_SplitsAtProviderLimit_AndPreservesOrder()
    {
        using var provider = new LimitedBatchProvider();

        var vectors = await provider.EmbedBatchAsync(["a", "b", "c"], CancellationToken.None);

        vectors.Should().NotBeNull();
        vectors!.Select(vector => vector[0]).Should().Equal(1f, 1f, 1f);
        provider.Requests.Select(request => request.Count).Should().Equal(2, 1);
    }

    [Fact]
    public async Task EmbedBatchAsync_RejectsNativeVectorBeforeObservingItsMalformedDimensions()
    {
        using var provider = new MalformedBatchProvider();

        (await provider.EmbedBatchAsync(["malformed"], CancellationToken.None)).Should().BeNull();
        (await provider.EmbedBatchAsync(["valid"], CancellationToken.None))
            .Should().NotBeNull()
            .And.ContainSingle()
            .Which.Should().Equal(1f, 0f);
    }

    [Fact]
    public async Task CompatibilityFallback_UsesExplicitSubBatchLimit()
    {
        using var provider = new LimitedCompatibilityProvider();

        var vectors = await provider.EmbedBatchAsync(["a", "b", "c", "d", "e"], CancellationToken.None);

        vectors.Should().NotBeNull().And.HaveCount(5);
        provider.RequestCount.Should().Be(5,
            because: "the single-input compatibility adapter must issue bounded sequential requests");
        provider.MaximumObservedBatchSize.Should().BeLessThanOrEqualTo(provider.Limit);
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

    private sealed class MalformedSingleProvider : ResilientEmbeddingProvider
    {
        private readonly Queue<float[]> _vectors;

        public MalformedSingleProvider(params float[][] vectors) : base(new HttpClient(), NullLogger.Instance)
            => _vectors = new Queue<float[]>(vectors);

        public override int Dimensions => 2;
        public override string Signature => "test:malformed-single";
        protected override string ProviderName => "Malformed single";

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            Task.FromResult<float[]?>(_vectors.Dequeue());
    }

    private class TrackingBatchProvider : ResilientEmbeddingProvider
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public TrackingBatchProvider() : base(new HttpClient(), NullLogger.Instance) { }
        public override int Dimensions => 2;
        public override string Signature => "test:batch";
        protected override string ProviderName => "Batch";
        protected override bool SupportsBatch => true;

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(
            IReadOnlyList<string> texts, CancellationToken ct)
        {
            Requests.Add(texts);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[[1,0],[0,1]]")
            };
            return Task.FromResult(response);
        }

        protected override Task<float[][]?> ParseEmbeddingBatchResponseAsync(
            HttpResponseMessage response, CancellationToken ct) =>
            Task.FromResult<float[][]?>([[1f, 0f], [0f, 1f]]);
    }

    private sealed class LimitedBatchProvider : ResilientEmbeddingProvider
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public LimitedBatchProvider() : base(new HttpClient(), NullLogger.Instance) { }
        public override int Dimensions => 1;
        public override string Signature => "test:limited";
        protected override string ProviderName => "Limited";
        protected override bool SupportsBatch => true;
        protected override int MaxBatchSize => 2;

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(
            IReadOnlyList<string> texts, CancellationToken ct)
        {
            Requests.Add(texts);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        protected override Task<float[][]?> ParseEmbeddingBatchResponseAsync(
            HttpResponseMessage response, CancellationToken ct)
        {
            var count = Requests[^1].Count;
            return Task.FromResult<float[][]?>(Enumerable.Range(1, count)
                .Select(value => new[] { (float)value }).ToArray());
        }
    }

    private sealed class MalformedBatchProvider : ResilientEmbeddingProvider
    {
        private int _requestCount;

        public MalformedBatchProvider() : base(new HttpClient(), NullLogger.Instance) { }
        public override int Dimensions => 2;
        public override string Signature => "test:malformed-batch";
        protected override string ProviderName => "Malformed batch";
        protected override bool SupportsBatch => true;

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(
            IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override Task<float[][]?> ParseEmbeddingBatchResponseAsync(
            HttpResponseMessage response, CancellationToken ct)
        {
            _requestCount++;
            return Task.FromResult<float[][]?>(_requestCount == 1
                ? [[1f]]
                : [[1f, 0f]]);
        }
    }

    private sealed class LimitedCompatibilityProvider : ResilientEmbeddingProvider
    {
        public int RequestCount { get; private set; }
        public int MaximumObservedBatchSize { get; private set; }
        public int Limit => MaxBatchSize;

        public LimitedCompatibilityProvider() : base(new HttpClient(), NullLogger.Instance) { }
        public override int Dimensions => 1;
        public override string Signature => "test:compatibility";
        protected override string ProviderName => "Compatibility";
        protected override int MaxBatchSize => 2;

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
        {
            RequestCount++;
            MaximumObservedBatchSize = Math.Max(MaximumObservedBatchSize, 1);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
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
