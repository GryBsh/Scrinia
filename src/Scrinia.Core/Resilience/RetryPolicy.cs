using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Resilience;

/// <summary>Configuration for retry behavior.</summary>
public sealed record RetryOptions(int MaxRetries = 3, int BaseDelayMs = 200);

/// <summary>Retry with exponential backoff and jitter.</summary>
public static class RetryPolicy
{
    private static readonly Random Jitter = new();

    /// <summary>Execute an async operation with retry on transient failures.</summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Func<T, bool> isTransient,
        RetryOptions? options = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        options ??= new RetryOptions();

        for (int attempt = 0; ; attempt++)
        {
            T result;
            try
            {
                result = await operation();
            }
            catch (Exception ex) when (attempt < options.MaxRetries && TransientDetector.IsTransient(ex))
            {
                var delay = ComputeDelay(attempt, options.BaseDelayMs);
                logger?.LogWarning(ex, "Retry {Attempt}/{Max} after {Delay}ms (exception)", attempt + 1, options.MaxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                continue;
            }

            if (!isTransient(result) || attempt >= options.MaxRetries)
                return result;

            // Transient result — compute delay, check Retry-After if HttpResponseMessage
            var retryDelay = ComputeDelay(attempt, options.BaseDelayMs);
            if (result is HttpResponseMessage httpResponse)
                retryDelay = GetRetryAfterDelay(httpResponse.Headers, retryDelay);

            logger?.LogWarning("Retry {Attempt}/{Max} after {Delay}ms (transient response)", attempt + 1, options.MaxRetries, retryDelay.TotalMilliseconds);
            await Task.Delay(retryDelay, ct);
        }
    }

    /// <summary>Execute a synchronous operation with retry on transient failures.</summary>
    public static T Execute<T>(
        Func<T> operation,
        Func<T, bool> isTransient,
        RetryOptions? options = null,
        ILogger? logger = null)
    {
        options ??= new RetryOptions();

        for (int attempt = 0; ; attempt++)
        {
            T result;
            try
            {
                result = operation();
            }
            catch (Exception ex) when (attempt < options.MaxRetries && TransientDetector.IsTransient(ex))
            {
                var delay = ComputeDelay(attempt, options.BaseDelayMs);
                logger?.LogWarning(ex, "Retry {Attempt}/{Max} after {Delay}ms (exception)", attempt + 1, options.MaxRetries, (int)delay.TotalMilliseconds);
                Thread.Sleep(delay);
                continue;
            }

            if (!isTransient(result) || attempt >= options.MaxRetries)
                return result;

            var retryDelay = ComputeDelay(attempt, options.BaseDelayMs);
            logger?.LogWarning("Retry {Attempt}/{Max} after {Delay}ms (transient result)", attempt + 1, options.MaxRetries, (int)retryDelay.TotalMilliseconds);
            Thread.Sleep(retryDelay);
        }
    }

    private static TimeSpan ComputeDelay(int attempt, int baseDelayMs)
    {
        int exponential = baseDelayMs * (1 << Math.Min(attempt, 10));
        int jitter;
        lock (Jitter) { jitter = Jitter.Next(0, baseDelayMs); }
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseHeaders headers, TimeSpan fallback)
    {
        if (headers.RetryAfter is null) return fallback;

        if (headers.RetryAfter.Delta is { } delta)
            return delta > fallback ? delta : fallback;

        if (headers.RetryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > fallback ? wait : fallback;
        }

        return fallback;
    }
}
