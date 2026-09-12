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
    internal async Task<IReadOnlyList<IMultiMemoryParticipantAdapter>> AdmitAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admitParticipant);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MultiMemoryParticipantRequest> ordered = NormalizeAndOrder(_store, request);

        string storeDirectory = _store.GetStoreDirForScope(request.Scope);
        using FileLock scopeLock = _store.AcquireExclusiveScopeLock(request.Scope, _lockTimeout);
        cancellationToken.ThrowIfCancellationRequested();
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
                throw AdmissionFailure(MultiMemoryFailure.Inconsistent,
                    "The participant adapter identity does not match the request.");
            adapters.Add(adapter);
        }

        return adapters.AsReadOnly();
    }

    public async Task<MultiMemoryAggregateResult> ExecuteAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default)
    {
        string transactionId = Guid.NewGuid().ToString("N");
        try
        {
            IReadOnlyList<IMultiMemoryParticipantAdapter> adapters = await AdmitAsync(
                request, admitParticipant, cancellationToken).ConfigureAwait(false);
            return new MultiMemoryAggregateResult(transactionId, request.Scope, MultiMemoryOutcome.Committed,
                adapters.Select(adapter => new MultiMemoryParticipantOutcome(adapter.Subject, adapter.OperationId,
                    adapter.Kind, adapter.EvidencePath, false, false, "admitted")).ToArray(), null, null, null);
        }
        catch (FileLockTimeoutException)
        {
            return Rejected(transactionId, request.Scope, MultiMemoryFailure.LockTimeout,
                "The transaction scope lock could not be acquired.");
        }
        catch (MultiMemoryAdmissionException exception)
        {
            return Rejected(transactionId, request.Scope, exception.Failure, exception.Message,
                exception.EvidencePath);
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
}

internal sealed class MultiMemoryAdmissionException : Exception
{
    internal MultiMemoryAdmissionException(string message, MultiMemoryFailure failure, string? evidencePath = null)
        : base(message)
    {
        Failure = failure;
        EvidencePath = evidencePath;
    }

    internal MultiMemoryFailure Failure { get; }
    internal string? EvidencePath { get; }
}
