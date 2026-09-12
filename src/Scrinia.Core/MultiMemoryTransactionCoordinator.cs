using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Owns the single-scope admission boundary for a multi-memory transaction.</summary>
internal sealed class MultiMemoryTransactionCoordinator : IMultiMemoryTransactionCoordinator
{
    private readonly FileMemoryStore _store;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>? _recoveryAdapter;
    private readonly Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>? _scopedRecoveryAdapter;

    internal static AsyncLocal<Func<MultiMemoryTransactionTransition, string, Exception?>?> FailureInjector { get; } = new();

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store, TimeSpan? lockTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (_lockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "The lock timeout must be positive.");
    }

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        Func<MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> recoveryAdapter,
        TimeSpan? lockTimeout = null)
        : this(store, lockTimeout) => _recoveryAdapter = recoveryAdapter ?? throw new ArgumentNullException(nameof(recoveryAdapter));

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> recoveryAdapter,
        TimeSpan? lockTimeout = null)
        : this(store, lockTimeout) => _scopedRecoveryAdapter = recoveryAdapter ?? throw new ArgumentNullException(nameof(recoveryAdapter));

    internal static IReadOnlyList<MultiMemoryParticipantRequest> NormalizeAndOrder(
        FileMemoryStore store, MultiMemoryTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);

        var normalized = new List<MultiMemoryParticipantRequest>(request.Participants.Count);
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (MultiMemoryParticipantRequest participant in request.Participants)
        {
            if (participant.Address.Kind != MemoryAddressKind.Persistent)
                throw AdmissionFailure(MultiMemoryFailure.Admission, "Ephemeral participants are not supported.");

            (string scope, string subject) = store.ResolveAddress(participant.Address);
            if (scope.Equals("local:agent", StringComparison.Ordinal) ||
                scope.Equals("local:skills", StringComparison.Ordinal))
                throw AdmissionFailure(MultiMemoryFailure.Admission, "File-entity participants are not supported.");
            if (!scope.Equals(request.Scope, StringComparison.Ordinal))
                throw AdmissionFailure(MultiMemoryFailure.Admission, "All participants must belong to the transaction scope.");
            if (!subjects.Add(subject))
                throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "Participant subjects must be unique.");
            if (!operationIds.Add(participant.OperationId))
                throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "Participant operation IDs must be unique.");

            normalized.Add(participant);
        }

        return normalized
            .OrderBy(participant => store.ResolveAddress(participant.Address).Subject, StringComparer.Ordinal)
            .ThenBy(participant => participant.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Admits typed B2 participant handles without beginning their lifecycle.</summary>
    internal async Task<MultiMemoryAdmissionResult> AdmitAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admitParticipant);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MultiMemoryParticipantRequest> ordered = NormalizeAndOrder(_store, request);

        try
        {
            return await _store.WithExclusiveScopeAdmissionAsync(request.Scope, _lockTimeout, async () =>
            {
                string storeDirectory = _store.GetStoreDirForScope(request.Scope);
                if (Directory.Exists(storeDirectory))
                {
                    RecoveryDiscoveryResult discovery = _store.DiscoverRecoveryForScope(request.Scope, storeDirectory);
                    if (discovery.BlockingEvidence is not null)
                        throw AdmissionFailure(MapRecoveryFailure(discovery.BlockingEvidence.Reason),
                            "Existing recovery evidence blocks admission.",
                            ToSafeEvidencePath(storeDirectory, discovery.BlockingEvidence.EvidencePath));
                    if (discovery.CoordinatorRecord is not null || discovery.Candidates.Count > 0)
                        throw AdmissionFailure(MultiMemoryFailure.Admission,
                            "Existing recovery evidence must be resolved before admission.");
                }

                var adapters = new List<IMultiMemoryParticipantAdapter>(ordered.Count);
                try
                {
                    foreach (MultiMemoryParticipantRequest participant in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IMultiMemoryParticipantAdapter adapter = await admitParticipant(participant, cancellationToken)
                            .ConfigureAwait(false);
                        if (adapter is null)
                            throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "The participant adapter was not supplied.");
                        (string scope, string subject) = _store.ResolveAddress(participant.Address);
                        if (!scope.Equals(request.Scope, StringComparison.Ordinal) ||
                            !subject.Equals(adapter.Subject, StringComparison.Ordinal) ||
                            !participant.OperationId.Equals(adapter.OperationId, StringComparison.Ordinal) ||
                            !participant.Kind.Equals(adapter.Kind, StringComparison.Ordinal))
                        {
                            adapters.Add(adapter);
                            throw AdmissionFailure(MultiMemoryFailure.Inconsistent,
                                "The participant adapter identity does not match the request.");
                        }
                        adapters.Add(adapter);
                    }
                }
                catch (Exception failure)
                {
                    bool cleanupFailed = DisposeAdapters(adapters);
                    if (failure is OperationCanceledException)
                        throw;
                    if (failure is MultiMemoryAdmissionException admissionFailure)
                    {
                        admissionFailure.CleanupFailed = cleanupFailed;
                        throw admissionFailure;
                    }
                    throw new MultiMemoryAdmissionException(
                        "A participant could not be admitted safely.", MultiMemoryFailure.Admission,
                        cleanupFailed: cleanupFailed);
                }

                return new MultiMemoryAdmissionResult(adapters.AsReadOnly());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileLockTimeoutException)
        {
            return new MultiMemoryAdmissionResult(MultiMemoryFailure.LockTimeout,
                "The transaction scope lock could not be acquired.");
        }
        catch (MultiMemoryAdmissionException exception)
        {
            return new MultiMemoryAdmissionResult(exception.Failure, exception.Message, exception.EvidencePath,
                exception.CleanupFailed);
        }
        catch (RestoreRecoveryException exception) when (exception.DiscoveryBlock is not null)
        {
            RecoveryBlock block = exception.DiscoveryBlock;
            return new MultiMemoryAdmissionResult(MapRecoveryFailure(block.Reason),
                "Existing recovery evidence blocks admission.",
                ToSafeEvidencePath(_store.GetStoreDirForScope(request.Scope), block.EvidencePath));
        }
        catch (IOException)
        {
            return new MultiMemoryAdmissionResult(MultiMemoryFailure.ReplayIoFailed,
                "The transaction scope could not be inspected safely.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MultiMemoryAdmissionResult(MultiMemoryFailure.Admission,
                "A participant could not be admitted safely.");
        }
    }

    public async Task<MultiMemoryAggregateResult> ExecuteAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admitParticipant);
        string transactionId = Guid.NewGuid().ToString("N");
        try
        {
            return await _store.WithExclusiveScopeAdmissionAsync(request.Scope, _lockTimeout, async () =>
            {
                IReadOnlyList<MultiMemoryParticipantRequest> ordered = NormalizeAndOrder(_store, request);
                MultiMemoryAdmissionResult admission = await AdmitUnderLockAsync(request, ordered, admitParticipant, cancellationToken).ConfigureAwait(false);
                if (!admission.IsAdmitted)
                    return Rejected(transactionId, request.Scope, admission.Failure!.Value,
                        AddCleanupWarning(admission.Warning!, admission.CleanupFailed), admission.EvidencePath);

                return await ExecuteUnderLockAsync(transactionId, request, admission.Participants, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileLockTimeoutException)
        {
            return Rejected(transactionId, request.Scope, MultiMemoryFailure.LockTimeout,
                "The transaction scope lock could not be acquired.");
        }
        catch (ArgumentException)
        {
            return Rejected(transactionId, request.Scope, MultiMemoryFailure.Admission,
                "Transaction participants could not be resolved safely.");
        }
        catch (RestoreRecoveryException exception) when (exception.DiscoveryBlock is not null)
        {
            RecoveryBlock block = exception.DiscoveryBlock;
            return new MultiMemoryAggregateResult(transactionId, request.Scope, MultiMemoryOutcome.RecoveryRequired, [],
                MapRecoveryFailure(block.Reason), block.Detail,
                ToSafeEvidencePath(_store.GetStoreDirForScope(request.Scope), block.EvidencePath));
        }
        catch (IOException)
        {
            return Rejected(transactionId, request.Scope, MultiMemoryFailure.ReplayIoFailed,
                "The transaction scope could not be inspected safely.");
        }
    }

    public async Task<MultiMemoryAggregateResult> RecoverAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A recovery scope is required.", nameof(scope));
        string transactionId = "recovery";
        try
        {
            return await _store.WithExclusiveScopeAdmissionAsync(scope, _lockTimeout,
                () => RecoverUnderLockAsync(scope, cancellationToken), cancellationToken, recoverCoordinator: false).ConfigureAwait(false);
        }
        catch (FileLockTimeoutException)
        {
            return Rejected(transactionId, scope, MultiMemoryFailure.LockTimeout, "The transaction scope lock could not be acquired.");
        }
        catch (RestoreRecoveryException exception) when (exception.DiscoveryBlock is not null)
        {
            RecoveryBlock block = exception.DiscoveryBlock;
            return new MultiMemoryAggregateResult(transactionId, scope, MultiMemoryOutcome.RecoveryRequired, [],
                MapRecoveryFailure(block.Reason), FailureDetail(MapRecoveryFailure(block.Reason)),
                ToSafeEvidencePath(_store.GetStoreDirForScope(scope), block.EvidencePath));
        }
    }

    internal async Task<MultiMemoryAggregateResult> RecoverUnderLockAsync(string scope, CancellationToken cancellationToken)
    {
        string directory = _store.GetStoreDirForScope(scope);
        if (!File.Exists(MultiMemoryTransactionRecordCodec.GetPath(directory)))
            return Rejected("recovery", scope, MultiMemoryFailure.ReplayIoFailed, "No coordinator decision record exists.");

        MultiMemoryTransactionRecord record;
        try { record = MultiMemoryTransactionRecordCodec.Read(directory, scope); }
        catch (MultiMemoryTransactionRecordException exception)
        {
            return new MultiMemoryAggregateResult(exception.TransactionId ?? "unknown", scope,
                MultiMemoryOutcome.RecoveryRequired, [], exception.Failure, FailureDetail(exception.Failure),
                SafeRecordPath(directory));
        }
        if (_recoveryAdapter is null && _scopedRecoveryAdapter is null)
            return new MultiMemoryAggregateResult(record.TransactionId, scope, MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Ambiguous, "Participant adapter resolution is unavailable; evidence is retained.", SafeRecordPath(directory));

        var adapters = new List<IMultiMemoryParticipantAdapter>();
        try
        {
            foreach (MultiMemoryParticipantManifest manifest in record.Participants)
            {
                IMultiMemoryParticipantAdapter adapter = await (_scopedRecoveryAdapter is not null
                    ? _scopedRecoveryAdapter(record.Scope, manifest, cancellationToken)
                    : _recoveryAdapter!(manifest, cancellationToken)).ConfigureAwait(false)
                    ?? throw new MultiMemoryParticipantMismatchException();
                ValidateRecoveryAdapter(record.Scope, manifest, adapter);
                adapters.Add(adapter);
            }
            if (record.State == MultiMemoryTransactionState.Prepared)
                return await AbortPreparedAsync(record, adapters, directory, cancellationToken).ConfigureAwait(false);
            if (record.State == MultiMemoryTransactionState.RecoveryRequired)
                return RecoveryRequired(record, adapters, MultiMemoryFailure.Ambiguous,
                    "The coordinator record requires explicit recovery before participant replay.", directory);
            return await FinishDecisionAsync(record, adapters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return RecoveryRequired(record, adapters, Classify(exception),
                "Participant recovery could not be proven safe.", directory);
        }
        finally { DisposeAdapters(adapters); }
    }

    private async Task<MultiMemoryAggregateResult> AbortPreparedAsync(MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, string directory, CancellationToken cancellationToken)
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>();
        foreach (IMultiMemoryParticipantAdapter adapter in adapters.Reverse())
        {
            try
            {
                await adapter.RollbackAsync(cancellationToken).ConfigureAwait(false);
                outcomes.Add(new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId, adapter.Kind,
                    adapter.EvidencePath, false, true, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return RecoveryRequired(record, adapters, Classify(exception),
                    "Prepared decision could not be safely aborted; evidence is retained.", directory);
            }
        }
        try { DeleteIfPresent(directory, MultiMemoryTransactionTransition.RecordDeleted); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope,
                MultiMemoryOutcome.RecoveryRequired, outcomes, MultiMemoryFailure.Cleanup,
                "Prepared decision was aborted but its evidence could not be removed.", SafeRecordPath(directory));
        }
        return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, MultiMemoryOutcome.RolledBack,
            outcomes, null, null, null);
    }

    private async Task<MultiMemoryAdmissionResult> AdmitUnderLockAsync(
        MultiMemoryTransactionRequest request, IReadOnlyList<MultiMemoryParticipantRequest> ordered,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken)
    {
        var adapters = new List<IMultiMemoryParticipantAdapter>(ordered.Count);
        try
        {
            foreach (MultiMemoryParticipantRequest participant in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMultiMemoryParticipantAdapter adapter = await admitParticipant(participant, cancellationToken).ConfigureAwait(false)
                    ?? throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "The participant adapter was not supplied.");
                (string scope, string subject) = _store.ResolveAddress(participant.Address);
                if (scope != request.Scope || adapter.Scope != request.Scope || subject != adapter.Subject ||
                    participant.OperationId != adapter.OperationId || participant.Kind != adapter.Kind ||
                    !MultiMemoryParticipantOutcome.IsSafeEvidencePath(adapter.EvidencePath))
                    throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "The participant adapter identity does not match the request.");
                adapters.Add(adapter);
            }
            return new MultiMemoryAdmissionResult(adapters.AsReadOnly());
        }
        catch (MultiMemoryAdmissionException exception)
        {
            bool cleanupFailed = DisposeAdapters(adapters);
            exception.CleanupFailed = cleanupFailed;
            return new MultiMemoryAdmissionResult(exception.Failure, exception.Message, exception.EvidencePath, cleanupFailed);
        }
        catch (OperationCanceledException) { DisposeAdapters(adapters); throw; }
        catch (Exception exception)
        {
            bool cleanupFailed = DisposeAdapters(adapters);
            return new MultiMemoryAdmissionResult(MultiMemoryFailure.Admission, exception.Message, cleanupFailed: cleanupFailed);
        }
    }

    private async Task<MultiMemoryAggregateResult> ExecuteUnderLockAsync(string transactionId,
        MultiMemoryTransactionRequest request, IReadOnlyList<IMultiMemoryParticipantAdapter> adapters,
        CancellationToken cancellationToken)
    {
        string directory = _store.GetStoreDirForScope(request.Scope);
        var manifests = adapters.Select(a => new MultiMemoryParticipantManifest(a.Subject, a.OperationId, a.Kind)).ToArray();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
            transactionId, request.Scope, MultiMemoryTransactionState.Prepared, manifests, now, now);
        bool decisionPublished = false;
        try
        {
            foreach (IMultiMemoryParticipantAdapter adapter in adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await adapter.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }
            Publish(directory, record, MultiMemoryTransactionTransition.PreparedPublished);
            record = MultiMemoryTransactionRecordCodec.WithState(record, MultiMemoryTransactionState.Committed, DateTimeOffset.UtcNow);
            try
            {
                Publish(directory, record, MultiMemoryTransactionTransition.CommittedPublished);
                decisionPublished = true;
            }
            catch (Exception)
            {
                // Atomic replacement may have succeeded before reporting an I/O failure. The
                // durable decision is therefore treated as possibly published and never rolled back.
                decisionPublished = true;
                return RecoveryRequired(record, adapters, MultiMemoryFailure.DecisionPublication,
                    "The committed decision could not be proven absent; evidence is retained.", directory);
            }
            return await FinishDecisionAsync(record, adapters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (decisionPublished) return RecoveryRequired(record, adapters, MultiMemoryFailure.Ambiguous, "Commit was cancelled after the durable decision.", directory);
            return await RollbackBeforeDecisionAsync(transactionId, request.Scope, adapters, directory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (decisionPublished) return RecoveryRequired(record, adapters, Classify(exception),
                FailureDetail(Classify(exception)), directory);
            return await RollbackBeforeDecisionAsync(transactionId, request.Scope, adapters, directory, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<MultiMemoryAggregateResult> FinishDecisionAsync(MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, CancellationToken cancellationToken)
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>();
        bool allParticipantsVerified = false;
        try
        {
            foreach (IMultiMemoryParticipantAdapter adapter in adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MultiMemoryCommitStatus status = await adapter.GetCommitStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status != MultiMemoryCommitStatus.Committed)
                {
                    try { await adapter.CommitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) { throw new MultiMemoryCommitException(exception); }
                }
                try
                {
                    await adapter.VerifyCommittedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    outcomes.Add(new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId, adapter.Kind,
                        adapter.EvidencePath, false, false, FailureDetail(MultiMemoryFailure.Verification)));
                    throw new MultiMemoryVerificationException(exception);
                }
                outcomes.Add(new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId, adapter.Kind,
                    adapter.EvidencePath, true, false, null));
            }
            allParticipantsVerified = true;
            MultiMemoryTransactionRecord proofRecord = MultiMemoryTransactionRecordCodec.WithCompletionProof(
                record, adapters.Select(adapter => new MultiMemoryParticipantCompletion(
                    adapter.Subject, adapter.OperationId, adapter.Kind, adapter.EvidencePath)).ToArray(),
                DateTimeOffset.UtcNow);
            try
            {
                Publish(_store.GetStoreDirForScope(record.Scope), proofRecord,
                    MultiMemoryTransactionTransition.CompletionProofPublished);
                record = proofRecord;
            }
            catch (Exception)
            {
                return RecoveryRequired(record, adapters, MultiMemoryFailure.DecisionPublication,
                    "Participant completion proof could not be durably recorded.",
                    _store.GetStoreDirForScope(record.Scope));
            }
            foreach (IMultiMemoryParticipantAdapter adapter in adapters)
                await adapter.CleanupAsync(cancellationToken).ConfigureAwait(false);
            Delete(record, MultiMemoryTransactionTransition.RecordDeleted);
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, MultiMemoryOutcome.Committed,
                outcomes, null, null, null);
        }
        catch (OperationCanceledException)
        {
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, MultiMemoryOutcome.RecoveryRequired,
                outcomes, MultiMemoryFailure.Ambiguous, "Decision completion was cancelled; evidence is retained.", SafeRecordPath(_store.GetStoreDirForScope(record.Scope)));
        }
        catch (Exception exception)
        {
            MultiMemoryFailure failure = allParticipantsVerified ? MultiMemoryFailure.Cleanup : Classify(exception);
            MultiMemoryOutcome outcome = allParticipantsVerified
                ? MultiMemoryOutcome.CommittedCleanupPending : MultiMemoryOutcome.RecoveryRequired;
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, outcome, outcomes, failure,
                FailureDetail(failure), SafeRecordPath(_store.GetStoreDirForScope(record.Scope)));
        }
    }

    private async Task<MultiMemoryAggregateResult> RollbackBeforeDecisionAsync(string id, string scope,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, string directory, CancellationToken cancellationToken)
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>();
        MultiMemoryFailure? failure = null;
        foreach (IMultiMemoryParticipantAdapter adapter in adapters.Reverse())
        {
            bool rolledBack = false;
            try
            {
                await adapter.RollbackAsync(cancellationToken).ConfigureAwait(false);
                rolledBack = true;
            }
            catch (Exception exception) { failure ??= Classify(exception); }
            outcomes.Add(new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId, adapter.Kind,
                adapter.EvidencePath, false, rolledBack, rolledBack ? null : failure?.ToString()));
        }
        if (failure is null)
        {
            try { DeleteIfPresent(directory, MultiMemoryTransactionTransition.RecordDeleted); }
            catch (Exception) { failure = MultiMemoryFailure.Cleanup; }
        }
        return new MultiMemoryAggregateResult(id, scope, failure is null ? MultiMemoryOutcome.RolledBack : MultiMemoryOutcome.RecoveryRequired,
            outcomes.OrderBy(o => o.Subject, StringComparer.Ordinal).ToArray(), failure,
            failure is null ? null : "Pre-decision rollback could not be proven complete.", failure is null ? null : SafeRecordPath(directory));
    }

    private void Publish(string directory, MultiMemoryTransactionRecord record, MultiMemoryTransactionTransition transition)
    {
        ThrowIfInjected(transition, MultiMemoryTransactionRecordCodec.GetPath(directory));
        MultiMemoryTransactionRecordCodec.Write(directory, record);
    }

    private void Delete(MultiMemoryTransactionRecord record, MultiMemoryTransactionTransition transition) =>
        DeleteIfPresent(_store.GetStoreDirForScope(record.Scope), transition);

    private static void DeleteIfPresent(string directory, MultiMemoryTransactionTransition transition)
    {
        string path = MultiMemoryTransactionRecordCodec.GetPath(directory);
        ThrowIfInjected(transition, path);
        File.Delete(path);
    }

    private static void ThrowIfInjected(MultiMemoryTransactionTransition transition, string path)
    {
        Exception? failure = FailureInjector.Value?.Invoke(transition, path);
        if (failure is not null)
            throw failure;
    }

    private MultiMemoryAggregateResult RecoveryRequired(MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, MultiMemoryFailure failure, string warning, string directory) =>
        new(record.TransactionId, record.Scope, MultiMemoryOutcome.RecoveryRequired,
            adapters.Select(a => new MultiMemoryParticipantOutcome(a.Subject, a.OperationId, a.Kind, a.EvidencePath, false, false, FailureDetail(failure))).ToArray(),
            failure, warning, SafeRecordPath(directory));

    private static string FailureDetail(MultiMemoryFailure failure) => failure switch
    {
        MultiMemoryFailure.ParticipantCommit => "Participant commit failed.",
        MultiMemoryFailure.Verification => "Participant verification failed.",
        MultiMemoryFailure.Cleanup => "Coordinator cleanup remains pending.",
        MultiMemoryFailure.DecisionPublication => "Coordinator decision publication is ambiguous.",
        MultiMemoryFailure.Ambiguous => "Participant recovery is ambiguous.",
        MultiMemoryFailure.UnsupportedVersion => "Coordinator record version is unsupported.",
        MultiMemoryFailure.Malformed => "Coordinator record is malformed.",
        MultiMemoryFailure.Unsafe => "Coordinator evidence is unsafe.",
        MultiMemoryFailure.Inconsistent => "Coordinator evidence is inconsistent.",
        MultiMemoryFailure.ReplayIoFailed => "Coordinator evidence could not be read safely.",
        _ => "Multi-memory recovery requires intervention."
    };

    private static MultiMemoryFailure Classify(Exception exception) => exception switch
    {
        MultiMemoryParticipantMismatchException => MultiMemoryFailure.Ambiguous,
        MultiMemoryCommitException => MultiMemoryFailure.ParticipantCommit,
        MultiMemoryVerificationException => MultiMemoryFailure.Verification,
        OperationCanceledException => MultiMemoryFailure.Ambiguous,
        IOException => MultiMemoryFailure.ReplayIoFailed,
        _ => MultiMemoryFailure.ParticipantCommit
    };

    private static void ValidateRecoveryAdapter(string scope, MultiMemoryParticipantManifest manifest,
        IMultiMemoryParticipantAdapter adapter)
    {
        if (adapter.Scope != scope || adapter.Subject != manifest.Subject ||
            adapter.OperationId != manifest.OperationId || adapter.Kind != manifest.Kind ||
            !MultiMemoryParticipantOutcome.IsSafeEvidencePath(adapter.EvidencePath))
            throw new MultiMemoryParticipantMismatchException();
    }

    private sealed class MultiMemoryParticipantMismatchException : Exception;

    private sealed class MultiMemoryCommitException : Exception
    {
        internal MultiMemoryCommitException(Exception inner) : base("Participant commit failed.", inner) { }
    }

    private sealed class MultiMemoryVerificationException : Exception
    {
        internal MultiMemoryVerificationException(Exception inner) : base("Participant commit verification failed.", inner) { }
    }

    private string? SafeRecordPath(string directory) => MultiMemoryParticipantOutcome.IsSafeEvidencePath(
        Path.GetFileName(MultiMemoryTransactionRecordCodec.GetPath(directory))) ? MultiMemoryTransactionRecordCodec.FileName : null;

    private static MultiMemoryFailure MapRecoveryFailure(RecoveryBlockReason reason) => reason switch
    {
        RecoveryBlockReason.Malformed => MultiMemoryFailure.Malformed,
        RecoveryBlockReason.UnsupportedVersion => MultiMemoryFailure.UnsupportedVersion,
        RecoveryBlockReason.Unsafe => MultiMemoryFailure.Unsafe,
        RecoveryBlockReason.Inconsistent => MultiMemoryFailure.Inconsistent,
        RecoveryBlockReason.Ambiguous => MultiMemoryFailure.Ambiguous,
        RecoveryBlockReason.ReplayIoFailed => MultiMemoryFailure.ReplayIoFailed,
        _ => MultiMemoryFailure.Admission
    };

    private static MultiMemoryAdmissionException AdmissionFailure(MultiMemoryFailure failure, string message,
        string? evidencePath = null) => new(message, failure, evidencePath);

    private static string? ToSafeEvidencePath(string scopeDirectory, string evidencePath)
    {
        string relative = Path.GetRelativePath(scopeDirectory, evidencePath).Replace('\\', '/');
        return MultiMemoryParticipantOutcome.IsSafeEvidencePath(relative) ? relative : null;
    }

    private static MultiMemoryAggregateResult Rejected(string transactionId, string scope,
        MultiMemoryFailure failure, string warning, string? evidencePath = null) =>
        new(transactionId, scope, MultiMemoryOutcome.Rejected, [], failure, warning, evidencePath);

    private static string AddCleanupWarning(string warning, bool cleanupFailed) =>
        cleanupFailed ? $"{warning} Admission cleanup was incomplete." : warning;

    private static bool DisposeAdapters(IEnumerable<IMultiMemoryParticipantAdapter> adapters)
    {
        bool cleanupFailed = false;
        var disposed = new HashSet<IMultiMemoryParticipantAdapter>(ReferenceEqualityComparer.Instance);
        foreach (IMultiMemoryParticipantAdapter adapter in adapters.Reverse())
        {
            if (!disposed.Add(adapter) || adapter is not IDisposable disposable)
                continue;
            try
            {
                disposable.Dispose();
            }
            catch
            {
                cleanupFailed = true;
            }
        }
        return cleanupFailed;
    }
}

