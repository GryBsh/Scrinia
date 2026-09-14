using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Services;

internal sealed class TransactionalParticipantException : Exception
{
    internal TransactionalParticipantException(TransactionalParticipantFailure failure) => Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    internal TransactionalParticipantFailure Failure { get; }
}

internal sealed class TransactionalParticipantAdapter : IMultiMemoryParticipantAdapter
{
    private readonly ITransactionalMemoryParticipant _participant;
    private readonly string _transactionId;
    private readonly string _scope;
    private readonly MemoryAddress _address;
    private readonly string _operationId;
    private readonly string _kind;
    private readonly RetryBudget _retryBudget;
    private TransactionalPreparedHandle? _preparedHandle;

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, MultiMemoryParticipantRequest request)
    {
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _transactionId = request.TransactionId ?? throw new MultiMemoryParticipantMismatchException();
        _scope = request.Scope ?? throw new MultiMemoryParticipantMismatchException();
        _address = request.Address; _operationId = request.OperationId; _kind = request.Kind;
        _retryBudget = CreateRetryBudget(participant.Descriptor.RetryPolicy);
    }

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, B4ParticipantRecoveryState state)
    {
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _transactionId = state.TransactionId; _scope = state.Scope; _address = MemoryAddress.Parse(state.Address);
        _operationId = state.OperationId; _kind = state.Kind;
        _preparedHandle = new(state.PreparedHandleReference!, state.PreparedHandleVersion!);
        _retryBudget = CreateRetryBudget(participant.Descriptor.RetryPolicy);
    }

    public string Kind => _kind;
    public string Scope => _scope;
    public string Subject => _address.ToString();
    public string OperationId => _operationId;
    public string EvidencePath => string.Empty;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var retry = await _retryBudget.ExecuteAsync(
            token => _participant.PrepareAsync(new(_transactionId, _scope, _address, _operationId, _kind,
                [new(_address, _operationId, _kind)]), token),
            new PrepareClassifier(), cancellationToken).ConfigureAwait(false);
        if (retry.Value?.Handle is null) throw new MultiMemoryParticipantMismatchException();
        _preparedHandle = retry.Value.Handle;
    }

    public async Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _participant.GetCommitStatusAsync(CreateCommitContext(), cancellationToken).ConfigureAwait(false);
        return result.State switch
        {
            TransactionalCommitStatus.Prepared => MultiMemoryCommitStatus.Prepared,
            TransactionalCommitStatus.Committed => MultiMemoryCommitStatus.Committed,
            _ => MultiMemoryCommitStatus.Unknown
        };
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var result = await _participant.CommitAsync(CreateCommitContext(), cancellationToken).ConfigureAwait(false);
        if (result.State != TransactionalParticipantState.Committed) throw new MultiMemoryParticipantMismatchException();
    }

    public async Task VerifyCommittedAsync(CancellationToken cancellationToken)
    {
        if (await GetCommitStatusAsync(cancellationToken).ConfigureAwait(false) != MultiMemoryCommitStatus.Committed)
            throw new MultiMemoryParticipantMismatchException();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var result = await _participant.RollbackAsync(CreateRollbackContext(), cancellationToken).ConfigureAwait(false);
        if (result.State != TransactionalParticipantState.RolledBack) throw new MultiMemoryParticipantMismatchException();
    }

    public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private TransactionalCommitContext CreateCommitContext() =>
        new(_transactionId, _scope, _address, _operationId, _kind, _preparedHandle ?? throw new MultiMemoryParticipantMismatchException());

    private TransactionalRollbackContext CreateRollbackContext() =>
        new(_transactionId, _scope, _address, _operationId, _kind, _preparedHandle ?? throw new MultiMemoryParticipantMismatchException());

    internal static RetryClassification ClassifyReturnedFailure(TransactionalPrepareResult result, bool callbackStarted) =>
        result.Handle is not null ? new(RetryTerminalState.Success, null, false, false, false, false, null) :
        ClassifyFailure(result.Failure!, callbackStarted);

    internal static RetryClassification ClassifyReturnedFailure(TransactionalParticipantResult result, bool callbackStarted) =>
        result.State is TransactionalParticipantState.Committed or TransactionalParticipantState.RolledBack
            ? new(RetryTerminalState.Success, null, false, false, false, false, null)
            : ClassifyFailure(result.Failure!, callbackStarted);

    internal static RetryClassification ClassifyReturnedFailure(TransactionalCommitStatusResult result, bool callbackStarted) =>
        result.State is TransactionalCommitStatus.Prepared or TransactionalCommitStatus.Committed
            ? new(RetryTerminalState.Success, null, false, false, false, false, null)
            : ClassifyFailure(result.Failure!, callbackStarted);

    internal static RetryClassification ClassifyThrownException(Exception exception, bool callbackStarted)
    {
        if (exception is OperationCanceledException)
            return new(RetryTerminalState.CancellationAfterCallbackStart, Scrinia.Core.TransactionalFailureCode.Cancelled,
                callbackStarted, callbackStarted, false, true, null);
        if (exception is TransactionalParticipantException typed)
            return ClassifyFailure(typed.Failure, callbackStarted);
        return new(RetryTerminalState.Success, Scrinia.Core.TransactionalFailureCode.Permanent, true, true, false, false, null);
    }

    private static RetryClassification ClassifyFailure(TransactionalParticipantFailure failure, bool callbackStarted) =>
        new(RetryTerminalState.Success, ToCoreCode(failure.Code), failure.StateMayHaveChanged,
            failure.StateMayHaveChanged, failure.Code == Scrinia.Plugin.Abstractions.TransactionalFailureCode.DeclaredTransient,
            failure.Code == Scrinia.Plugin.Abstractions.TransactionalFailureCode.Cancelled, failure.Detail);

    private static Scrinia.Core.TransactionalFailureCode ToCoreCode(Scrinia.Plugin.Abstractions.TransactionalFailureCode code) =>
        (Scrinia.Core.TransactionalFailureCode)(int)code;

    private static RetryBudget CreateRetryBudget(Scrinia.Plugin.Abstractions.TransactionalRetryPolicy policy) =>
        new(new Scrinia.Core.TransactionalRetryPolicy(policy.MaxAttempts, policy.MaxElapsed, policy.InitialDelay, policy.MaximumDelay),
            new SystemMonotonicClock(), (delay, token) => new(Task.Delay(delay, token)));

    private sealed class PrepareClassifier : IRetryClassifier<TransactionalPrepareResult>
    {
        public RetryClassification Classify(TransactionalPrepareResult result, bool callbackStarted) => ClassifyReturnedFailure(result, callbackStarted);
        public RetryClassification Classify(Exception exception, bool callbackStarted) => ClassifyThrownException(exception, callbackStarted);
    }
}
