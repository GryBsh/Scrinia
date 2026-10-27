using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings.Providers;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests.Embeddings;

public sealed class ProviderResilienceRegressionTests
{
    [Theory]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("azure", false)]
    [InlineData("azure", true)]
    [InlineData("voyage", false)]
    [InlineData("voyage", true)]
    public async Task ConfigurableHttpModel_DiscoversFirstShape_AndRejectsLaterShape(string kind, bool batch)
    {
        await using var server = new JsonServer(
            "{\"data\":[{\"index\":0,\"embedding\":[3,4]}]}",
            "{\"data\":[{\"index\":0,\"embedding\":[1,0,0]}]}");
        using var provider = CreateProvider(kind, server.Url);

        var first = await Embed(provider, batch);
        Assert.NotNull(first);
        Assert.Equal(2, first.Length);
        Assert.Equal(0.6f, first[0]);
        Assert.Equal(0.8f, first[1]);
        Assert.Equal(2, provider.Dimensions);
        Assert.Null(await Embed(provider, !batch));
        Assert.Equal(2, provider.Dimensions);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "nan")]
    [InlineData(true, "nan")]
    [InlineData(false, "positive-infinity")]
    [InlineData(true, "positive-infinity")]
    [InlineData(false, "negative-infinity")]
    [InlineData(true, "negative-infinity")]
    public async Task InvalidVector_DoesNotEstablishDimensionsOrCloseCircuit(bool batch, string shape)
    {
        float[] invalid = shape switch
        {
            "empty" => [],
            "nan" => [float.NaN],
            "positive-infinity" => [float.PositiveInfinity],
            _ => [float.NegativeInfinity]
        };
        var breaker = OpenBreaker();
        using var provider = new ControlledProvider(breaker) { Vectors = [invalid] };

        Assert.Null(await Embed(provider, batch));
        Assert.Equal(0, provider.Dimensions);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        provider.Vectors = [[3f, 4f]];
        Assert.NotNull(await Embed(provider, !batch));
        Assert.Equal(2, provider.Dimensions);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task InvalidBatch_DoesNotLatchItsFirstVectorDimensions()
    {
        using var provider = new ControlledProvider { Vectors = [[1f, 0f], [1f, 0f, 0f]] };
        Assert.Null(await provider.EmbedBatchAsync(["a", "b"]));
        Assert.Equal(0, provider.Dimensions);
        provider.Vectors = [[1f, 0f, 0f]];
        Assert.NotNull(await provider.EmbedAsync("valid"));
        Assert.Equal(3, provider.Dimensions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HalfOpen_SingleAndBatchShareOneProbe(bool batchProbe)
    {
        var breaker = OpenBreaker();
        using var provider = new ControlledProvider(breaker) { Gate = NewSignal() };
        var probe = Embed(provider, batchProbe);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.Null(await Embed(provider, !batchProbe).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, provider.RequestCount);
            Assert.Equal(CircuitState.HalfOpen, breaker.State);
        }
        finally
        {
            provider.Gate.SetResult();
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.NotNull(await Embed(provider, !batchProbe));
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "client")]
    [InlineData(true, "client")]
    [InlineData(false, "server")]
    [InlineData(true, "server")]
    [InlineData(false, "parse")]
    [InlineData(true, "parse")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public async Task HalfOpen_UnsuccessfulProbeReleasesPermitWithoutSuccess(bool batchProbe, string outcome)
    {
        var breaker = OpenBreaker();
        using var provider = new ControlledProvider(breaker);
        using var cts = new CancellationTokenSource();
        provider.Status = outcome switch
        {
            "client" => HttpStatusCode.BadRequest,
            "server" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.OK
        };
        provider.ParseError = outcome == "parse";
        if (outcome == "null") provider.Vectors = null;
        if (outcome == "cancel")
        {
            provider.Gate = NewSignal();
            var pending = Embed(provider, batchProbe, cts.Token);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            provider.Gate = null;
        }
        else
        {
            Assert.Null(await Embed(provider, batchProbe));
        }

        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        provider.Status = HttpStatusCode.OK;
        provider.ParseError = false;
        provider.Vectors = [[1f, 0f]];
        Assert.NotNull(await Embed(provider, !batchProbe));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task ConcurrentFirstBatches_OnlyOneShapeCanSucceed()
    {
        const int callers = 8;
        using var start = new Barrier(callers);
        using var provider = new ConcurrentShapeProvider(start);
        var tasks = Enumerable.Range(0, callers).Select(i => Task.Factory.StartNew(
            () => provider.EmbedBatchAsync([i.ToString()]).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        var accepted = results.Where(result => result is not null).ToArray();
        Assert.Single(accepted);
        Assert.Equal(provider.Dimensions, accepted[0]![0].Length);
    }

    [Fact]
    public async Task GeminiConfiguredDimensions_RemainAnEnforcedConstraint()
    {
        await using var server = new JsonServer("{\"embedding\":{\"values\":[1,0,0]}}");
        using var provider = new GoogleGeminiEmbeddingProvider("offline", "custom", server.Url, 2, NullLogger.Instance);
        Assert.Null(await provider.EmbedAsync("text"));
        Assert.Equal(2, provider.Dimensions);
    }

    private static ResilientEmbeddingProvider CreateProvider(string kind, string url) => kind switch
    {
        "openai" => new OpenAiEmbeddingProvider("offline", "custom-model", url, NullLogger.Instance),
        "azure" => new AzureAiEmbeddingProvider(url, "offline", "custom-deployment", "custom-model", "2024-10-21", false, NullLogger.Instance),
        "voyage" => new VoyageAiEmbeddingProvider("offline", "custom-model", url, NullLogger.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<float[]?> Embed(ResilientEmbeddingProvider provider, bool batch, CancellationToken ct = default) =>
        batch ? (await provider.EmbedBatchAsync(["text"], ct))?[0] : await provider.EmbedAsync("text", ct);

    private static CircuitBreaker OpenBreaker()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1, CooldownSeconds: 0));
        breaker.RecordFailure();
        return breaker;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledProvider(CircuitBreaker? breaker = null)
        : ResilientEmbeddingProvider(new HttpClient(), NullLogger.Instance, breaker, new RetryOptions(MaxRetries: 0))
    {
        public float[][]? Vectors { get; set; } = [[1f, 0f]];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool ParseError { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Entered { get; } = NewSignal();
        public int RequestCount;
        public override int Dimensions => ObservedDimensions;
        public override string Signature => "test:controlled";
        protected override string ProviderName => "Controlled";
        protected override bool SupportsBatch => true;

        protected override async Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct)
        {
            int request = Interlocked.Increment(ref RequestCount);
            Entered.TrySetResult();
            if (request == 1 && Gate is { } gate) await gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent("") };
        }

        protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            SendEmbedRequestAsync(texts[0], ct);

        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            ParseError ? throw new FormatException("malformed response") : Task.FromResult(Vectors?[0]);

        protected override Task<float[][]?> ParseEmbeddingBatchResponseAsync(HttpResponseMessage response, CancellationToken ct) =>
            ParseError ? throw new FormatException("malformed response") : Task.FromResult(Vectors);
    }

    private sealed class ConcurrentShapeProvider(Barrier start)
        : ResilientEmbeddingProvider(new HttpClient(), NullLogger.Instance)
    {
        public override int Dimensions => ObservedDimensions;
        public override string Signature => "test:concurrent";
        protected override string ProviderName => "Concurrent";
        protected override bool SupportsBatch => true;

        protected override Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct) => throw new NotSupportedException();
        protected override Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct) => throw new NotSupportedException();

        protected override Task<HttpResponseMessage> SendEmbedBatchRequestAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(texts[0]) });

        protected override async Task<float[][]?> ParseEmbeddingBatchResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            int index = int.Parse(await response.Content.ReadAsStringAsync(ct));
            var vector = Enumerable.Repeat(1f, 1_000_000 + index).ToArray();
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10), ct)) throw new TimeoutException("Concurrent response gate timed out.");
            return [vector];
        }
    }

    private sealed class JsonServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _serve;
        public string Url { get; }

        public JsonServer(params string[] responses)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serve = ServeAsync(responses);
        }

        private async Task ServeAsync(string[] responses)
        {
            foreach (string response in responses)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                int length = 0;
                bool chunked = false;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line[15..]);
                    if (line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                        chunked = true;
                }
                if (chunked)
                {
                    while (true)
                    {
                        int size = Convert.ToInt32(await reader.ReadLineAsync(_stop.Token), 16);
                        Assert.Equal(size, await reader.ReadBlockAsync(new char[size].AsMemory(), _stop.Token));
                        await reader.ReadLineAsync(_stop.Token);
                        if (size == 0) break;
                    }
                }
                else
                {
                    Assert.Equal(length, await reader.ReadBlockAsync(new char[length].AsMemory(), _stop.Token));
                }
                byte[] bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(bytes, _stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _serve; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _stop.Dispose(); }
        }
    }
}
