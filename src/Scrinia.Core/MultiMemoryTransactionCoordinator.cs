using Scrinia.Core.Models;
using Scrinia.Core.Bundles;

namespace Scrinia.Core;

/// <summary>Owns the single-scope admission boundary for a multi-memory transaction.</summary>
internal sealed class MultiMemoryTransactionCoordinator : IMultiMemoryTransactionCoordinator
{
    private readonly FileMemoryStore _store;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>? _recoveryAdapter;
    private readonly Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>>? _scopedRecoveryAdapter;
    private readonly IMultiMemoryParticipantAdapterFactory? _participantAdapterFactory;

    internal static AsyncLocal<Func<MultiMemoryTransactionTransition, string, Exception?>?> FailureInjector { get; } = new();

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store, TimeSpan? lockTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (_lockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "The lock timeout must be positive.");
    }

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        IMultiMemoryParticipantAdapterFactory participantAdapterFactory, TimeSpan? lockTimeout = null)
        : this(store, lockTimeout) => _participantAdapterFactory = participantAdapterFactory ?? throw new ArgumentNullException(nameof(participantAdapterFactory));

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        Func<MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> recoveryAdapter,
        TimeSpan? lockTimeout = null)
        : this(store, lockTimeout) => _recoveryAdapter = recoveryAdapter ?? throw new ArgumentNullException(nameof(recoveryAdapter));

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> recoveryAdapter,
        TimeSpan? lockTimeout = null)
        : this(store, lockTimeout) => _scopedRecoveryAdapter = recoveryAdapter ?? throw new ArgumentNullException(nameof(recoveryAdapter));

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store,
        Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> recoveryAdapter,
        IMultiMemoryParticipantAdapterFactory participantAdapterFactory, TimeSpan? lockTimeout = null)
        : this(store, recoveryAdapter, lockTimeout) =>
        _participantAdapterFactory = participantAdapterFactory ?? throw new ArgumentNullException(nameof(participantAdapterFactory));

    internal static MultiMemoryParticipantRequest[] NormalizeAndOrder(
        FileMemoryStore store, MultiMemoryTransactionRequest request)
        => NormalizeAndOrder(store, request, null);

    private static MultiMemoryParticipantRequest[] NormalizeAndOrder(
        FileMemoryStore store, MultiMemoryTransactionRequest request, string? transactionId)
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

        normalized.AddRange(request.TransactionalParticipants.Select(participant =>
            MultiMemoryParticipantRequest.ForTransactionalParticipant(transactionId ?? "pending", request.Scope, participant)));

        return normalized
            .OrderBy(participant => participant.ParticipantIdentity is null ? 0 : 1)
            .ThenBy(participant => participant.OrderingKey ?? store.ResolveAddress(participant.Address).Subject, StringComparer.Ordinal)
            .ThenBy(participant => participant.ParticipantIdentity ?? store.ResolveAddress(participant.Address).Subject, StringComparer.Ordinal)
            .ThenBy(participant => participant.OperationId, StringComparer.Ordinal)
            .ThenBy(participant => participant.Kind, StringComparer.Ordinal)
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
        MultiMemoryParticipantRequest[] ordered = NormalizeAndOrder(_store, request);

        try
        {
            using WorkspaceLease lease = _store.EnterWorkspaceMutation(_lockTimeout, cancellationToken);
            return await _store.WithExclusiveScopeAdmissionAsync(lease, request.Scope, _lockTimeout, async () =>
            {
                string storeDirectory = StoreDirectory(request.Scope);
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

                var adapters = new List<IMultiMemoryParticipantAdapter>(ordered.Length);
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
                        string expectedSubject = participant.ParticipantIdentity is null ? subject : participant.Address.ToString();
                        if (!scope.Equals(request.Scope, StringComparison.Ordinal) ||
                            !expectedSubject.Equals(adapter.Subject, StringComparison.Ordinal) ||
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
                ToSafeEvidencePath(StoreDirectory(request.Scope), block.EvidencePath));
        }
        catch (IOException)
        {
            return new MultiMemoryAdmissionResult(MultiMemoryFailure.ReplayIoFailed,
                "The transaction scope could not be inspected safely.");
        }
        catch (BundleImportException) { throw; }
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
            using WorkspaceLease lease = _store.EnterWorkspaceMutation(_lockTimeout, cancellationToken);
            return await _store.WithExclusiveScopeAdmissionAsync(lease, request.Scope, _lockTimeout, async () =>
            {
                IReadOnlyList<MultiMemoryParticipantRequest> ordered = NormalizeAndOrder(_store, request, transactionId);
                MultiMemoryAdmissionResult admission = await AdmitUnderLockAsync(request, ordered, admitParticipant, cancellationToken).ConfigureAwait(false);
                if (!admission.IsAdmitted)
                    return Rejected(transactionId, request.Scope, admission.Failure!.Value,
                        AddCleanupWarning(admission.Warning!, admission.CleanupFailed), admission.EvidencePath);

                return await ExecuteUnderLockAsync(transactionId, request, admission.Participants,
                    admission.RejectedTransactionalParticipants, cancellationToken).ConfigureAwait(false);
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
                ToSafeEvidencePath(StoreDirectory(request.Scope), block.EvidencePath));
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
            using WorkspaceLease lease = _store.EnterWorkspaceMutation(_lockTimeout, cancellationToken);
            return await _store.WithExclusiveScopeAdmissionAsync(lease, scope, _lockTimeout,
                () => RecoverUnderLockAsync(lease, scope, cancellationToken), cancellationToken, recoverCoordinator: false).ConfigureAwait(false);
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
                ToSafeEvidencePath(StoreDirectory(scope), block.EvidencePath));
        }
    }

    private string StoreDirectory(string scope) => PathRouter.ToStoreDir(scope, _store.WorkspaceRoot);

    internal async Task<MultiMemoryAggregateResult> RecoverUnderLockAsync(WorkspaceLease lease, string scope, CancellationToken cancellationToken)
    {
        lease.AssertValid(_store.WorkspaceRoot);
        string directory = StoreDirectory(scope);
        if (!File.Exists(MultiMemoryTransactionRecordCodec.GetPath(directory)))
            return Rejected("recovery", scope, MultiMemoryFailure.ReplayIoFailed, "No coordinator decision record exists.");

        MultiMemoryTransactionRecord record;
        try { record = MultiMemoryTransactionRecordCodec.Read(directory, scope, cancellationToken); }
        catch (MultiMemoryTransactionRecordException exception)
        {
            return new MultiMemoryAggregateResult(exception.TransactionId ?? "unknown", scope,
                MultiMemoryOutcome.RecoveryRequired, [], exception.Failure, FailureDetail(exception.Failure),
                SafeRecordPath(directory));
        }
        var b3Adapters = new List<IMultiMemoryParticipantAdapter>();
        try
        {
            foreach (MultiMemoryParticipantManifest manifest in record.Participants)
            {
                IMultiMemoryParticipantAdapter adapter = await (_scopedRecoveryAdapter is not null
                    ? _scopedRecoveryAdapter(record.Scope, manifest, cancellationToken)
                    : _recoveryAdapter is not null
                        ? _recoveryAdapter(manifest, cancellationToken)
                        : Task.FromException<IMultiMemoryParticipantAdapter>(new MultiMemoryParticipantMismatchException()))
                    .ConfigureAwait(false) ?? throw new MultiMemoryParticipantMismatchException();
                ValidateRecoveryAdapter(record.Scope, manifest, adapter);
                b3Adapters.Add(adapter);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            DisposeAdapters(b3Adapters);
            return new MultiMemoryAggregateResult(record.TransactionId, scope, MultiMemoryOutcome.RecoveryRequired, [],
                Classify(exception), "B3 participant recovery could not be proven safe; evidence is retained.", SafeRecordPath(directory));
        }
        bool hasTransactionalRecovery = record.TransactionalParticipants is { Count: > 0 } ||
            record.RejectedTransactionalParticipants is { Count: > 0 };
        if (hasTransactionalRecovery)
            return await RecoverTransactionalParticipantsAsync(record, b3Adapters, directory, _participantAdapterFactory, cancellationToken).ConfigureAwait(false);
        if (_recoveryAdapter is null && _scopedRecoveryAdapter is null)
            return new MultiMemoryAggregateResult(record.TransactionId, scope, MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Ambiguous, "Participant adapter resolution is unavailable; evidence is retained.", SafeRecordPath(directory));

        var adapters = new List<IMultiMemoryParticipantAdapter>();
        try
        {
            adapters.AddRange(b3Adapters);
            if (record.State == MultiMemoryTransactionState.Prepared)
                return await AbortPreparedAsync(record, adapters, directory, cancellationToken).ConfigureAwait(false);
            if (record.State == MultiMemoryTransactionState.RecoveryRequired)
                return RecoveryRequired(record, adapters, MultiMemoryFailure.Ambiguous,
                    "The coordinator record requires explicit recovery before participant replay.", directory);
            MultiMemoryAggregateResult finished = await FinishDecisionAsync(record, adapters, cancellationToken).ConfigureAwait(false);
            return ProjectAggregate(finished, CreateRecoveryRequest(record), record, adapters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return RecoveryRequired(record, adapters, Classify(exception),
                "Participant recovery could not be proven safe.", directory);
        }
        finally { DisposeAdapters(adapters); }
    }

    private static MultiMemoryAggregateResult ProjectAggregate(MultiMemoryAggregateResult result,
        MultiMemoryTransactionRequest request, MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters)
    {
        var b4 = new List<MultiMemoryTransactionalParticipantOutcome>();
        foreach (IMultiMemoryParticipantAdapter adapter in adapters)
        {
            B4ParticipantRecoveryState? state = adapter.RecoveryState;
            MultiMemoryTransactionalParticipantRequest? requested = request.TransactionalParticipants
                .FirstOrDefault(item => item.Address.ToString() == adapter.Subject && item.OperationId == adapter.OperationId);
            if (state is null && requested is null) continue;
            b4.Add(new MultiMemoryTransactionalParticipantOutcome(
                state?.ParticipantIdentity ?? requested!.ParticipantIdentity,
                state?.OrderingKey ?? requested!.OrderingKey,
                adapter.OperationId, adapter.Kind, true,
                result.Outcome == MultiMemoryOutcome.RecoveryRequired
                    ? state?.LifecycleState ?? TransactionalParticipantLifecycleState.Ambiguous
                    : result.Outcome == MultiMemoryOutcome.RolledBack
                        ? TransactionalParticipantLifecycleState.RolledBack
                        : TransactionalParticipantLifecycleState.Committed,
                result.Outcome is MultiMemoryOutcome.Committed or MultiMemoryOutcome.CommittedCleanupPending,
                result.Outcome == MultiMemoryOutcome.RolledBack, adapter.EvidencePath, null));
        }
        var rejected = (record.RejectedTransactionalParticipants ?? [])
            .Select(item => new MultiMemoryTransactionalParticipantOutcome(item.ParticipantIdentity, item.OrderingKey,
                item.OperationId, item.Kind, false, TransactionalParticipantLifecycleState.Changed,
                false, false, null, item.RejectionDetail)).ToArray();
        IReadOnlyList<MultiMemoryParticipantOutcome> b3 = result.Participants
            .Where(outcome => request.Participants.Any(item => item.OperationId == outcome.OperationId &&
                item.Kind == outcome.Kind && item.Address.Value.EndsWith('/' + outcome.Subject, StringComparison.Ordinal)))
            .Select(outcome => new MultiMemoryParticipantOutcome(outcome.Subject, outcome.OperationId, outcome.Kind,
                outcome.EvidencePath, outcome.Committed, outcome.RolledBack, outcome.Reason))
            .ToArray();
        var ordered = request.RequestedParticipants(record.TransactionId)
            .OrderBy(participant => participant.ParticipantIdentity is null ? 0 : 1)
            .ThenBy(participant => participant.OrderingKey ?? participant.Address.Value, StringComparer.Ordinal)
            .ThenBy(participant => participant.ParticipantIdentity ?? participant.Address.Value, StringComparer.Ordinal)
            .ThenBy(participant => participant.OperationId, StringComparer.Ordinal)
            .ThenBy(participant => participant.Kind, StringComparer.Ordinal).ToArray();
        int OrdinalFor(string operationId, string kind, string? identity, string subject) => ordered
            .Select((participant, ordinal) => (participant, ordinal))
            .Where(item => item.participant.OperationId == operationId && item.participant.Kind == kind &&
                (identity is null ? item.participant.ParticipantIdentity is null &&
                    item.participant.Address.Value.EndsWith('/' + subject, StringComparison.Ordinal) :
                    item.participant.ParticipantIdentity == identity))
            .Select(item => item.ordinal).Single();
        b3 = b3.Select(item => new MultiMemoryParticipantOutcome(item.Subject, item.OperationId, item.Kind,
            item.EvidencePath, item.Committed, item.RolledBack, item.Reason,
            OrdinalFor(item.OperationId, item.Kind, null, item.Subject))).ToArray();
        b4 = b4.Select(item => new MultiMemoryTransactionalParticipantOutcome(item.ParticipantIdentity,
            item.OrderingKey, item.OperationId, item.Kind, true, item.LifecycleState, item.Committed,
            item.RolledBack, item.EvidencePath, item.Reason,
            OrdinalFor(item.OperationId, item.Kind, item.ParticipantIdentity, item.OperationId))).ToList();
        rejected = rejected.Select(item => new MultiMemoryTransactionalParticipantOutcome(item.ParticipantIdentity,
            item.OrderingKey, item.OperationId, item.Kind, false, item.LifecycleState, false, false,
            item.EvidencePath, item.Reason,
            OrdinalFor(item.OperationId, item.Kind, item.ParticipantIdentity, item.OperationId))).ToArray();
        MultiMemoryAggregateProjectionInput input = new(b3, b4, rejected);
        return MultiMemoryAggregateResult.Create(result.TransactionId, result.Scope, result.Outcome,
            request.RequestedParticipants(record.TransactionId), input, result.Failure, result.Warning, result.EvidencePath);
    }

    private async Task<MultiMemoryTransactionRecord> UpdateParticipantLifecycleAsync(
        string scopeDirectory,
        MultiMemoryTransactionRecord expected,
        B4ParticipantRecoveryState participant,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(participant);
        if (!string.Equals(expected.TransactionId, participant.TransactionId, StringComparison.Ordinal) ||
            !string.Equals(expected.Scope, participant.Scope, StringComparison.Ordinal))
            throw new MultiMemoryTransactionRecordException("Transactional lifecycle identity does not match the coordinator record.",
                MultiMemoryFailure.Inconsistent, transactionId: expected.TransactionId, expectedScope: expected.Scope);

        MultiMemoryTransactionRecord replacement = MultiMemoryTransactionRecordCodec.WithTransactionalParticipant(
            expected, participant, DateTimeOffset.UtcNow);
        MultiMemoryTransactionRecordCodec.WriteExpected(scopeDirectory, expected, replacement);
        await Task.CompletedTask.ConfigureAwait(false);
        return replacement;
    }

    private async Task<MultiMemoryAggregateResult> RecoverTransactionalParticipantsAsync(
        MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> b3Adapters,
        string directory,
        IMultiMemoryParticipantAdapterFactory? participantFactory,
        CancellationToken cancellationToken)
    {
        var adapters = new List<IMultiMemoryParticipantAdapter>(b3Adapters);
        try
        {
            foreach (B4ParticipantRecoveryState state in record.TransactionalParticipants ?? [])
            {
                if (participantFactory is null)
                    throw new MultiMemoryParticipantMismatchException();
                var manifest = new MultiMemoryParticipantManifest(state.Address, state.OperationId, state.Kind);
                IMultiMemoryParticipantAdapter adapter = await participantFactory.RehydrateAsync(
                    new B4ParticipantRehydrationRequest(state, manifest, state.Capabilities), cancellationToken)
                    .ConfigureAwait(false) ?? throw new MultiMemoryParticipantMismatchException();
                ValidateRehydratedAdapter(state, adapter);
                adapters.Add(adapter);
                if (state.PreparedHandleReference is null)
                {
                    await PersistRecoveryFailureAsync(record, directory, adapters,
                        new MultiMemoryParticipantMismatchException(), cancellationToken).ConfigureAwait(false);
                    MultiMemoryAggregateResult blocked = RecoveryRequired(record, b3Adapters, MultiMemoryFailure.Ambiguous,
                        "A handle-less participant state requires explicit recovery; evidence is retained.", directory);
                    return ProjectAggregate(blocked, CreateRecoveryRequest(record), record, adapters);
                }
            }
            adapters = OrderRecoveryAdapters(record, adapters);
            if (record.TransactionalParticipants is null or { Count: 0 })
                return await ProjectRejectedOnlyRecoveryAsync(record, b3Adapters, directory, cancellationToken).ConfigureAwait(false);
            if (record.State == MultiMemoryTransactionState.Prepared)
            {
                MultiMemoryAggregateResult aborted = await AbortPreparedAsync(record, adapters, directory, cancellationToken)
                    .ConfigureAwait(false);
                return ProjectAggregate(aborted, CreateRecoveryRequest(record), record, adapters);
            }
            if (record.State == MultiMemoryTransactionState.RecoveryRequired)
                return RecoveryRequired(record, adapters, MultiMemoryFailure.Ambiguous,
                    "The coordinator record requires explicit recovery before participant replay.", directory);
            MultiMemoryAggregateResult finished = await FinishDecisionAsync(record, adapters, cancellationToken).ConfigureAwait(false);
            return ProjectAggregate(finished, CreateRecoveryRequest(record), record, adapters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            await PersistRecoveryFailureAsync(record, directory, adapters, exception, cancellationToken).ConfigureAwait(false);
            return RecoveryRequired(record, adapters, Classify(exception),
                "Transactional participant recovery could not be proven safe; evidence is retained.", directory);
        }
        finally { DisposeAdapters(adapters); }
    }

    private async Task<MultiMemoryAggregateResult> ProjectRejectedOnlyRecoveryAsync(
        MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> b3Adapters,
        string directory,
        CancellationToken cancellationToken)
    {
        var b3Outcomes = b3Adapters.Select(adapter => new MultiMemoryParticipantOutcome(
            adapter.Subject, adapter.OperationId, adapter.Kind, adapter.EvidencePath,
            record.State == MultiMemoryTransactionState.Committed, false, null)).ToArray();
        var rejectedMetadata = record.RejectedTransactionalParticipants ?? [];
        var rejected = rejectedMetadata.Select(item =>
            new MultiMemoryTransactionalParticipantOutcome(item.ParticipantIdentity, item.OrderingKey,
                item.OperationId, item.Kind, false, TransactionalParticipantLifecycleState.Changed,
                false, false, null, item.RejectionDetail)).ToArray();
        var requested = record.Participants.Select(manifest => new MultiMemoryParticipantRequest(
            MultiMemoryTransactionRequest.AddressForScope(record.Scope, manifest.Subject),
            manifest.OperationId, manifest.Kind)).Concat(rejectedMetadata.Select(item =>
                MultiMemoryParticipantRequest.ForTransactionalParticipant(record.TransactionId, record.Scope,
                    new MultiMemoryTransactionalParticipantRequest(MemoryAddress.Parse(item.Address),
                        item.ParticipantIdentity, item.ContractVersion, item.OrderingKey, item.OperationId, item.Kind,
                        new Dictionary<string, string>(), new Dictionary<string, string>())))).ToArray();
        (IReadOnlyList<MultiMemoryParticipantOutcome> numberedB3,
            IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> numberedRejected) =
            AddCanonicalOrdinals(requested, b3Outcomes, rejected);
        MultiMemoryAggregateResult result = MultiMemoryAggregateResult.Create(record.TransactionId, record.Scope,
            record.State == MultiMemoryTransactionState.Committed ? MultiMemoryOutcome.Committed : MultiMemoryOutcome.RolledBack,
            requested, new MultiMemoryAggregateProjectionInput(numberedB3, [], numberedRejected), null, null, null);
        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    private static (IReadOnlyList<MultiMemoryParticipantOutcome> B3,
        IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> Rejected) AddCanonicalOrdinals(
        IReadOnlyList<MultiMemoryParticipantRequest> requested,
        IReadOnlyList<MultiMemoryParticipantOutcome> b3,
        IReadOnlyList<MultiMemoryTransactionalParticipantOutcome> rejected)
    {
        var ordered = requested.OrderBy(participant => participant.ParticipantIdentity is null ? 0 : 1)
            .ThenBy(participant => participant.OrderingKey ?? participant.Address.Value, StringComparer.Ordinal)
            .ThenBy(participant => participant.ParticipantIdentity ?? participant.Address.Value, StringComparer.Ordinal)
            .ThenBy(participant => participant.OperationId, StringComparer.Ordinal)
            .ThenBy(participant => participant.Kind, StringComparer.Ordinal)
            .Select((participant, ordinal) => (participant, ordinal)).ToArray();
        int OrdinalFor(string operationId, string kind, string? identity, string subject) => ordered
            .Where(item => item.participant.OperationId == operationId && item.participant.Kind == kind &&
                (identity is null ? item.participant.ParticipantIdentity is null &&
                    item.participant.Address.Value.EndsWith('/' + subject, StringComparison.Ordinal) :
                    item.participant.ParticipantIdentity == identity))
            .Select(item => item.ordinal).Single();
        return (
            b3.Select(item => new MultiMemoryParticipantOutcome(item.Subject, item.OperationId, item.Kind,
                item.EvidencePath, item.Committed, item.RolledBack, item.Reason,
                OrdinalFor(item.OperationId, item.Kind, null, item.Subject))).ToArray(),
            rejected.Select(item => new MultiMemoryTransactionalParticipantOutcome(item.ParticipantIdentity,
                item.OrderingKey, item.OperationId, item.Kind, false, item.LifecycleState, false, false,
                item.EvidencePath, item.Reason, OrdinalFor(item.OperationId, item.Kind,
                    item.ParticipantIdentity, item.OperationId))).ToArray());
    }

    private static List<IMultiMemoryParticipantAdapter> OrderRecoveryAdapters(
        MultiMemoryTransactionRecord record, IEnumerable<IMultiMemoryParticipantAdapter> adapters)
    {
        var b4Keys = (record.TransactionalParticipants ?? []).ToDictionary(
            state => (state.ParticipantIdentity, state.OperationId, state.Kind),
            state => state.OrderingKey);
        return adapters.OrderBy(adapter => b4Keys.TryGetValue(
                (adapter.RecoveryState?.ParticipantIdentity ?? string.Empty, adapter.OperationId, adapter.Kind),
                out string? orderingKey) ? 1 : 0)
            .ThenBy(adapter => b4Keys.TryGetValue(
                (adapter.RecoveryState?.ParticipantIdentity ?? string.Empty, adapter.OperationId, adapter.Kind),
                out string? orderingKey) ? orderingKey : adapter.Subject, StringComparer.Ordinal)
            .ThenBy(adapter => adapter.Subject, StringComparer.Ordinal).ToList();
    }

    private static MultiMemoryTransactionRequest CreateRecoveryRequest(MultiMemoryTransactionRecord record)
    {
        string TopicAddress(string address) => address;
        var b3 = record.Participants.Select(manifest => new MultiMemoryParticipantRequest(
            MultiMemoryTransactionRequest.AddressForScope(record.Scope, manifest.Subject),
            manifest.OperationId, manifest.Kind)).ToArray();
        var b4 = (record.TransactionalParticipants ?? []).Select(state =>
            new MultiMemoryTransactionalParticipantRequest(MemoryAddress.Parse(TopicAddress(state.Address)),
                state.ParticipantIdentity, state.ContractVersion, state.OrderingKey, state.OperationId, state.Kind,
                state.Payload, state.Metadata)).Concat((record.RejectedTransactionalParticipants ?? []).Select(item =>
            new MultiMemoryTransactionalParticipantRequest(MemoryAddress.Parse(TopicAddress(item.Address)),
                item.ParticipantIdentity, item.ContractVersion, item.OrderingKey, item.OperationId, item.Kind,
                new Dictionary<string, string>(), new Dictionary<string, string>()))).ToArray();
        return new MultiMemoryTransactionRequest(record.Scope, b3, b4);
    }

    private async Task PersistRecoveryFailureAsync(MultiMemoryTransactionRecord record, string directory,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, Exception exception, CancellationToken cancellationToken)
    {
        MultiMemoryTransactionRecord expected = record;
        foreach (B4ParticipantRecoveryState original in record.TransactionalParticipants ?? [])
        {
            B4ParticipantRecoveryState state = adapters.Select(adapter => adapter.RecoveryState)
                .FirstOrDefault(candidate => candidate?.ParticipantIdentity == original.ParticipantIdentity) ?? original;
            B4ParticipantRecoveryState quarantined = new(state.TransactionId, state.OperationId, state.Scope, state.Address,
                state.ParticipantIdentity, state.ContractVersion, state.OrderingKey, state.Kind, state.Capabilities,
                state.Payload, state.Metadata, TransactionalParticipantLifecycleState.Ambiguous,
                state.PreparedHandleReference, state.PreparedHandleVersion, state.RetryAttempt, state.RetryElapsedMilliseconds,
                true, true, TransactionalFailureCode.UnknownTransient, state.EvidenceReference, state.EvidenceKind,
                state.EvidenceVersion, TransactionalQuarantineState.Quarantined, "recovery-mismatch");
            try { expected = await UpdateParticipantLifecycleAsync(directory, expected, quarantined, cancellationToken).ConfigureAwait(false); }
            catch (Exception) { return; }
        }
    }

    private static void PersistTransactionalEvidence(MultiMemoryTransactionRecord record, string directory,
        List<IMultiMemoryParticipantAdapter> adapters)
    {
        if (record.TransactionalParticipants is null || adapters.Count == 0) return;
        B4ParticipantRecoveryState[] states = adapters.Select(adapter => adapter.RecoveryState).Where(state => state is not null)
            .Select(state => state!).OrderBy(state => state.OrderingKey, StringComparer.Ordinal).ToArray();
        if (states.Length == 0) return;
        try
        {
            MultiMemoryTransactionRecord replacement = MultiMemoryTransactionRecordCodec.WithTransactionalParticipants(
                record, states, DateTimeOffset.UtcNow);
            MultiMemoryTransactionRecordCodec.WriteExpected(directory, record, replacement);
        }
        catch (Exception)
        {
            // The original coordinator evidence remains authoritative when a replacement cannot be proven.
        }
    }

    private async Task<MultiMemoryAggregateResult> AbortPreparedAsync(MultiMemoryTransactionRecord record,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, string directory, CancellationToken cancellationToken)
    {
        var (outcomes, stateChanged, stateChangeUnknown, failure, latestRecord) = await RollBackParticipantsAsync(
            adapters,
            async (participantAdapter, rollbackCancellationToken) =>
            {
                record = await UpdateParticipantStateIfPresentAsync(record, participantAdapter,
                    rollbackCancellationToken).ConfigureAwait(false);
                return record;
            },
            detail => FailureDetail(detail!.Value),
            cancellationToken).ConfigureAwait(false);
        record = latestRecord ?? record;

        IReadOnlyList<MultiMemoryParticipantOutcome> orderedOutcomes = outcomes
            .OrderBy(outcome => outcome.Subject, StringComparer.Ordinal)
            .ThenBy(outcome => outcome.OperationId, StringComparer.Ordinal)
            .ToArray();
        if (stateChanged)
            _store.InvalidateScopeCacheAfterRollback(record.Scope);
        if (failure is not null)
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope,
                MultiMemoryOutcome.RecoveryRequired, orderedOutcomes, failure.Value,
                "Prepared decision could not be safely aborted; evidence is retained.", SafeRecordPath(directory));
        if (stateChangeUnknown)
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope,
                MultiMemoryOutcome.RecoveryRequired, orderedOutcomes, MultiMemoryFailure.Ambiguous,
                "Rollback completed but its observable state change could not be proven.", SafeRecordPath(directory));
        try { DeleteIfPresent(directory, MultiMemoryTransactionTransition.RecordDeleted); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope,
                MultiMemoryOutcome.RecoveryRequired, orderedOutcomes, MultiMemoryFailure.Cleanup,
                "Prepared decision was aborted but its evidence could not be removed.", SafeRecordPath(directory));
        }
        return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, MultiMemoryOutcome.RolledBack,
            orderedOutcomes, null, null, null);
    }

    private async Task<(List<MultiMemoryParticipantOutcome> Outcomes, bool StateChanged, bool StateChangeUnknown,
        MultiMemoryFailure? Failure, MultiMemoryTransactionRecord? LatestRecord)>
        RollBackParticipantsAsync(IReadOnlyList<IMultiMemoryParticipantAdapter> adapters,
                                  Func<IMultiMemoryParticipantAdapter, CancellationToken, Task<MultiMemoryTransactionRecord>>? afterRollback,
                                  Func<MultiMemoryFailure?, string?>? detailFactory,
                                  CancellationToken cancellationToken)
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>();
        MultiMemoryFailure? failure = null;
        bool stateChanged = false;
        bool stateChangeUnknown = false;
        MultiMemoryTransactionRecord? latestRecord = null;
        foreach (IMultiMemoryParticipantAdapter adapter in adapters.Reverse())
        {
            bool rolledBack = false;
            string? detail = null;
            try
            {
                await adapter.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (afterRollback is not null)
                    latestRecord = await afterRollback(adapter, cancellationToken).ConfigureAwait(false);
                rolledBack = true;
                stateChanged |= adapter.ObservableStateChanged is true;
                stateChangeUnknown |= adapter.ObservableStateChanged is null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                try
                {
                    if (afterRollback is not null)
                        latestRecord = await afterRollback(adapter, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception updateException) { failure ??= Classify(updateException); }
                failure ??= Classify(exception);
                rolledBack = false;
                detail = detailFactory?.Invoke(failure);
                stateChanged |= adapter.ObservableStateChanged is true;
                stateChangeUnknown |= adapter.ObservableStateChanged is null;
            }
            outcomes.Add(new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId, adapter.Kind,
                adapter.EvidencePath, false, rolledBack, detail));
        }
        return (outcomes, stateChanged, stateChangeUnknown, failure, latestRecord);
    }

    private async Task<MultiMemoryAdmissionResult> AdmitUnderLockAsync(
        MultiMemoryTransactionRequest request, IReadOnlyList<MultiMemoryParticipantRequest> ordered,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken)
    {
        var adapters = new List<IMultiMemoryParticipantAdapter>(ordered.Count);
        var rejected = new List<B4ParticipantRequestMetadata>();
        try
        {
            foreach (MultiMemoryParticipantRequest participant in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMultiMemoryParticipantAdapter? adapter;
                try
                {
                    adapter = await admitParticipant(participant, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (participant.ParticipantIdentity is not null && exception is not OperationCanceledException)
                {
                    rejected.Add(CreateRejectedMetadata(participant, MultiMemoryFailure.Admission,
                        "Transactional participant was unavailable."));
                    continue;
                }
                if (adapter is null)
                {
                    if (participant.ParticipantIdentity is not null)
                    {
                        rejected.Add(CreateRejectedMetadata(participant, MultiMemoryFailure.Admission,
                            "Transactional participant was unavailable."));
                        continue;
                    }
                    throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "The participant adapter was not supplied.");
                }
                (string scope, string subject) = _store.ResolveAddress(participant.Address);
                string expectedSubject = participant.ParticipantIdentity is null ? subject : participant.Address.ToString();
                if (scope != request.Scope || adapter.Scope != request.Scope || expectedSubject != adapter.Subject ||
                    participant.OperationId != adapter.OperationId || participant.Kind != adapter.Kind ||
                    !MultiMemoryParticipantOutcome.IsSafeEvidencePath(adapter.EvidencePath))
                {
                    if (participant.ParticipantIdentity is not null)
                    {
                        DisposeAdapters([adapter]);
                        rejected.Add(CreateRejectedMetadata(participant, MultiMemoryFailure.Inconsistent,
                            "Transactional participant identity did not match the request."));
                        continue;
                    }
                    throw AdmissionFailure(MultiMemoryFailure.Inconsistent, "The participant adapter identity does not match the request.");
                }
                adapters.Add(adapter);
            }
            return new MultiMemoryAdmissionResult(adapters.AsReadOnly(), rejected);
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

    private static B4ParticipantRequestMetadata CreateRejectedMetadata(
        MultiMemoryParticipantRequest participant, MultiMemoryFailure failure, string detail) =>
        new(participant.TransactionId ?? "unknown", participant.Scope ?? "unknown", participant.Address.ToString(),
            participant.ParticipantIdentity ?? throw new ArgumentException("B4 identity is required.", nameof(participant)),
            participant.ContractVersion ?? throw new ArgumentException("B4 contract version is required.", nameof(participant)),
            participant.OrderingKey ?? throw new ArgumentException("B4 ordering key is required.", nameof(participant)),
            participant.OperationId, participant.Kind, ToTransactionalFailureCode(failure), detail);

    private static TransactionalFailureCode ToTransactionalFailureCode(MultiMemoryFailure failure) => failure switch
    {
        MultiMemoryFailure.Inconsistent => TransactionalFailureCode.InvalidResponse,
        MultiMemoryFailure.Ambiguous => TransactionalFailureCode.UnknownTransient,
        _ => TransactionalFailureCode.Permanent
    };

    private async Task<MultiMemoryAggregateResult> ExecuteUnderLockAsync(string transactionId,
        MultiMemoryTransactionRequest request, IReadOnlyList<IMultiMemoryParticipantAdapter> adapters,
        IReadOnlyList<B4ParticipantRequestMetadata> rejectedTransactionalParticipants,
        CancellationToken cancellationToken)
    {
        string directory = StoreDirectory(request.Scope);
        var manifests = adapters
            .Where(adapter => request.Participants.Any(participant => participant.OperationId == adapter.OperationId &&
                participant.Kind == adapter.Kind && _store.ResolveAddress(participant.Address).Subject == adapter.Subject))
            .Select(a => new MultiMemoryParticipantManifest(a.Subject, a.OperationId, a.Kind)).ToArray();
        if (manifests.Length == 0)
            manifests = [new MultiMemoryParticipantManifest("coordinator", transactionId, "memory")];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
            transactionId, request.Scope, MultiMemoryTransactionState.Prepared, manifests, now, now,
            rejectedTransactionalParticipants: rejectedTransactionalParticipants);
        bool decisionPublished = false;
        try
        {
            foreach (IMultiMemoryParticipantAdapter adapter in adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await adapter.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }
            B4ParticipantRecoveryState[] states = adapters
                .Select(adapter => adapter.RecoveryState)
                .Where(state => state is not null)
                .Select(state => state!)
                .OrderBy(state => state.OrderingKey, StringComparer.Ordinal)
                .ToArray();
            if (states.Length > 0)
                record = MultiMemoryTransactionRecordCodec.WithTransactionalParticipants(record, states, DateTimeOffset.UtcNow);
            Publish(directory, record, MultiMemoryTransactionTransition.PreparedPublished);
            foreach (B4ParticipantRecoveryState state in states)
                record = await UpdateParticipantLifecycleAsync(directory, record, state, cancellationToken).ConfigureAwait(false);
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
            MultiMemoryAggregateResult finished = await FinishDecisionAsync(record, adapters, cancellationToken).ConfigureAwait(false);
            return ProjectAggregate(finished, request, record, adapters);
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
                record = await UpdateParticipantStateIfPresentAsync(record, adapter, cancellationToken).ConfigureAwait(false);
                if (status != MultiMemoryCommitStatus.Committed)
                {
                    try { await adapter.CommitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) { throw new MultiMemoryCommitException(exception); }
                    record = await UpdateParticipantStateIfPresentAsync(record, adapter, cancellationToken).ConfigureAwait(false);
                }
                try
                {
                    await adapter.VerifyCommittedAsync(cancellationToken).ConfigureAwait(false);
                    record = await UpdateParticipantStateIfPresentAsync(record, adapter, cancellationToken).ConfigureAwait(false);
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
            _store.InvalidateCommittedScope(record.Scope);
            MultiMemoryTransactionRecord proofRecord = MultiMemoryTransactionRecordCodec.WithCompletionProof(
                record, adapters.Select(adapter => new MultiMemoryParticipantCompletion(
                    adapter.Subject, adapter.OperationId, adapter.Kind, adapter.EvidencePath)).ToArray(),
                DateTimeOffset.UtcNow);
            try
            {
                Publish(StoreDirectory(record.Scope), proofRecord,
                    MultiMemoryTransactionTransition.CompletionProofPublished);
                record = proofRecord;
            }
            catch (Exception)
            {
                return RecoveryRequired(record, adapters, MultiMemoryFailure.DecisionPublication,
                    "Participant completion proof could not be durably recorded.",
                    StoreDirectory(record.Scope));
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
                outcomes, MultiMemoryFailure.Ambiguous, "Decision completion was cancelled; evidence is retained.", SafeRecordPath(StoreDirectory(record.Scope)));
        }
        catch (Exception exception)
        {
            MultiMemoryFailure failure = allParticipantsVerified ? MultiMemoryFailure.Cleanup : Classify(exception);
            MultiMemoryOutcome outcome = allParticipantsVerified
                ? MultiMemoryOutcome.CommittedCleanupPending : MultiMemoryOutcome.RecoveryRequired;
            return new MultiMemoryAggregateResult(record.TransactionId, record.Scope, outcome, outcomes, failure,
                FailureDetail(failure), SafeRecordPath(StoreDirectory(record.Scope)));
        }
    }

    private async Task<MultiMemoryTransactionRecord> UpdateParticipantStateIfPresentAsync(
        MultiMemoryTransactionRecord record,
        IMultiMemoryParticipantAdapter adapter,
        CancellationToken cancellationToken)
    {
        B4ParticipantRecoveryState? state = adapter.RecoveryState;
        return state is null
            ? record
            : await UpdateParticipantLifecycleAsync(StoreDirectory(record.Scope), record, state, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<MultiMemoryAggregateResult> RollbackBeforeDecisionAsync(string id, string scope,
        IReadOnlyList<IMultiMemoryParticipantAdapter> adapters, string directory, CancellationToken cancellationToken)
    {
        var (outcomes, stateChanged, stateChangeUnknown, failure, _) = await RollBackParticipantsAsync(
            adapters,
            afterRollback: null,
            detailFactory: d => d?.ToString(),
            cancellationToken).ConfigureAwait(false);

        if (stateChanged)
            _store.InvalidateScopeCacheAfterRollback(scope);
        if (failure is null)
        {
            if (stateChangeUnknown)
                return new MultiMemoryAggregateResult(id, scope, MultiMemoryOutcome.RecoveryRequired,
                    outcomes.OrderBy(o => o.Subject, StringComparer.Ordinal).ToArray(), MultiMemoryFailure.Ambiguous,
                    "Pre-decision rollback completed but its observable state change could not be proven.", SafeRecordPath(directory));
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
        DeleteIfPresent(StoreDirectory(record.Scope), transition);

    private static void DeleteIfPresent(string directory, MultiMemoryTransactionTransition transition)
    {
        string path = MultiMemoryTransactionRecordCodec.GetPath(directory);
        ThrowIfInjected(transition, path);
        File.Delete(path);
    }

    private static void ThrowIfInjected(MultiMemoryTransactionTransition transition, string path)
    {
        Exception? failure = B0FaultSeam.ThrowIfInjectedOrNull(
            B0FaultSeam.MapMultiMemoryTransition(transition), transition.ToString());
        if (failure is not null)
            throw failure;
        failure = FailureInjector.Value?.Invoke(transition, path);
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

    private static void ValidateRehydratedAdapter(B4ParticipantRecoveryState state,
        IMultiMemoryParticipantAdapter adapter)
    {
        B4ParticipantRecoveryState? snapshot = adapter.RecoveryState;
        if (adapter.Scope != state.Scope || adapter.Subject != state.Address ||
            adapter.OperationId != state.OperationId || adapter.Kind != state.Kind ||
            !MultiMemoryParticipantOutcome.IsSafeEvidencePath(adapter.EvidencePath) || snapshot is null ||
            snapshot.TransactionId != state.TransactionId || snapshot.Scope != state.Scope ||
            snapshot.Address != state.Address || snapshot.ParticipantIdentity != state.ParticipantIdentity ||
            snapshot.ContractVersion != state.ContractVersion || snapshot.OrderingKey != state.OrderingKey ||
            snapshot.OperationId != state.OperationId || snapshot.Kind != state.Kind ||
            !snapshot.Capabilities.SequenceEqual(state.Capabilities, StringComparer.Ordinal) ||
            !DictionariesMatch(snapshot.Payload, state.Payload) || !DictionariesMatch(snapshot.Metadata, state.Metadata))
            throw new MultiMemoryParticipantMismatchException();
    }

    internal static bool DictionariesMatch(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in left)
        {
            if (pair.Key is null || pair.Value is null || !expected.TryAdd(pair.Key, pair.Value))
                return false;
        }

        foreach (KeyValuePair<string, string> pair in right)
        {
            if (pair.Key is null || pair.Value is null ||
                !expected.TryGetValue(pair.Key, out string? value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                return false;
        }

        return true;
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
        Subject = ResolveCandidateSubject(store, candidate);
        OperationId = candidate.OperationId;
        Kind = manifest.Kind == "memory" ? "memory" : candidate.Operation.ToString();
        EvidencePath = Path.GetRelativePath(PathRouter.ToStoreDir(candidate.Scope, store.WorkspaceRoot), candidate.EvidencePath).Replace('\\', '/');
    }

    public string Kind { get; }
    public string Scope { get; }
    public string Subject { get; }
    public string OperationId { get; }
    public string EvidencePath { get; }
    public bool? ObservableStateChanged { get; private set; }

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
        ObservableStateChanged = await _store.RecoverAppendParticipantForCoordinatorAsync(Scope, cancellationToken).ConfigureAwait(false);
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

    // Route the recovery candidate's subject through the narrow IAddressResolver port to keep the
    // FileMemoryStore <-> coordinator coupling acyclic. CA1859 prefers the concrete type for
    // performance; the decoupling at this production transaction boundary is intentional.
#pragma warning disable CA1859 // Keep the transaction boundary interface-typed.
    private static string ResolveCandidateSubject(IAddressResolver store, RecoveryCandidate candidate)
    {
        if (candidate.Operation == RecoveryOperation.Append && candidate.Envelope.Address is not null)
            return store.ResolveAddress(MemoryAddress.Parse(candidate.Envelope.Address)).Subject;
        int slash = candidate.CanonicalSubject.LastIndexOf('/');
        return slash < 0 ? candidate.CanonicalSubject : candidate.CanonicalSubject[(slash + 1)..];
    }
#pragma warning restore CA1859
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
    public bool? ObservableStateChanged => false;
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
    internal MultiMemoryAdmissionResult(IReadOnlyList<IMultiMemoryParticipantAdapter> participants,
        IReadOnlyList<B4ParticipantRequestMetadata>? rejectedTransactionalParticipants = null)
    {
        ArgumentNullException.ThrowIfNull(participants);
        Participants = Array.AsReadOnly(participants.ToArray());
        RejectedTransactionalParticipants = Array.AsReadOnly((rejectedTransactionalParticipants ?? []).ToArray());
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
        RejectedTransactionalParticipants = [];
    }

    internal bool IsAdmitted { get; }
    internal IReadOnlyList<IMultiMemoryParticipantAdapter> Participants { get; }
    internal MultiMemoryFailure? Failure { get; }
    internal string? Warning { get; }
    internal string? EvidencePath { get; }
    internal bool CleanupFailed { get; }
    internal IReadOnlyList<B4ParticipantRequestMetadata> RejectedTransactionalParticipants { get; }
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