internal sealed class FileMemoryParticipantAdapter : IMultiMemoryParticipantAdapter
{
    private readonly FileMemoryStore _store;
    private readonly RecoveryCandidate _candidate;
    private bool _verified;

    internal FileMemoryParticipantAdapter(FileMemoryStore store, MultiMemoryParticipantManifest manifest,
        RecoveryCandidate candidate)
    {
        _store = store;
        _candidate = candidate;
        Scope = candidate.Scope;
        Subject = manifest.Subject;
        OperationId = manifest.OperationId;
        Kind = manifest.Kind;
        EvidencePath = Path.GetRelativePath(store.GetStoreDirForScope(candidate.Scope), candidate.EvidencePath).Replace('\\', '/');
    }

    public string Kind { get; }
    public string Scope { get; }
    public string Subject { get; }
    public string OperationId { get; }
    public string EvidencePath { get; }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_candidate.Envelope.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending))
            return Task.FromResult(MultiMemoryCommitStatus.Prepared);

        RecoveryOutcome outcome = GetRecoveryAdapter().RecoverOrCleanup(_candidate.Envelope,
            _candidate.EvidencePath, cancellationToken);
        if (outcome.IsBlocked)
            throw new MultiMemoryParticipantMismatchException();
        _verified = outcome.IsCommitted;
        return Task.FromResult(_verified ? MultiMemoryCommitStatus.Committed : MultiMemoryCommitStatus.Unknown);
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_candidate.Envelope.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending))
            throw new MultiMemoryParticipantMismatchException();
        return Task.CompletedTask;
    }

    public Task VerifyCommittedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_verified)
            throw new MultiMemoryParticipantMismatchException();
        return Task.CompletedTask;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_candidate.Operation != RecoveryOperation.Append)
            throw new MultiMemoryParticipantMismatchException();
        await _store.RecoverAppendParticipantForCoordinatorAsync(Scope, cancellationToken).ConfigureAwait(false);
    }

    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private IRecoveryAdapter GetRecoveryAdapter() => _candidate.Operation switch
    {
        RecoveryOperation.Append => new AppendRecoveryAdapter(),
        RecoveryOperation.Restore => new RestoreRecoveryAdapter(),
        _ => throw new MultiMemoryParticipantMismatchException()
    };
}

