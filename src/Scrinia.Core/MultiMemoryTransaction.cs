using Scrinia.Core.Models;

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
    RecordDeleted
}

internal enum MultiMemoryCommitStatus
{
    Unknown,
    Prepared,
    Committed
}

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
        IReadOnlyList<MultiMemoryParticipantRequest> participants)
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
    }

    internal string Scope { get; }
    internal IReadOnlyList<MultiMemoryParticipantRequest> Participants { get; }

    internal void Deconstruct(
        out string scope,
        out IReadOnlyList<MultiMemoryParticipantRequest> participants) =>
        (scope, participants) = (Scope, Participants);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
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
    string Subject { get; }
    string OperationId { get; }
    string EvidencePath { get; }
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
