namespace Scrinia.Core.Resilience;

/// <summary>Configuration for circuit breaker behavior.</summary>
public sealed record CircuitBreakerOptions(int Threshold = 5, int CooldownSeconds = 30);

/// <summary>Circuit breaker states.</summary>
public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>
/// Per-provider circuit breaker. Tracks consecutive failures and prevents
/// calling a dead service. Thread-safe via Interlocked operations.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private int _consecutiveFailures;
    private long _openedAtTicks; // 0 = not open
    private int _state; // CircuitState as int for Interlocked
    private int _trialInFlight; // 0 = free, 1 = a trial is probing

    public CircuitBreaker(CircuitBreakerOptions? options = null)
    {
        options ??= new CircuitBreakerOptions();
        _threshold = options.Threshold;
        _cooldown = TimeSpan.FromSeconds(options.CooldownSeconds);
    }

    /// <summary>Current circuit state.</summary>
    public CircuitState State
    {
        get
        {
            var s = (CircuitState)Volatile.Read(ref _state);
            if (s == CircuitState.Open && CooldownExpired())
                return CircuitState.HalfOpen;
            return s;
        }
    }

    /// <summary>Check if a request should be allowed. Throws if circuit is open.</summary>
    public void EnsureClosed()
    {
        var state = State;
        if (state == CircuitState.Open)
            throw new CircuitBreakerOpenException(_cooldown);
    }

    /// <summary>Returns true if the circuit allows a request (closed or half-open).</summary>
    public bool AllowRequest() => State != CircuitState.Open;

    /// <summary>
    /// Attempts to claim the single half-open trial slot. Returns a non-null <see cref="HalfOpenTrial"/>
    /// token for exactly one caller after cooldown expiry; others get null until the trial is resolved.
    /// <para>
    /// Consumer contract: <c>using var trial = breaker.TryEnterHalfOpen(); if (trial is null) { /* treat
    /// as open */ } else { /* make the probe call; call RecordSuccess/RecordFailure on the outcome; the
    /// using disposes the token to release the slot even on exception/cancellation */ }</c>
    /// </para>
    /// </summary>
    public HalfOpenTrial? TryEnterHalfOpen()
    {
        if (State != CircuitState.HalfOpen) return null;
        if (Interlocked.CompareExchange(ref _trialInFlight, 1, 0) != 0) return null;
        return new HalfOpenTrial(this);
    }

    /// <summary>Record a successful operation. Resets failure count, closes circuit.</summary>
    /// <remarks>The half-open trial slot is released by disposing the <see cref="HalfOpenTrial"/> token, not here.</remarks>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
        Interlocked.Exchange(ref _openedAtTicks, 0);
    }

    /// <summary>Record a failed operation. Opens circuit if threshold reached.</summary>
    /// <remarks>The half-open trial slot is released by disposing the <see cref="HalfOpenTrial"/> token, not here.</remarks>
    public void RecordFailure()
    {
        int failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= _threshold)
        {
            Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>Manually reset the circuit to closed state.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
        Interlocked.Exchange(ref _openedAtTicks, 0);
        Interlocked.Exchange(ref _trialInFlight, 0);
    }

    private void ReleaseTrial() => Interlocked.Exchange(ref _trialInFlight, 0);

    private bool CooldownExpired()
    {
        long openedAt = Volatile.Read(ref _openedAtTicks);
        if (openedAt == 0) return false;
        return (DateTime.UtcNow.Ticks - openedAt) >= _cooldown.Ticks;
    }

    /// <summary>
    /// RAII token representing ownership of the single half-open trial slot. Disposing the token
    /// releases the slot exactly once, regardless of whether the probe succeeded, failed, threw,
    /// or was cancelled.
    /// </summary>
    public sealed class HalfOpenTrial : IDisposable
    {
        private CircuitBreaker? _owner;

        internal HalfOpenTrial(CircuitBreaker owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseTrial();
    }
}

/// <summary>Thrown when a circuit breaker is open and rejecting requests.</summary>
public sealed class CircuitBreakerOpenException : Exception
{
    public TimeSpan Cooldown { get; }

    public CircuitBreakerOpenException(TimeSpan cooldown)
        : base($"Circuit breaker is open. Retry after {cooldown.TotalSeconds:F0}s cooldown.")
    {
        Cooldown = cooldown;
    }
}
