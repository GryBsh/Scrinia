using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Resilience;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests verifying retry + circuit breaker compose correctly
/// for the patterns used by embedding and chat providers.
/// </summary>
public sealed class ResilienceIntegrationTests
{
    // ── Embedding provider pattern: PostAsJsonAsync + EnsureSuccessStatusCode ──

    [Fact]
    public async Task EmbeddingPattern_RetriesTransientThenSucceeds()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 5));
        var retryOptions = new RetryOptions(MaxRetries: 3, BaseDelayMs: 1);
        int calls = 0;

        // Simulate the embedding provider EmbedAsync pattern
        cb.EnsureClosed();
        var response = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                var status = calls < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status));
            },
            resp => TransientDetector.IsTransient(resp),
            retryOptions,
            logger: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(3);
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task EmbeddingPattern_CircuitBreakerOpensAfterThresholdFailures()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 3));
        var retryOptions = new RetryOptions(MaxRetries: 0, BaseDelayMs: 1); // no retries — each call fails once

        for (int i = 0; i < 3; i++)
        {
            try
            {
                cb.EnsureClosed();
                var response = await RetryPolicy.ExecuteAsync(
                    () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                    resp => TransientDetector.IsTransient(resp),
                    retryOptions,
                    logger: null);
                // Response is transient but retries exhausted — record failure
                cb.RecordFailure();
            }
            catch (CircuitBreakerOpenException)
            {
                // Circuit already open from a prior iteration
            }
        }

        cb.State.Should().Be(CircuitState.Open);

        // Next call should throw immediately
        var act = () => cb.EnsureClosed();
        act.Should().Throw<CircuitBreakerOpenException>();
    }

    [Fact]
    public async Task EmbeddingPattern_CircuitBreakerResetsOnSuccess()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 3));
        var retryOptions = new RetryOptions(MaxRetries: 0, BaseDelayMs: 1);

        // Record 2 failures (below threshold)
        for (int i = 0; i < 2; i++)
        {
            cb.EnsureClosed();
            await RetryPolicy.ExecuteAsync(
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                resp => TransientDetector.IsTransient(resp),
                retryOptions);
            cb.RecordFailure();
        }

        // Success resets the counter
        cb.EnsureClosed();
        var ok = await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            resp => TransientDetector.IsTransient(resp),
            retryOptions);
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);

        // One more failure should NOT open (counter was reset)
        cb.EnsureClosed();
        await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            resp => TransientDetector.IsTransient(resp),
            retryOptions);
        cb.RecordFailure();
        cb.State.Should().Be(CircuitState.Closed);
    }

    // ── Chat provider pattern: SendAsync with retry + circuit breaker ─────────

    [Fact]
    public async Task ChatPattern_RetriesTransientThenSucceeds()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 5));
        var retryOptions = new RetryOptions(MaxRetries: 3, BaseDelayMs: 1);
        int calls = 0;

        // Simulate the chat provider StreamChatAsync pattern
        cb.EnsureClosed();
        var response = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                if (calls < 2)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

                // Success — return a streaming-style response with SSE body
                var successResponse = new HttpResponseMessage(HttpStatusCode.OK);
                successResponse.Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\ndata: [DONE]\n",
                    Encoding.UTF8, "text/event-stream");
                return Task.FromResult(successResponse);
            },
            resp => TransientDetector.IsTransient(resp),
            retryOptions,
            logger: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);

        // Simulate reading SSE stream successfully
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("[DONE]");
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task ChatPattern_CircuitBreakerOpensAfterRepeatedFailures()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 2));
        var retryOptions = new RetryOptions(MaxRetries: 0, BaseDelayMs: 1);

        // First failed request
        cb.EnsureClosed();
        var r1 = await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            resp => TransientDetector.IsTransient(resp),
            retryOptions);
        r1.IsSuccessStatusCode.Should().BeFalse();
        cb.RecordFailure();

        // Second failed request — should trip the circuit
        cb.EnsureClosed();
        var r2 = await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            resp => TransientDetector.IsTransient(resp),
            retryOptions);
        cb.RecordFailure();

        cb.State.Should().Be(CircuitState.Open);

        // Third request — circuit breaker should prevent it
        string? errorMessage = null;
        try
        {
            cb.EnsureClosed();
        }
        catch (CircuitBreakerOpenException ex)
        {
            errorMessage = ex.Message;
        }

        errorMessage.Should().NotBeNull();
        errorMessage.Should().Contain("Circuit breaker is open");
    }

    [Fact]
    public async Task ChatPattern_TransientExceptionRetriedAndSucceeds()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 5));
        var retryOptions = new RetryOptions(MaxRetries: 3, BaseDelayMs: 1);
        int calls = 0;

        cb.EnsureClosed();
        var response = await RetryPolicy.ExecuteAsync(
            () =>
            {
                calls++;
                if (calls == 1) throw new TimeoutException("Request timed out");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            resp => TransientDetector.IsTransient(resp),
            retryOptions,
            logger: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task FullLifecycle_CircuitOpensRecoversThenCloses()
    {
        var cb = new CircuitBreaker(new CircuitBreakerOptions(Threshold: 2, CooldownSeconds: 1));
        var retryOptions = new RetryOptions(MaxRetries: 0, BaseDelayMs: 1);

        // Trip the circuit with 2 failures
        for (int i = 0; i < 2; i++)
        {
            cb.EnsureClosed();
            await RetryPolicy.ExecuteAsync(
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
                resp => TransientDetector.IsTransient(resp),
                retryOptions);
            cb.RecordFailure();
        }
        cb.State.Should().Be(CircuitState.Open);

        // Wait for cooldown
        await Task.Delay(1100);
        cb.State.Should().Be(CircuitState.HalfOpen);

        // Successful probe closes the circuit
        cb.EnsureClosed(); // HalfOpen allows requests
        var response = await RetryPolicy.ExecuteAsync(
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            resp => TransientDetector.IsTransient(resp),
            retryOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        cb.RecordSuccess();
        cb.State.Should().Be(CircuitState.Closed);
    }
}
