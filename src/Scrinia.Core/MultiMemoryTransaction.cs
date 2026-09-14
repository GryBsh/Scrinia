using Scrinia.Core.Models;
using System.Collections.ObjectModel;

namespace Scrinia.Core;

internal enum MultiMemoryTransactionState
{
    Prepared,
    Committed,
    RecoveryRequired
}

internal enum MultiMemoryOutcome
{
    Committed,
    CommittedCleanupPending,
    RolledBack,
    RecoveryRequired,
    Rejected
}

internal enum MultiMemoryFailure
{
    Admission,
    Prepare,
    DecisionPublication,
    ParticipantCommit,
    Verification,
    Rollback,
    Cleanup,
    Malformed,
    UnsupportedVersion,
    Unsafe,
    Inconsistent,
    Ambiguous,
    ReplayIoFailed,
    LockTimeout
}

internal enum MultiMemoryTransactionTransition
{
    PreparedPublished,
    CommittedPublished,
    CompletionProofPublished,
    RecordDeleted
}

internal enum MultiMemoryCommitStatus
{
    Unknown,
    Prepared,
    Committed
}

internal enum TransactionalParticipantLifecycleState { Prepared, Committed, RolledBack, Changed, Ambiguous }
internal enum TransactionalQuarantineState { None, Quarantined }
internal enum TransactionalFailureCode
{
    Veto, DeclaredTransient, Permanent, UnknownTransient, Cancelled, InvalidResponse,
    CommitFailed, VerificationFailed, RollbackFailed, EvidenceInvalid,
    RetryMonotonicClockFailed, RetryMonotonicClockMovedBackwards, RetryElapsedOverflow
}

internal interface IMultiMemoryParticipantAdapterFactory
{
    Task<IMultiMemoryParticipantAdapter> CreateAsync(MultiMemoryParticipantRequest request, CancellationToken cancellationToken);
    Task<IMultiMemoryParticipantAdapter> RehydrateAsync(B4ParticipantRehydrationRequest request, CancellationToken cancellationToken);
}

internal sealed class MultiMemoryParticipantMismatchException : Exception;

internal sealed record MultiMemoryParticipantRequest
{
    internal MultiMemoryParticipantRequest(MemoryAddress address, string operationId, string kind)
    {
        if (!address.IsValid)
            throw new ArgumentException("A valid memory address is required.", nameof(address));

        Address = address;
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
    }

