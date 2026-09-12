using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Owns the single-scope admission boundary for a multi-memory transaction.</summary>
internal sealed class MultiMemoryTransactionCoordinator : IMultiMemoryTransactionCoordinator
{
    private readonly FileMemoryStore _store;
    private readonly TimeSpan _lockTimeout;

    internal MultiMemoryTransactionCoordinator(FileMemoryStore store, TimeSpan? lockTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (_lockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "The lock timeout must be positive.");
    }

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
        string transactionId = Guid.NewGuid().ToString("N");
        try
        {
            MultiMemoryAdmissionResult admission = await AdmitAsync(
                request, admitParticipant, cancellationToken).ConfigureAwait(false);
            if (!admission.IsAdmitted)
                return Rejected(transactionId, request.Scope, admission.Failure!.Value,
                    AddCleanupWarning(admission.Warning!, admission.CleanupFailed),
                    admission.EvidencePath);
            _ = DisposeAdapters(admission.Participants);
            throw new NotSupportedException("Multi-memory execution begins in the lifecycle task.");
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
        catch (IOException)
        {
            return Rejected(transactionId, request.Scope, MultiMemoryFailure.ReplayIoFailed,
                "The transaction scope could not be inspected safely.");
        }
    }

    public Task<MultiMemoryAggregateResult> RecoverAsync(string scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Multi-memory recovery is outside the admission boundary.");

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
