using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests;

public sealed class TransientDetectorTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransient_TransientStatusCode_ReturnsTrue(HttpStatusCode code)
    {
        var response = new HttpResponseMessage(code);
        TransientDetector.IsTransient(response).Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void IsTransient_NonTransientStatusCode_ReturnsFalse(HttpStatusCode code)
    {
        var response = new HttpResponseMessage(code);
        TransientDetector.IsTransient(response).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_NullResponse_ReturnsFalse()
    {
        TransientDetector.IsTransient((HttpResponseMessage?)null).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_TimeoutException_ReturnsTrue()
    {
        TransientDetector.IsTransient(new TimeoutException()).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_IOException_ReturnsTrue()
    {
        TransientDetector.IsTransient(new IOException()).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_SocketException_ReturnsTrue()
    {
        TransientDetector.IsTransient(new SocketException()).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_HttpRequestExceptionWrappingSocket_ReturnsTrue()
    {
        var inner = new SocketException();
        var ex = new HttpRequestException("fail", inner);
        TransientDetector.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_HttpRequestExceptionWrappingIO_ReturnsTrue()
    {
        var inner = new IOException();
        var ex = new HttpRequestException("fail", inner);
        TransientDetector.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_ArgumentException_ReturnsFalse()
    {
        TransientDetector.IsTransient(new ArgumentException()).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_NullException_ReturnsFalse()
    {
        TransientDetector.IsTransient((Exception?)null).Should().BeFalse();
    }

    [Fact]
    public void HttpClientTimeout_TaskCanceledWithTimeoutInner_IsTransient()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());
        TransientDetector.IsTransient(ex).Should().BeTrue(); // BUG: currently false
    }

    [Fact]
    public void CallerCancellation_TaskCanceledWithoutTimeoutInner_IsNotTransient()
    {
        var ex = new TaskCanceledException("cancelled");
        TransientDetector.IsTransient(ex).Should().BeFalse();
    }
}

public sealed class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_SucceedsFirstAttempt_ReturnsResult()
    {
        var result = await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(42),
            r => false,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_TransientThenSuccess_RetriesAndReturns()
    {
        int calls = 0;
        var result = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                return Task.FromResult(calls < 3 ? -1 : 99);
            },
            r => r == -1,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(99);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_AllTransient_ReturnsLastResult()
    {
        int calls = 0;
        var result = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                return Task.FromResult(-1);
            },
            r => r == -1,
            new RetryOptions(MaxRetries: 2, BaseDelayMs: 1));

        result.Should().Be(-1);
        calls.Should().Be(3); // initial + 2 retries
    }

    [Fact]
    public async Task ExecuteAsync_TransientException_RetriesThenSucceeds()
    {
        int calls = 0;
        var result = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                if (calls < 2) throw new TimeoutException();
                return Task.FromResult(7);
            },
            _ => false,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(7);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientException_DoesNotRetry()
    {
        int calls = 0;
        var act = () => RetryPolicy.ExecuteAsync<int>(
            () => { calls++; throw new ArgumentException("bad"); },
            _ => false,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        await act.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCanellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => RetryPolicy.ExecuteAsync(
            () => Task.FromResult(-1),
            r => r == -1,
            new RetryOptions(MaxRetries: 5, BaseDelayMs: 1),
            ct: cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_BackoffIncreases()
    {
        var delays = new List<TimeSpan>();
        int calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan lastTime = TimeSpan.Zero;

        await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                var now = sw.Elapsed;
                if (calls > 1) delays.Add(now - lastTime);
                lastTime = now;
                return Task.FromResult(calls <= 3 ? -1 : 1);
            },
            r => r == -1,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 50));

        delays.Should().HaveCount(3);

        // Each delay is asserted against its OWN lower bound rather than compared to another
        // measured delay, and that distinction is the whole point.
        //
        // `RetryPolicy.ComputeDelay` is `baseDelay * 2^attempt + jitter[0, baseDelay)`, so with a
        // 50ms base the computed values fall in [50,100), [100,150) and [200,250) — ranges that
        // cannot overlap, so `delays[2] > delays[0]` is true of the POLICY by construction. What
        // these numbers actually measure is wall clock, which is the computed delay PLUS however
        // long the thread pool took to resume the continuation. Under a loaded parallel test host
        // that scheduling penalty is unbounded, and it lands on each sample independently: a 250ms
        // stall on the first sample and none on the third inverts the comparison and fails a test
        // about arithmetic that never went wrong. That is the observed flake — caught by name on
        // 2026-08-20 during a full-solution run.
        //
        // Scheduling noise is ONE-DIRECTIONAL: it can only make an observed delay longer, never
        // shorter. So a lower bound per delay is immune to it by construction, while any
        // comparison between two noisy samples is not. The bounds also pin growth more tightly
        // than the old assertion did — it only related delays[2] to delays[0] and said nothing
        // about delays[1].
        //
        // The 10% slack absorbs timer granularity (Task.Delay resolves on a ~15.6ms tick on
        // Windows), not load. Collapsing ComputeDelay to a constant still fails these: a flat 50ms
        // would have to pick up 130ms of scheduling delay to reach the third bound.
        delays[0].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(45),
            because: "attempt 0 sleeps baseDelay (50ms) plus jitter, and scheduling only adds");
        delays[1].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90),
            because: "attempt 1 doubles to 100ms — the step the old assertion never checked");
        delays[2].Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180),
            because: "attempt 2 doubles again to 200ms; a backoff that stopped growing cannot "
                   + "reach this without 130ms of scheduling delay landing on exactly this sample");
    }

    [Fact]
    public void Execute_Sync_SucceedsFirstAttempt()
    {
        var result = RetryPolicy.Execute(
            () => 42,
            r => false,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(42);
    }

    [Fact]
    public void Execute_Sync_TransientThenSuccess()
    {
        int calls = 0;
        var result = RetryPolicy.Execute(
            () =>
            {
                calls++;
                return calls < 2 ? -1 : 10;
            },
            r => r == -1,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(10);
        calls.Should().Be(2);
    }

    [Fact]
    public void Execute_Sync_TransientException_Retries()
    {
        int calls = 0;
        var result = RetryPolicy.Execute(
            () =>
            {
                calls++;
                if (calls < 2) throw new IOException("transient");
                return 5;
            },
            _ => false,
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.Should().Be(5);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_RetryAfterHeader_Respected()
    {
        int calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                var response = new HttpResponseMessage(
                    calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
                if (calls == 1)
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                return Task.FromResult(response);
            },
            r => TransientDetector.IsTransient(r),
            new RetryOptions(MaxRetries: 3, BaseDelayMs: 1));

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);
        // Should have waited at least the Retry-After duration
        sw.Elapsed.TotalMilliseconds.Should().BeGreaterThan(150);
    }
}

public sealed class CircuitBreakerTests
{
    [Fact]
    public void NewCircuitBreaker_StartsInClosedState()
    {
        var cb = new CircuitBreaker();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void AllowRequest_WhenClosed_ReturnsTrue()
    {
        var cb = new CircuitBreaker();
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_BelowThreshold_StaysClosed()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 5));
        for (int i = 0; i < 4; i++)
            cb.RecordFailure();

        cb.State.Should().Be(CircuitState.Closed);
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 3));
        for (int i = 0; i < 3; i++)
            cb.RecordFailure();

        cb.State.Should().Be(CircuitState.Open);
        cb.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void EnsureClosed_WhenOpen_ThrowsCircuitBreakerOpenException()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1));
        cb.RecordFailure();

        var act = () => cb.EnsureClosed();
        act.Should().Throw<CircuitBreakerOpenException>()
            .Which.Cooldown.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void RecordSuccess_ResetsFailuresAndCloses()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 2));
        cb.RecordFailure();
        cb.RecordSuccess();
        cb.RecordFailure(); // should be 1 again, not 2

        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Reset_FromOpen_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1));
        cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Open);

        cb.Reset();
        cb.State.Should().Be(CircuitState.Closed);
        cb.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void CooldownExpired_TransitionsToHalfOpen()
    {
        // Use a 1-second cooldown so we can test the transition
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1, CooldownSeconds: 1));
        cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Open);

        // Wait for cooldown to expire
        Thread.Sleep(1100);

        cb.State.Should().Be(CircuitState.HalfOpen);
        cb.AllowRequest().Should().BeTrue(); // HalfOpen allows requests
    }

    [Fact]
    public void HalfOpen_SuccessClosesCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1, CooldownSeconds: 1));
        cb.RecordFailure();
        Thread.Sleep(1100);
        cb.State.Should().Be(CircuitState.HalfOpen);

        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void HalfOpen_FailureReopensCircuit()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 1, CooldownSeconds: 1));
        cb.RecordFailure();
        Thread.Sleep(1100);
        cb.State.Should().Be(CircuitState.HalfOpen);

        cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentFailures_OpenCircuitCorrectly()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 10));

        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => cb.RecordFailure()));
        await Task.WhenAll(tasks);

        // After 20 concurrent failures, circuit must be open
        cb.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentSuccessAndFailure_NoCrash()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 50));

        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() =>
            {
                if (i % 2 == 0)
                    cb.RecordFailure();
                else
                    cb.RecordSuccess();
            }));
        await Task.WhenAll(tasks);

        // Should not throw — state is indeterminate but valid
        var state = cb.State;
        state.Should().BeOneOf(CircuitState.Closed, CircuitState.Open, CircuitState.HalfOpen);
    }

    [Fact]
    public void CircuitBreakerOpenException_HasCooldownInfo()
    {
        var cooldown = TimeSpan.FromSeconds(30);
        var ex = new CircuitBreakerOpenException(cooldown);
        ex.Cooldown.Should().Be(cooldown);
        ex.Message.Should().Contain("30");
    }

    [Fact]
    public void DefaultOptions_UseExpectedValues()
    {
        var opts = new CircuitBreakerOptions();
        opts.Threshold.Should().Be(5);
        opts.CooldownSeconds.Should().Be(30);

        var retryOpts = new RetryOptions();
        retryOpts.MaxRetries.Should().Be(3);
        retryOpts.BaseDelayMs.Should().Be(200);
    }
}

