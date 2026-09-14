namespace Scrinia.Core;

internal interface IMonotonicClock
{
    TimeSpan GetElapsed();
}

internal sealed record TransactionalRetryPolicy
{
    internal TransactionalRetryPolicy(int maxAttempts = 3, TimeSpan? maxElapsed = null,
        TimeSpan? initialDelay = null, TimeSpan? maximumDelay = null)
    {
        if (maxAttempts is < 1 or > 8) throw new ArgumentException("Max attempts must be between 1 and 8.", nameof(maxAttempts));
        MaxAttempts = maxAttempts;
        MaxElapsed = maxElapsed ?? TimeSpan.FromSeconds(30);
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(2);
        if (MaxElapsed <= TimeSpan.Zero || MaxElapsed > TimeSpan.FromMinutes(5) || InitialDelay < TimeSpan.Zero ||
            MaximumDelay < InitialDelay || MaximumDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Retry policy bounds are invalid.");
    }
    internal int MaxAttempts { get; }
    internal TimeSpan MaxElapsed { get; }
    internal TimeSpan InitialDelay { get; }
    internal TimeSpan MaximumDelay { get; }
}

internal interface IWallClock
{
    DateTimeOffset UtcNow { get; }
}

internal enum RetryTerminalState
{
    Success, DeclaredTransientExhausted, CancellationBeforeCallback,
    CancellationAfterCallbackStart, RetryMonotonicClockFailed,
    RetryMonotonicClockMovedBackwards, RetryElapsedOverflow
}

internal sealed record RetryClassification(
    RetryTerminalState State, TransactionalFailureCode? FailureCode,
    bool StateMayHaveChanged, bool Ambiguous, bool Retryable,
    bool Cancelled, string? Detail)
{
    internal bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(State) || Retryable && FailureCode != TransactionalFailureCode.DeclaredTransient ||
                Cancelled && FailureCode != TransactionalFailureCode.Cancelled || Ambiguous && !StateMayHaveChanged)
                return false;
            return State switch
            {
                RetryTerminalState.RetryMonotonicClockFailed => FailureCode == TransactionalFailureCode.RetryMonotonicClockFailed && StateMayHaveChanged && Ambiguous && !Retryable && !Cancelled,
                RetryTerminalState.RetryMonotonicClockMovedBackwards => FailureCode == TransactionalFailureCode.RetryMonotonicClockMovedBackwards && StateMayHaveChanged && Ambiguous && !Retryable && !Cancelled,
                RetryTerminalState.RetryElapsedOverflow => FailureCode == TransactionalFailureCode.RetryElapsedOverflow && StateMayHaveChanged && Ambiguous && !Retryable && !Cancelled,
                _ => true
            };
        }
    }
}

internal interface IRetryClassifier<T>
{
    RetryClassification Classify(T result, bool callbackStarted);
    RetryClassification Classify(Exception exception, bool callbackStarted);
}

internal sealed record RetryResult<T>
{
    internal RetryResult(T? value, int attempts, TimeSpan elapsed,
        TransactionalFailureCode? failureCode, bool stateMayHaveChanged,
        bool ambiguous, bool exhausted, bool cancelled)
    {
        if (attempts < 0 || elapsed < TimeSpan.Zero ||
            failureCode is null && !cancelled && !exhausted && value is null ||
            (failureCode is not null || cancelled || exhausted) && value is not null ||
            cancelled && failureCode != TransactionalFailureCode.Cancelled ||
            exhausted && failureCode != TransactionalFailureCode.DeclaredTransient ||
            failureCode is null && (cancelled || exhausted))
            throw new ArgumentException("Retry result fields are inconsistent.");
        if (!stateMayHaveChanged && ambiguous)
            throw new ArgumentException("An ambiguous retry result must allow state change.");
        Value = value; Attempts = attempts; Elapsed = elapsed; FailureCode = failureCode;
        StateMayHaveChanged = stateMayHaveChanged; Ambiguous = ambiguous;
        Exhausted = exhausted; Cancelled = cancelled;
    }

    internal T? Value { get; }
    internal int Attempts { get; }
    internal TimeSpan Elapsed { get; }
    internal TransactionalFailureCode? FailureCode { get; }
    internal bool StateMayHaveChanged { get; }
    internal bool Ambiguous { get; }
    internal bool Exhausted { get; }
    internal bool Cancelled { get; }
}

internal sealed class RetryBudget
{
    private readonly TransactionalRetryPolicy _policy;
    private readonly IMonotonicClock _clock;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delayScheduler;

