using Microsoft.Extensions.Logging;
using Scrinia.Core.Resilience;

namespace Scrinia.Core.Embeddings.Providers;

/// <summary>
/// Abstract base class for HTTP-based embedding providers that share
/// circuit breaker + retry resilience boilerplate.
/// </summary>
public abstract class ResilientEmbeddingProvider : IEmbeddingProvider
{
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RetryOptions _retryOptions;
    private int _dimensions;

    /// <summary>The HTTP client used to make API requests.</summary>
    protected readonly HttpClient Http;

    /// <summary>Logger instance for the provider.</summary>
    protected readonly ILogger Logger;

    /// <summary>Retry options for transient failure handling.</summary>
    protected RetryOptions RetryOpts => _retryOptions;

    /// <inheritdoc />
    public virtual bool IsAvailable => true;

    /// <summary>
    /// Observed dimensions from the first successful embedding response.
    /// Returns 0 when no embedding has been generated yet.
    /// </summary>
    protected int ObservedDimensions => _dimensions;

    /// <inheritdoc />
    public abstract int Dimensions { get; }

    /// <summary>Display name for this provider, used in log messages.</summary>
    protected abstract string ProviderName { get; }

    /// <summary>Initializes resilience infrastructure shared by all HTTP embedding providers.</summary>
    protected ResilientEmbeddingProvider(HttpClient http, ILogger logger,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null)
    {
        Http = http;
        Logger = logger;
        _circuitBreaker = circuitBreaker ?? new CircuitBreaker();
        _retryOptions = retryOptions ?? new RetryOptions();
    }

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            _circuitBreaker.EnsureClosed();

            var response = await RetryPolicy.ExecuteAsync(
                async () => await SendEmbedRequestAsync(text, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                Logger,
                ct);
            response.EnsureSuccessStatusCode();

            var vec = await ParseEmbeddingResponseAsync(response, ct);
            if (vec != null)
            {
                Interlocked.CompareExchange(ref _dimensions, vec.Length, 0);
                VectorMath.L2Normalize(vec);
                _circuitBreaker.RecordSuccess();
            }
            return vec;
        }
        catch (CircuitBreakerOpenException)
        {
            Logger.LogWarning("Embedding provider '{Provider}' circuit breaker is open \u2014 falling back to keyword search", ProviderName);
            return null;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            Logger.LogWarning(ex, "{Provider} embedding failed", ProviderName);
            return null;
        }
    }

    /// <summary>Send the HTTP request for a single embedding. Called inside the retry policy.</summary>
    protected abstract Task<HttpResponseMessage> SendEmbedRequestAsync(string text, CancellationToken ct);

    /// <summary>Parse the embedding vector from a successful HTTP response. Return null if not found.</summary>
    protected abstract Task<float[]?> ParseEmbeddingResponseAsync(HttpResponseMessage response, CancellationToken ct);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Dispose resources. Override to add custom cleanup.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            Http.Dispose();
    }
}