public sealed class CircuitBreakerRegistryTests : IDisposable
{
    public CircuitBreakerRegistryTests() => CircuitBreakerRegistry.Clear();
    public void Dispose() => CircuitBreakerRegistry.Clear();

    [Fact]
    public void Register_And_TryGet_RoundTrips()
    {
        var cb = new CircuitBreaker();
        CircuitBreakerRegistry.Register("test-provider", cb);

        CircuitBreakerRegistry.TryGet("test-provider", out var retrieved).Should().BeTrue();
        retrieved.Should().BeSameAs(cb);
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        CircuitBreakerRegistry.TryGet("nonexistent", out _).Should().BeFalse();
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var cb1 = new CircuitBreaker();
        var cb2 = new CircuitBreaker();
        CircuitBreakerRegistry.Register("provider-a", cb1);
        CircuitBreakerRegistry.Register("provider-b", cb2);

        var all = CircuitBreakerRegistry.GetAll();
        // Containment, not an exact count. The registry is a process-wide static and xUnit runs
        // test classes in parallel, so any concurrently running test that constructs an
        // HttpMemoryStore registers "http-memory-store:{store}" and inflates the total. The
        // Clear() in this class's ctor cannot fence that off — it only orders against itself.
        // What GetAll actually promises is that a registered breaker comes back; assert that.
        all.Should().ContainKey("provider-a").WhoseValue.Should().BeSameAs(cb1);
        all.Should().ContainKey("provider-b").WhoseValue.Should().BeSameAs(cb2);
    }

    [Fact]
    public void Register_Overwrites_ExistingName()
    {
        var cb1 = new CircuitBreaker();
        var cb2 = new CircuitBreaker();
        CircuitBreakerRegistry.Register("same-name", cb1);
        CircuitBreakerRegistry.Register("same-name", cb2);

        CircuitBreakerRegistry.TryGet("same-name", out var retrieved).Should().BeTrue();
        retrieved.Should().BeSameAs(cb2);
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        CircuitBreakerRegistry.Register("to-remove", new CircuitBreaker());
        CircuitBreakerRegistry.Remove("to-remove").Should().BeTrue();
        CircuitBreakerRegistry.TryGet("to-remove", out _).Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitive_Lookup()
    {
        var cb = new CircuitBreaker();
        CircuitBreakerRegistry.Register("Chat:OpenAI", cb);

        CircuitBreakerRegistry.TryGet("chat:openai", out var retrieved).Should().BeTrue();
        retrieved.Should().BeSameAs(cb);
    }
}