    internal RetryBudget(TransactionalRetryPolicy policy, IMonotonicClock clock,
        Func<TimeSpan, CancellationToken, ValueTask> delayScheduler)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
    }

    internal async Task<RetryResult<T>> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        IRetryClassifier<T> classifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(classifier);
        var attempts = 0;
        var lastElapsed = TimeSpan.Zero;
        var hasClockReading = false;
        var previousElapsed = TimeSpan.Zero;

        while (attempts < _policy.MaxAttempts)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled<T>(attempts, lastElapsed, false);

            RetryClassification classification;
            T? value = default;
            attempts++;
            try
            {
                value = await operation(cancellationToken).ConfigureAwait(false);
                classification = classifier.Classify(value, true);
            }
            catch (Exception exception)
            {
                classification = classifier.Classify(exception, true);
            }

            if (!TryReadElapsed(ref previousElapsed, ref hasClockReading, out lastElapsed, out var clockFailure))
                return new(default, attempts, lastElapsed, clockFailure, true, true, false, false);

            if (classification.Cancelled)
                return Cancelled<T>(attempts, lastElapsed, true);
            if (!classification.IsValid)
                return new(default, attempts, lastElapsed, TransactionalFailureCode.InvalidResponse, true, true, false, false);
            if (!classification.Retryable)
            {
                if (classification.FailureCode is null)
                    return new(value, attempts, lastElapsed, null, false, false, false, false);
                return new(default, attempts, lastElapsed, classification.FailureCode, classification.StateMayHaveChanged,
                    classification.Ambiguous, false, false);
            }
            if (attempts == _policy.MaxAttempts)
                return new(default, attempts, lastElapsed, TransactionalFailureCode.DeclaredTransient,
                    classification.StateMayHaveChanged, false, true, false);

            var delay = CalculateDelay(attempts - 1);
            if (delay is null)
                return new(default, attempts, lastElapsed, TransactionalFailureCode.RetryElapsedOverflow, true, true, false, false);
            if (!CanAdvance(lastElapsed, delay.Value, _policy.MaxElapsed, out var elapsedOverflow))
            {
                if (elapsedOverflow)
                    return new(default, attempts, lastElapsed, TransactionalFailureCode.RetryElapsedOverflow, true, true, false, false);
                return new(default, attempts, lastElapsed, TransactionalFailureCode.DeclaredTransient,
                    classification.StateMayHaveChanged, false, true, false);
            }
            try
            {
                await _delayScheduler(delay.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled<T>(attempts, lastElapsed, true);
            }
        }

        return new(default, attempts, lastElapsed, TransactionalFailureCode.DeclaredTransient, false, false, true, false);
    }

    private bool TryReadElapsed(ref TimeSpan previous, ref bool hasPrevious, out TimeSpan elapsed, out TransactionalFailureCode failure)
    {
        try { elapsed = _clock.GetElapsed(); }
        catch { elapsed = previous; failure = TransactionalFailureCode.RetryMonotonicClockFailed; return false; }
        if (elapsed < TimeSpan.Zero) { failure = TransactionalFailureCode.RetryMonotonicClockFailed; return false; }
        if (hasPrevious && elapsed < previous) { failure = TransactionalFailureCode.RetryMonotonicClockMovedBackwards; return false; }
        previous = elapsed; hasPrevious = true; failure = default; return true;
    }

    private TimeSpan? CalculateDelay(int retryIndex)
    {
        try
        {
            var ticks = checked(_policy.InitialDelay.Ticks * (1L << Math.Min(retryIndex, 30)));
            return TimeSpan.FromTicks(Math.Min(ticks, _policy.MaximumDelay.Ticks));
        }
        catch (OverflowException) { return null; }
    }

    private static bool CanAdvance(TimeSpan elapsed, TimeSpan delay, TimeSpan maximum, out bool overflow)
    {
        try
        {
            var next = TimeSpan.FromTicks(checked(elapsed.Ticks + delay.Ticks));
            overflow = false;
            return elapsed <= maximum && next <= maximum;
        }
        catch (OverflowException)
        {
            overflow = true;
            return false;
        }
    }

    private static RetryResult<T> Cancelled<T>(int attempts, TimeSpan elapsed, bool callbackStarted) =>
        new(default, attempts, elapsed, TransactionalFailureCode.Cancelled,
            callbackStarted, callbackStarted, false, true);
}

internal sealed class SystemMonotonicClock : IMonotonicClock
{
    private readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan GetElapsed() => TimeSpan.FromSeconds((double)(System.Diagnostics.Stopwatch.GetTimestamp() - _origin) / System.Diagnostics.Stopwatch.Frequency);
}

internal sealed class SystemWallClock : IWallClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