internal sealed class CompletedProofParticipantAdapter : IMultiMemoryParticipantAdapter
{
    internal CompletedProofParticipantAdapter(string scope, MultiMemoryParticipantManifest manifest,
        MultiMemoryParticipantCompletion proof)
    {
        Scope = scope;
        Subject = manifest.Subject;
        OperationId = manifest.OperationId;
        Kind = manifest.Kind;
        EvidencePath = proof.EvidencePath;
    }

    public string Kind { get; }
    public string Scope { get; }
    public string Subject { get; }
    public string OperationId { get; }
    public string EvidencePath { get; }
    public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(MultiMemoryCommitStatus.Committed);
    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task VerifyCommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken cancellationToken) =>
        Task.FromException(new MultiMemoryParticipantMismatchException());
    public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed record MultiMemoryAdmissionResult
{
    internal MultiMemoryAdmissionResult(IReadOnlyList<IMultiMemoryParticipantAdapter> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        Participants = Array.AsReadOnly(participants.ToArray());
        IsAdmitted = true;
    }

    internal MultiMemoryAdmissionResult(MultiMemoryFailure failure, string warning, string? evidencePath = null,
        bool cleanupFailed = false)
    {
        Failure = failure;
        Warning = warning;
        EvidencePath = evidencePath;
        CleanupFailed = cleanupFailed;
        Participants = [];
    }

    internal bool IsAdmitted { get; }
    internal IReadOnlyList<IMultiMemoryParticipantAdapter> Participants { get; }
    internal MultiMemoryFailure? Failure { get; }
    internal string? Warning { get; }
    internal string? EvidencePath { get; }
    internal bool CleanupFailed { get; }
}

internal sealed class MultiMemoryAdmissionException : Exception
{
    internal MultiMemoryAdmissionException(string message, MultiMemoryFailure failure, string? evidencePath = null,
        bool cleanupFailed = false)
        : base(message)
    {
        Failure = failure;
        EvidencePath = evidencePath;
        CleanupFailed = cleanupFailed;
    }

    internal MultiMemoryFailure Failure { get; }
    internal string? EvidencePath { get; }
    internal bool CleanupFailed { get; set; }
}