    internal MemoryAddress Address { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal string? ParticipantIdentity { get; private init; }
    internal int? ContractVersion { get; private init; }
    internal string? OrderingKey { get; private init; }
    internal IReadOnlyDictionary<string, string>? Payload { get; private init; }
    internal IReadOnlyDictionary<string, string>? Metadata { get; private init; }

    internal static MultiMemoryParticipantRequest ForTransactionalParticipant(
        string scope, MultiMemoryTransactionalParticipantRequest participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        return new MultiMemoryParticipantRequest(participant.Address, participant.OperationId, participant.Kind)
        {
            ParticipantIdentity = participant.ParticipantIdentity,
            ContractVersion = participant.ContractVersion,
            OrderingKey = participant.OrderingKey,
            Payload = participant.Payload,
            Metadata = participant.Metadata
        };
    }

    internal void Deconstruct(out MemoryAddress address, out string operationId, out string kind) =>
        (address, operationId, kind) = (Address, OperationId, Kind);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal sealed record MultiMemoryTransactionRequest
{
    internal MultiMemoryTransactionRequest(
        string scope,
        IReadOnlyList<MultiMemoryParticipantRequest> participants,
        IReadOnlyList<MultiMemoryTransactionalParticipantRequest>? transactionalParticipants = null)
    {
        Scope = RequireText(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (participants.Any(participant => participant is null))
            throw new ArgumentException("Participants cannot contain null values.", nameof(participants));

        var copiedParticipants = participants.ToArray();
        if (copiedParticipants
            .GroupBy(participant => participant.Address.ToString(), StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Participant subjects must be unique.", nameof(participants));

        Participants = Array.AsReadOnly(copiedParticipants);
        transactionalParticipants ??= Array.Empty<MultiMemoryTransactionalParticipantRequest>();
        if (transactionalParticipants.Any(participant => participant is null))
            throw new ArgumentException("Transactional participants cannot contain null values.", nameof(transactionalParticipants));
        if (copiedParticipants.Length + transactionalParticipants.Count > 4096)
            throw new ArgumentException("The combined participant count exceeds 4096.", nameof(transactionalParticipants));
        var copiedTransactional = transactionalParticipants.ToArray();
        if (copiedTransactional.Select(item => item.ParticipantIdentity).Distinct(StringComparer.Ordinal).Count() != copiedTransactional.Length)
            throw new ArgumentException("Transactional participant identities must be unique.", nameof(transactionalParticipants));
        if (copiedTransactional.Select(item => item.OrderingKey).Concat(Enumerable.Repeat<string?>(null, copiedParticipants.Length))
            .Where(key => key is not null).GroupBy(key => key!, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Participant ordering keys must be unique.", nameof(transactionalParticipants));
        TransactionalParticipants = Array.AsReadOnly(copiedTransactional);
    }

    internal string Scope { get; }
    internal IReadOnlyList<MultiMemoryParticipantRequest> Participants { get; }
    internal IReadOnlyList<MultiMemoryTransactionalParticipantRequest> TransactionalParticipants { get; }

    internal void Deconstruct(
        out string scope,
        out IReadOnlyList<MultiMemoryParticipantRequest> participants) =>
        (scope, participants) = (Scope, Participants);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal sealed record MultiMemoryTransactionalParticipantRequest
{
    internal MultiMemoryTransactionalParticipantRequest(MemoryAddress address, string participantIdentity, int contractVersion,
        string orderingKey, string operationId, string kind, IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!address.IsValid || address.Kind != MemoryAddressKind.Persistent) throw new ArgumentException("A persistent address is required.", nameof(address));
        Address = address;
        ParticipantIdentity = B4Validation.Text(participantIdentity, nameof(participantIdentity));
        if (contractVersion != 1) throw new ArgumentException("Contract version must be 1.", nameof(contractVersion));
        ContractVersion = contractVersion;
        OrderingKey = B4Validation.OrderingKey(orderingKey);
        OperationId = B4Validation.Text(operationId, nameof(operationId));
        Kind = B4Validation.Text(kind, nameof(kind));
        Payload = B4Validation.Dictionary(payload, 4096, nameof(payload));
        Metadata = B4Validation.Dictionary(metadata, 512, nameof(metadata));
    }
    internal MemoryAddress Address { get; }
    internal string ParticipantIdentity { get; }
    internal int ContractVersion { get; }
    internal string OrderingKey { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal IReadOnlyDictionary<string, string> Payload { get; }
    internal IReadOnlyDictionary<string, string> Metadata { get; }
}

internal static class B4Validation
{
    internal static string Text(string value, string name, int max = 512) => string.IsNullOrWhiteSpace(value) || value.Length > max
        ? throw new ArgumentException($"{name} must be nonblank and at most {max} characters.", name) : value;
    internal static string OrderingKey(string value)
    {
        Text(value, nameof(value), 64);
        if (value.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-') || value[0] == '-')
            throw new ArgumentException("Ordering key must be canonical.", nameof(value));
        return value;
    }
    internal static IReadOnlyList<string> Capabilities(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 32) throw new ArgumentException("Capabilities must contain 1 to 32 entries.", nameof(values));
        var canonical = values.Select((value, index) => Text(value, $"capabilities[{index}]").Trim()).ToArray();
        if (canonical.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64) || canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Length)
            throw new ArgumentException("Capabilities must be nonblank and ordinal-distinct.", nameof(values));
        Array.Sort(canonical, StringComparer.Ordinal);
        return Array.AsReadOnly(canonical);
    }
    internal static bool SafeReference(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        !Path.IsPathRooted(value) && !value.Contains('\\') && !value.Contains(':') &&
        value.Split('/').All(segment => segment is not ("." or ".."));
    internal static IReadOnlyDictionary<string, string> Dictionary(IReadOnlyDictionary<string, string> values, int valueLimit, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count > 64) throw new ArgumentException("Dictionary cannot contain more than 64 entries.", name);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            Text(pair.Key, "key", 128);
            Text(pair.Value, "value", valueLimit);
            if (!copy.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("Dictionary keys must be unique.", name);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}

internal sealed record MultiMemoryParticipantManifest
{
    public MultiMemoryParticipantManifest(string subject, string operationId, string kind)
    {
        Subject = RequireText(subject, nameof(subject));
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
    }

    public string Subject { get; }
    public string OperationId { get; }
    public string Kind { get; }

    internal void Deconstruct(out string subject, out string operationId, out string kind) =>
        (subject, operationId, kind) = (Subject, OperationId, Kind);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal sealed record MultiMemoryParticipantOutcome
{
    internal MultiMemoryParticipantOutcome(
        string subject,
        string operationId,
        string kind,
        string? evidencePath,
        bool committed,
        bool rolledBack,
        string? reason)
    {
        Subject = RequireText(subject, nameof(subject));
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
        if (evidencePath is not null && !IsSafeEvidencePath(evidencePath))
            throw new ArgumentException("Evidence path must be a safe relative path.", nameof(evidencePath));
        if (committed && rolledBack)
            throw new ArgumentException("A participant cannot be both committed and rolled back.");

        EvidencePath = evidencePath;
        Committed = committed;
        RolledBack = rolledBack;
        Reason = reason;
    }

    internal string Subject { get; }
    internal string OperationId { get; }
    internal string Kind { get; }
    internal string? EvidencePath { get; }
    internal bool Committed { get; }
    internal bool RolledBack { get; }
    internal string? Reason { get; }

    internal void Deconstruct(
        out string subject,
        out string operationId,
        out string kind,
        out string? evidencePath,
        out bool committed,
        out bool rolledBack,
        out string? reason) =>
        (subject, operationId, kind, evidencePath, committed, rolledBack, reason) =
        (Subject, OperationId, Kind, EvidencePath, Committed, RolledBack, Reason);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;

    internal static bool IsSafeEvidencePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        path.Split('/', StringSplitOptions.None).All(segment => segment is not ".." and not ".");
}

internal sealed record MultiMemoryAggregateResult
{
    internal MultiMemoryAggregateResult(
        string transactionId,
        string scope,
        MultiMemoryOutcome outcome,
        IReadOnlyList<MultiMemoryParticipantOutcome> participants,
        MultiMemoryFailure? failure,
        string? warning,
        string? evidencePath)
    {
        TransactionId = RequireText(transactionId, nameof(transactionId));
        Scope = RequireText(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Any(participant => participant is null))
            throw new ArgumentException("Participants cannot contain null values.", nameof(participants));
        if (outcome == MultiMemoryOutcome.RecoveryRequired &&
            !MultiMemoryParticipantOutcome.IsSafeEvidencePath(evidencePath ?? string.Empty))
            throw new ArgumentException(
                "A recovery-required result needs a safe evidence path.", nameof(evidencePath));

        Outcome = outcome;
        Participants = Array.AsReadOnly(participants.ToArray());
        Failure = failure;
        Warning = warning;
        EvidencePath = evidencePath;
    }

    internal string TransactionId { get; }
    internal string Scope { get; }
    internal MultiMemoryOutcome Outcome { get; }
    internal IReadOnlyList<MultiMemoryParticipantOutcome> Participants { get; }
    internal MultiMemoryFailure? Failure { get; }
    internal string? Warning { get; }
    internal string? EvidencePath { get; }

    internal void Deconstruct(
        out string transactionId,
        out string scope,
        out MultiMemoryOutcome outcome,
        out IReadOnlyList<MultiMemoryParticipantOutcome> participants,
        out MultiMemoryFailure? failure,
        out string? warning,
        out string? evidencePath) =>
        (transactionId, scope, outcome, participants, failure, warning, evidencePath) =
        (TransactionId, Scope, Outcome, Participants, Failure, Warning, EvidencePath);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal interface IMultiMemoryParticipantAdapter
{
    string Kind { get; }
    string Scope { get; }
    string Subject { get; }
    string OperationId { get; }
    string EvidencePath { get; }
    bool? ObservableStateChanged => true;
    Task PrepareAsync(CancellationToken cancellationToken);
    Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task VerifyCommittedAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
}

internal interface IMultiMemoryTransactionCoordinator
{
    Task<MultiMemoryAggregateResult> ExecuteAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken,
            Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default);

    Task<MultiMemoryAggregateResult> RecoverAsync(
        string scope,
        CancellationToken cancellationToken = default);
}
