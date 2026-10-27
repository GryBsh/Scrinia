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
    protected int ObservedDimensions => Volatile.Read(ref _dimensions);

    /// <inheritdoc />
    public abstract int Dimensions { get; }

    /// <summary>
    /// Required response dimensions, or zero when the model's shape must be discovered.
    /// Providers with an estimated display default override this independently of <see cref="Dimensions"/>.
    /// </summary>
    protected virtual int ExpectedDimensions => Dimensions;

    /// <inheritdoc />
    public abstract string Signature { get; }

    /// <summary>Display name for this provider, used in log messages.</summary>
    protected abstract string ProviderName { get; }

    /// <summary>Whether this provider has a genuine batch wire operation.</summary>
    protected virtual bool SupportsBatch => false;

    /// <summary>Maximum number of inputs sent in one provider-native request.</summary>
    protected virtual int MaxBatchSize => 64;

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
            using var trial = _circuitBreaker.EnterRequest();

            using var response = await RetryPolicy.ExecuteAsync(
                async () => await SendEmbedRequestAsync(text, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions,
                Logger,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Logger.LogWarning("{Provider} embedding request failed: HTTP {StatusCode} — {Body}",
                    ProviderName, (int)response.StatusCode, HttpErrorBody.Truncate(errorBody));
                // 4xx is a per-input client error (e.g. "input length exceeds context length")
                // bad payload, not provider health. Do NOT trip the circuit breaker: that would
                // cascade-fail every subsequent embed in a reindex even when the provider is
                // healthy. Reserve breaker failures for transient signals (5xx, 429, IO/socket).
                int statusCode = (int)response.StatusCode;
                bool isClientError = statusCode is >= 400 and < 500;
                if (!isClientError)
                    _circuitBreaker.RecordFailure();
                return null;
            }

            var vec = await ParseEmbeddingResponseAsync(response, ct);
            if (vec != null)
            {
                if (!ValidateVector(vec, ExpectedDimensions) || !TryObserveDimensions(vec.Length))
                    return null;

                VectorMath.L2Normalize(vec);
                _circuitBreaker.RecordSuccess();
            }
            return vec;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation must propagate, not masquerade as an embed failure
        }
        catch (CircuitBreakerOpenException)
        {
            Logger.LogWarning("Embedding provider '{Provider}' circuit breaker is open \u2014 falling back to keyword search", ProviderName);
            return null;
        }
        catch (Exception ex)
        {
            // Only count transient/infrastructure failures toward the breaker. Parse errors and
            // unexpected exceptions are logged but don't kill the provider for the rest of the
            // session, same rationale as the 4xx exclusion above.
            if (TransientDetector.IsTransient(ex))
                _circuitBreaker.RecordFailure();
            Logger.LogWarning(ex, "{Provider} embedding failed", ProviderName);
            return null;
        }
    }

    /// <inheritdoc />
    public virtual async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return [];
        if (!SupportsBatch)
            return await EmbedBatchSequentiallyAsync(texts, ct);

        if (MaxBatchSize <= 0)
            throw new InvalidOperationException($"{ProviderName} configured an invalid batch limit: {MaxBatchSize}.");

        var allVectors = new List<float[]>(texts.Count);
        for (int offset = 0; offset < texts.Count; offset += MaxBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(MaxBatchSize).ToArray();
            var vectors = await EmbedNativeBatchAsync(batch, ct);
            if (vectors is null)
                return null;
            allVectors.AddRange(vectors);
        }

        return allVectors.ToArray();
    }

    private async Task<float[][]?> EmbedNativeBatchAsync(string[] texts, CancellationToken ct)
    {

        try
        {
            using var trial = _circuitBreaker.EnterRequest();
            using var response = await RetryPolicy.ExecuteAsync(
                async () => await SendEmbedBatchRequestAsync(texts, ct),
                resp => TransientDetector.IsTransient(resp),
                _retryOptions, Logger, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Logger.LogWarning("{Provider} batch embedding request failed: HTTP {StatusCode} — {Body}",
                    ProviderName, (int)response.StatusCode, HttpErrorBody.Truncate(errorBody));
                if ((int)response.StatusCode is < 400 or >= 500)
                    _circuitBreaker.RecordFailure();
                return null;
            }

            var vectors = await ParseEmbeddingBatchResponseAsync(response, ct);
            if (vectors is null || vectors.Length != texts.Length)
                return null;

            int batchDimensions = 0;
            int expectedDimensions = ExpectedDimensions;
            foreach (var vector in vectors)
            {
                if (!ValidateVector(vector, expectedDimensions))
                    return null;

                if (batchDimensions > 0 && vector.Length != batchDimensions)
                {
                    Logger.LogWarning(
                        "{Provider} returned inconsistent dimensions within one batch: got {ActualDimensions}, previously got {BatchDimensions}",
                        ProviderName, vector.Length, batchDimensions);
                    return null;
                }

                batchDimensions = vector.Length;
            }

            // A malformed batch must not establish a shape. The CAS winner also decides
            // between simultaneous first responses with different, individually valid shapes.
            if (!TryObserveDimensions(batchDimensions))
                return null;

            foreach (var vector in vectors)
                VectorMath.L2Normalize(vector);

            _circuitBreaker.RecordSuccess();
            return vectors;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CircuitBreakerOpenException)
        {
            Logger.LogWarning("Embedding provider '{Provider}' circuit breaker is open — falling back to keyword search", ProviderName);
            return null;
        }
        catch (Exception ex)
        {
            if (TransientDetector.IsTransient(ex))
                _circuitBreaker.RecordFailure();
            Logger.LogWarning(ex, "{Provider} batch embedding failed", ProviderName);
            return null;
        }
    }

    private bool ValidateVector(float[]? vector, int expectedDimensions)
    {
        if (vector is not { Length: > 0 } || vector.Any(value => !float.IsFinite(value)))
        {
            Logger.LogWarning("{Provider} returned an empty or non-finite embedding vector", ProviderName);
            return false;
        }

        if (expectedDimensions > 0 && vector.Length != expectedDimensions)
        {
            Logger.LogWarning(
                "{Provider} returned a vector with {ActualDimensions} dimensions; expected {ExpectedDimensions}",
                ProviderName, vector.Length, expectedDimensions);
            return false;
        }

        return true;
    }

    private bool TryObserveDimensions(int dimensions)
    {
        int observed = Interlocked.CompareExchange(ref _dimensions, dimensions, 0);
        if (observed == 0 || observed == dimensions)
            return true;

        Logger.LogWarning(
            "{Provider} returned inconsistent vector dimensions: got {ActualDimensions}, previously observed {ObservedDimensions}",
            ProviderName, dimensions, observed);
        return false;
    }

    private async Task<float[][]?> EmbedBatchSequentiallyAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = new float[texts.Count][];
        if (MaxBatchSize <= 0)
            throw new InvalidOperationException($"{ProviderName} configured an invalid batch limit: {MaxBatchSize}.");

        for (int offset = 0; offset < texts.Count; offset += MaxBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(MaxBatchSize, texts.Count - offset);
            for (int i = 0; i < count; i++)
            {
                var vector = await EmbedAsync(texts[offset + i], ct);
                if (vector is null)
                    return null;
                vectors[offset + i] = vector;
            }
        }
        return vectors;
    }

    /// <summary>Send one provider-native batch request.</summary>
    protected virtual Task<HttpResponseMessage> SendEmbedBatchRequestAsync(
        IReadOnlyList<string> texts, CancellationToken ct) =>
        throw new NotSupportedException($"{ProviderName} does not support batch requests.");

    /// <summary>Parse one provider-native ordered batch response.</summary>
    protected virtual Task<float[][]?> ParseEmbeddingBatchResponseAsync(
        HttpResponseMessage response, CancellationToken ct) =>
        throw new NotSupportedException($"{ProviderName} does not support batch responses.");

    /// <summary>Validates provider indices and restores the caller's input order.</summary>
    protected static float[][]? OrderIndexedVectors<T>(
        IReadOnlyList<T>? items, int expectedCount, Func<T, int> index, Func<T, float[]?> vector)
    {
        if (items is null || items.Count != expectedCount)
            return null;

        var ordered = new float[expectedCount][];
        var seen = new bool[expectedCount];
        foreach (var item in items)
        {
            int itemIndex = index(item);
            var itemVector = vector(item);
            if (itemIndex < 0 || itemIndex >= expectedCount || seen[itemIndex] || itemVector is null)
                return null;
            seen[itemIndex] = true;
            ordered[itemIndex] = itemVector;
        }

        return seen.All(value => value) ? ordered : null;
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
