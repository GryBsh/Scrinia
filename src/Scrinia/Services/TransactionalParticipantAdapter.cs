using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Services;

internal sealed class TransactionalParticipantException : Exception
{
    internal TransactionalParticipantException(TransactionalParticipantFailure failure, TransactionalRecoveryEvidence? evidence, bool ambiguous)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
        Evidence = evidence; Ambiguous = ambiguous;
    }
    internal TransactionalParticipantFailure Failure { get; }
    internal TransactionalRecoveryEvidence? Evidence { get; }
    internal bool Ambiguous { get; }
}

internal sealed class TransactionalParticipantAdapter : IMultiMemoryParticipantAdapter
{
    private readonly ITransactionalMemoryParticipant _participant;
    private readonly string _transactionId;
    private readonly string _scope;
    private readonly MemoryAddress _address;
    private readonly string _operationId;
    private readonly string _kind;
    private readonly string _participantIdentity;
    private readonly int _contractVersion;
    private readonly string _orderingKey;
    private readonly IReadOnlyList<string> _capabilities;
    private readonly IReadOnlyDictionary<string, string> _payload;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly RetryBudget _retryBudget;
    private TransactionalPreparedHandle? _preparedHandle;
    private TransactionalParticipantFailure? _lastFailure;
    private TransactionalRecoveryEvidence? _lastEvidence;
    private bool _lastAmbiguous;
    private string _evidencePath = string.Empty;
    private TransactionalParticipantLifecycleState _lifecycleState = TransactionalParticipantLifecycleState.Prepared;
    private bool _stateMayHaveChanged;
    private bool _ambiguous;
    private TransactionalQuarantineState _quarantineState;
    private string? _quarantineReason;

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, MultiMemoryParticipantRequest request)
    {
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _transactionId = request.TransactionId ?? throw new MultiMemoryParticipantMismatchException();
        _scope = request.Scope ?? throw new MultiMemoryParticipantMismatchException();
        _address = request.Address; _operationId = request.OperationId; _kind = request.Kind;
        _participantIdentity = request.ParticipantIdentity ?? throw new MultiMemoryParticipantMismatchException();
        _contractVersion = request.ContractVersion ?? throw new MultiMemoryParticipantMismatchException();
        _orderingKey = request.OrderingKey ?? throw new MultiMemoryParticipantMismatchException();
        _capabilities = participant.Descriptor.Capabilities.ToArray();
        _payload = request.Payload ?? throw new MultiMemoryParticipantMismatchException();
        _metadata = request.Metadata ?? throw new MultiMemoryParticipantMismatchException();
        _retryBudget = CreateRetryBudget(participant.Descriptor.RetryPolicy);
    }

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, B4ParticipantRecoveryState state)
    {
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _transactionId = state.TransactionId; _scope = state.Scope; _address = MemoryAddress.Parse(state.Address);
        _operationId = state.OperationId; _kind = state.Kind;
        _participantIdentity = state.ParticipantIdentity; _contractVersion = state.ContractVersion;
        _orderingKey = state.OrderingKey; _capabilities = state.Capabilities;
        _payload = state.Payload; _metadata = state.Metadata;
        _preparedHandle = new(state.PreparedHandleReference!, state.PreparedHandleVersion!);
        _retryBudget = CreateRetryBudget(participant.Descriptor.RetryPolicy);
        _lifecycleState = state.LifecycleState; _stateMayHaveChanged = state.StateMayHaveChanged;
        _ambiguous = state.Ambiguous; _quarantineState = state.QuarantineState;
        _quarantineReason = state.QuarantineReason; _lastFailure = state.LastFailureCode is { } code
            ? new((Scrinia.Plugin.Abstractions.TransactionalFailureCode)(int)code, "recovered failure", state.StateMayHaveChanged) : null;
        _evidencePath = state.EvidenceReference ?? string.Empty;
    }

    public string Kind => _kind;
    public string Scope => _scope;
    public string Subject => _address.ToString();
    public string OperationId => _operationId;
    public string EvidencePath => _evidencePath;
    public B4ParticipantRecoveryState? RecoveryState => CreateRecoveryState();

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ClearCapturedFailure();
        var retry = await _retryBudget.ExecuteAsync(
            token => _participant.PrepareAsync(new(_transactionId, _scope, _address, _operationId, _kind,
                [new(_address, _operationId, _kind)]), token),
            new PrepareClassifier(CaptureFailure, this), cancellationToken).ConfigureAwait(false);
        if (retry.Value?.Handle is null) throw CreateTypedFailure(retry);
        _preparedHandle = retry.Value.Handle;
        _lifecycleState = TransactionalParticipantLifecycleState.Prepared;
    }

    public async Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken)
    {
        ClearCapturedFailure();
        var retry = await _retryBudget.ExecuteAsync(
            token => _participant.GetCommitStatusAsync(CreateCommitContext(), token),
            new CommitStatusClassifier(CaptureFailure, this), cancellationToken).ConfigureAwait(false);
        if (retry.Value is null) throw CreateTypedFailure(retry);
        return retry.Value.State switch
        {
            TransactionalCommitStatus.Prepared => MultiMemoryCommitStatus.Prepared,
            TransactionalCommitStatus.Committed => MultiMemoryCommitStatus.Committed,
            _ => throw CreateTypedFailure(retry)
        };
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ClearCapturedFailure();
        var retry = await _retryBudget.ExecuteAsync(
            token => _participant.CommitAsync(CreateCommitContext(), token),
            new ParticipantResultClassifier(CaptureFailure, this), cancellationToken).ConfigureAwait(false);
        if (retry.Value?.State != TransactionalParticipantState.Committed) throw CreateTypedFailure(retry);
        _lifecycleState = TransactionalParticipantLifecycleState.Committed;
    }

    public async Task VerifyCommittedAsync(CancellationToken cancellationToken)
    {
        if (await GetCommitStatusAsync(cancellationToken).ConfigureAwait(false) != MultiMemoryCommitStatus.Committed)
            throw CreateTypedFailure(new RetryResult<TransactionalCommitStatusResult>(default, 1, TimeSpan.Zero,
                Scrinia.Core.TransactionalFailureCode.VerificationFailed, true, _lastAmbiguous, false, false));
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        ClearCapturedFailure();
        var retry = await _retryBudget.ExecuteAsync(
            token => _participant.RollbackAsync(CreateRollbackContext(), token),
            new ParticipantResultClassifier(CaptureFailure, this), cancellationToken).ConfigureAwait(false);
        if (retry.Value?.State != TransactionalParticipantState.RolledBack) throw CreateTypedFailure(retry);
        _lifecycleState = TransactionalParticipantLifecycleState.RolledBack;
    }

    public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private TransactionalCommitContext CreateCommitContext() =>
        new(_transactionId, _scope, _address, _operationId, _kind, _preparedHandle ?? throw new MultiMemoryParticipantMismatchException());

    private TransactionalRollbackContext CreateRollbackContext() =>
        new(_transactionId, _scope, _address, _operationId, _kind, _preparedHandle ?? throw new MultiMemoryParticipantMismatchException());

    internal static RetryClassification ClassifyReturnedFailure(TransactionalPrepareResult? result, bool callbackStarted) =>
        result is null ? InvalidResponse() :
        result.Handle is not null ? new(RetryTerminalState.Success, null, false, false, false, false, null) :
        result.Failure is null ? InvalidResponse() : ClassifyFailure(result.Failure, callbackStarted);

    internal static RetryClassification ClassifyReturnedFailure(TransactionalParticipantResult? result, bool callbackStarted) =>
        result is null ? InvalidResponse() :
        result.State is TransactionalParticipantState.Committed or TransactionalParticipantState.RolledBack
            ? new(RetryTerminalState.Success, null, false, false, false, false, null)
            : result.Failure is null ? InvalidResponse() : ClassifyFailure(result.Failure, callbackStarted);

    internal static RetryClassification ClassifyReturnedFailure(TransactionalCommitStatusResult? result, bool callbackStarted) =>
        result is null ? InvalidResponse() :
        result.State is TransactionalCommitStatus.Prepared or TransactionalCommitStatus.Committed
            ? new(RetryTerminalState.Success, null, false, false, false, false, null)
            : result.Failure is null ? InvalidResponse() : ClassifyFailure(result.Failure, callbackStarted);

    private static RetryClassification InvalidResponse() =>
        new(RetryTerminalState.Success, Scrinia.Core.TransactionalFailureCode.InvalidResponse,
            true, true, false, false, "Callback returned an invalid result.");

    internal static RetryClassification ClassifyThrownException(Exception exception, bool callbackStarted)
    {
        if (exception is OperationCanceledException)
            return new(RetryTerminalState.CancellationAfterCallbackStart, Scrinia.Core.TransactionalFailureCode.Cancelled,
                callbackStarted, callbackStarted, false, true, null);
        if (exception is TransactionalParticipantException typed)
            return ClassifyFailure(typed.Failure, callbackStarted, typed.Ambiguous);
        return new(RetryTerminalState.Success, Scrinia.Core.TransactionalFailureCode.Permanent, true, true, false, false, null);
    }

    private static RetryClassification ClassifyFailure(TransactionalParticipantFailure failure, bool callbackStarted,
        bool? ambiguous = null) =>
        new(RetryTerminalState.Success, ToCoreCode(failure.Code), failure.StateMayHaveChanged,
            ambiguous ?? failure.StateMayHaveChanged,
            failure.Code == Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient,
            failure.Code == Scrinia.Plugin.Abstractions.TransactionalFailureCode.Cancelled, failure.Detail);

    private static Scrinia.Core.TransactionalFailureCode ToCoreCode(Scrinia.Plugin.Abstractions.TransactionalFailureCode code) =>
        (Scrinia.Core.TransactionalFailureCode)(int)code;

    private static RetryBudget CreateRetryBudget(Scrinia.Plugin.Abstractions.TransactionalRetryPolicy policy) =>
        new(new Scrinia.Core.TransactionalRetryPolicy(policy.MaxAttempts, policy.MaxElapsed, policy.InitialDelay, policy.MaximumDelay),
            new SystemMonotonicClock(), (delay, token) => new(Task.Delay(delay, token)));

    private TransactionalParticipantException CreateTypedFailure<T>(RetryResult<T> result) where T : class =>
        new(_lastFailure ?? new((Scrinia.Plugin.Abstractions.TransactionalFailureCode)(int)(result.FailureCode ?? Scrinia.Core.TransactionalFailureCode.InvalidResponse),
            "Transactional participant callback did not complete successfully.", result.StateMayHaveChanged),
            _lastEvidence, _lastAmbiguous || result.Ambiguous);

    private void CaptureFailure(TransactionalParticipantFailure? failure, TransactionalRecoveryEvidence? evidence)
    {
        _lastFailure = failure;
        _lastEvidence = evidence;
        _lastAmbiguous = failure?.StateMayHaveChanged == true && evidence is not null;
        _evidencePath = evidence?.EvidenceReference ?? string.Empty;
        _stateMayHaveChanged = failure?.StateMayHaveChanged == true;
        _ambiguous = _lastAmbiguous;
        if (_ambiguous)
        {
            _lifecycleState = TransactionalParticipantLifecycleState.Ambiguous;
            _quarantineState = TransactionalQuarantineState.Quarantined;
            _quarantineReason = "ambiguous participant state";
        }
    }

    private void CaptureException(Exception exception)
    {
        if (exception is TransactionalParticipantException typed)
        {
            _lastFailure = typed.Failure;
            _lastEvidence = typed.Evidence;
            _lastAmbiguous = typed.Ambiguous;
            _evidencePath = typed.Evidence?.EvidenceReference ?? string.Empty;
            _stateMayHaveChanged = typed.Failure.StateMayHaveChanged;
            _ambiguous = typed.Ambiguous;
            if (_ambiguous)
            {
                _lifecycleState = TransactionalParticipantLifecycleState.Ambiguous;
                _quarantineState = TransactionalQuarantineState.Quarantined;
                _quarantineReason = "ambiguous participant state";
            }
            return;
        }

        _lastFailure = null;
        _lastEvidence = null;
        _lastAmbiguous = false;
        _evidencePath = string.Empty;
    }

    private void ClearCapturedFailure()
    {
        _lastFailure = null;
        _lastEvidence = null;
        _lastAmbiguous = false;
        _evidencePath = string.Empty;
    }

    private B4ParticipantRecoveryState? CreateRecoveryState()
    {
        if (_preparedHandle is null && _lifecycleState is TransactionalParticipantLifecycleState.Prepared or TransactionalParticipantLifecycleState.Committed)
            return null;
        Scrinia.Core.TransactionalFailureCode? failureCode = _lastFailure is null ? null : (Scrinia.Core.TransactionalFailureCode)(int)_lastFailure.Code;
        string? evidenceReference = _lastEvidence?.EvidenceReference ?? (_evidencePath.Length == 0 ? null : _evidencePath);
        return new B4ParticipantRecoveryState(_transactionId, _operationId, _scope, _address.ToString(),
            _participantIdentity, _contractVersion, _orderingKey, _kind, _capabilities, _payload, _metadata,
            _lifecycleState, _preparedHandle?.Reference, _preparedHandle?.Version, 0, 0,
            _stateMayHaveChanged, _ambiguous, failureCode, evidenceReference,
            _lastEvidence?.EvidenceKind, _lastEvidence?.EvidenceVersion, _quarantineState, _quarantineReason);
    }

    private sealed class PrepareClassifier : IRetryClassifier<TransactionalPrepareResult>
    {
        private readonly Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> _capture;
        private readonly TransactionalParticipantAdapter _owner;
        internal PrepareClassifier(Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> capture,
            TransactionalParticipantAdapter owner) { _capture = capture; _owner = owner; }
        public RetryClassification Classify(TransactionalPrepareResult result, bool callbackStarted)
        {
            _capture(result?.Failure, result?.Evidence);
            return ClassifyReturnedFailure(result, callbackStarted);
        }
        public RetryClassification Classify(Exception exception, bool callbackStarted) { _owner.CaptureException(exception); return ClassifyThrownException(exception, callbackStarted); }

    }

    private sealed class ParticipantResultClassifier : IRetryClassifier<TransactionalParticipantResult>
    {
        private readonly Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> _capture;
        private readonly TransactionalParticipantAdapter _owner;
        internal ParticipantResultClassifier(Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> capture,
            TransactionalParticipantAdapter owner) { _capture = capture; _owner = owner; }
        public RetryClassification Classify(TransactionalParticipantResult result, bool callbackStarted)
        {
            _capture(result?.Failure, result?.Evidence);
            return ClassifyReturnedFailure(result, callbackStarted);
        }
        public RetryClassification Classify(Exception exception, bool callbackStarted) { _owner.CaptureException(exception); return ClassifyThrownException(exception, callbackStarted); }

    }

    private sealed class CommitStatusClassifier : IRetryClassifier<TransactionalCommitStatusResult>
    {
        private readonly Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> _capture;
        private readonly TransactionalParticipantAdapter _owner;
        internal CommitStatusClassifier(Action<TransactionalParticipantFailure?, TransactionalRecoveryEvidence?> capture,
            TransactionalParticipantAdapter owner) { _capture = capture; _owner = owner; }
        public RetryClassification Classify(TransactionalCommitStatusResult result, bool callbackStarted)
        {
            _capture(result?.Failure, result?.Evidence);
            return ClassifyReturnedFailure(result, callbackStarted);
        }
        public RetryClassification Classify(Exception exception, bool callbackStarted) { _owner.CaptureException(exception); return ClassifyThrownException(exception, callbackStarted); }

    }
}
