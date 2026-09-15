using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core;

[JsonConverter(typeof(RecoveryOperationJsonConverter))]
internal enum RecoveryOperation
{
    Restore,
    Append,
    FileEntity
}

[JsonConverter(typeof(RecoveryLifecycleStateJsonConverter))]
internal enum RecoveryLifecycleState
{
    Prepared,
    Applying,
    Committed,
    CleanupPending,
    Cleaned,
    RecoveryRequired,
    RolledBack
}

internal abstract record RecoveryBody;

internal sealed record RecoveryEnvelopeDocument(
    string Format,
    int Version,
    RecoveryOperation Operation,
    int BodyVersion,
    string OperationId,
    string Scope,
    string? Address,
    RecoveryLifecycleState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JsonElement Body)
{
    [JsonIgnore]
    internal bool IsLegacy { get; init; }
    [JsonIgnore]
    internal RecoveryBody? TypedBody { get; init; }
}

internal sealed record RecoveryCandidate(
    RecoveryOperation Operation,
    string EvidencePath,
    string Scope,
    string CanonicalSubject,
    string OperationId,
    RecoveryEnvelopeDocument Envelope,
    bool IsLegacy);

internal enum RecoveryBlockReason
{
    Malformed,
    Unsafe,
    UnsupportedVersion,
    Ambiguous,
    Inconsistent,
    ReplayIoFailed,
    WrongScope
}

internal sealed record RecoveryBlock
{
    internal RecoveryBlock(
        string scope,
        string evidencePath,
        RecoveryOperation? operation,
        string operationId,
        RecoveryBlockReason reason,
        string detail,
        int? unsupportedVersion = null)
    {
        Scope = RequireText(scope, nameof(scope));
        EvidencePath = RequireText(evidencePath, nameof(evidencePath));
        OperationId = RequireText(operationId, nameof(operationId));
        Detail = RequireText(detail, nameof(detail));
        UnsupportedVersion = unsupportedVersion;
        Operation = operation;
        Reason = reason;
    }

    internal string Scope { get; }
    internal string EvidencePath { get; }
    internal RecoveryOperation? Operation { get; }
    internal string OperationId { get; }
    internal RecoveryBlockReason Reason { get; }
    internal string Detail { get; }
    internal int? UnsupportedVersion { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal sealed record RecoveryDiscoveryResult
{
    internal RecoveryDiscoveryResult(
        IReadOnlyList<RecoveryCandidate> candidates,
        RecoveryBlock? blockingEvidence,
        MultiMemoryTransactionRecord? coordinatorRecord = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Candidates = Array.AsReadOnly(candidates.ToArray());
        BlockingEvidence = blockingEvidence;
        CoordinatorRecord = coordinatorRecord;
    }

    internal IReadOnlyList<RecoveryCandidate> Candidates { get; }
    internal RecoveryBlock? BlockingEvidence { get; }
    internal MultiMemoryTransactionRecord? CoordinatorRecord { get; }
}

internal sealed record MaintenanceDiscoveryCursor
{
    internal MaintenanceDiscoveryCursor(string? afterRelativePath = null, int unsafeCandidatesToSkip = 0)
    {
        AfterRelativePath = afterRelativePath is null
            ? null
            : MaintenanceContractValidation.RequireRelativePath(afterRelativePath, nameof(afterRelativePath));
        ArgumentOutOfRangeException.ThrowIfNegative(unsafeCandidatesToSkip);
        UnsafeCandidatesToSkip = unsafeCandidatesToSkip;
    }

    internal string? AfterRelativePath { get; }
    internal int UnsafeCandidatesToSkip { get; }
}

internal sealed record RecoveryMaintenanceDiscoveryPage(
    IReadOnlyList<B2RecoveryMaintenanceProjection> Recovery,
    B3CoordinatorMaintenanceProjection? Coordinator,
    MaintenanceDiscoveryCursor? NextCursor,
    bool HasMore);

internal sealed record B2RecoveryMaintenanceProjection(
    RecoveryOperation Operation, string Scope, string EvidencePath,
    string CanonicalSubject, string OperationId, RecoveryLifecycleState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool IsTerminalAndVerified, bool IsReferenced, bool IsQuarantined,
    bool IsAmbiguous, bool IsPathSafe)
{
    internal MaintenanceProtectionReason? Protection { get; init; }
}

internal interface IB2RecoveryMaintenanceAdapter
{
    IReadOnlyList<B2RecoveryMaintenanceProjection> Project(RecoveryDiscoveryResult discovery, string expectedScope);
    RecoveryCleanupDecision EvaluateCleanup(B2RecoveryMaintenanceProjection view, IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record B3CoordinatorMaintenanceProjection(
    string Scope, string TransactionId, MultiMemoryTransactionState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<MultiMemoryParticipantManifest> Participants,
    IReadOnlyList<MultiMemoryParticipantCompletion> CompletedParticipants,
    IReadOnlyList<B4ParticipantMaintenanceProjection> TransactionalParticipants,
    IReadOnlyList<B4RejectedParticipantMaintenanceProjection> RejectedParticipants,
    bool IsTerminalAndVerified, bool IsPathSafe)
{
    internal MaintenanceProtectionReason? Protection { get; init; }
}

internal interface IB3CoordinatorMaintenanceAdapter
{
    B3CoordinatorMaintenanceProjection Project(MultiMemoryTransactionRecord record, string expectedScope);
    RecoveryCleanupDecision EvaluateCleanup(B3CoordinatorMaintenanceProjection projection, IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record B4ParticipantMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    string ParticipantIdentity, string OrderingKey, string Kind,
    TransactionalParticipantLifecycleState LifecycleState,
    TransactionalQuarantineState QuarantineState, string? EvidenceReference,
    string? EvidenceKind, string? EvidenceVersion, bool StateMayHaveChanged,
    bool Ambiguous, bool IsTerminalAndVerified, bool IsPathSafe)
{
    internal MaintenanceProtectionReason? Protection { get; init; }
}

internal sealed record B4RejectedParticipantMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    string ParticipantIdentity, string OrderingKey, string Kind,
    TransactionalFailureCode RejectionCode, bool IsPathSafe);

internal sealed record B4QuarantineMaintenanceProjection(
    string TransactionId, string OperationId, string Scope, string Address,
    TransactionalQuarantineState State, string? Reason, bool IsPathSafe);

internal interface IB4ParticipantMaintenanceAdapter
{
    B4ParticipantMaintenanceProjection Project(B4ParticipantRecoveryState state);
    B4RejectedParticipantMaintenanceProjection ProjectRejected(B4ParticipantRequestMetadata metadata);
    B4QuarantineMaintenanceProjection ProjectQuarantine(B4ParticipantRecoveryState state);
    RecoveryCleanupDecision EvaluateCleanup(B4ParticipantMaintenanceProjection projection, IReadOnlySet<string> referencedRelativePaths);
}

internal sealed record RecoveryCleanupDecision(
    bool Eligible, bool Protected, MaintenanceProtectionReason? Protection,
    string SafeRelativePath, string? Warning);

internal interface IRecoveryAdapter
{
    RecoveryOperation Operation { get; }
    bool IsCandidateFile(string fileName);
    RecoveryEnvelopeDocument ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory);
    RecoveryEnvelopeDocument ValidateForMutation(
        RecoveryEnvelopeDocument envelope,
        string expectedScope,
        string evidencePath,
        string expectedSubject,
        string expectedEvidenceDirectory);
    RecoveryOutcome RecoverOrCleanup(
        RecoveryEnvelopeDocument envelope,
        string evidencePath,
        CancellationToken cancellationToken);
    RecoveryOutcome Admit(RecoveryEnvelopeDocument envelope, string evidencePath);
}

internal sealed class RecoveryUnsafePathException : IOException
{
    internal RecoveryUnsafePathException(string message) : base(message) { }
}

internal sealed record RecoveryOutcome
{
    internal RecoveryOutcome(
        bool isBlocked,
        bool isCommitted,
        bool cleanupPending,
        string scope,
        string evidencePath,
        string operationId,
        RecoveryBlock? block,
        string? warning)
    {
        Scope = RequireText(scope, nameof(scope));
        EvidencePath = RequireText(evidencePath, nameof(evidencePath));
        OperationId = RequireText(operationId, nameof(operationId));
        if (isBlocked && block is null)
            throw new ArgumentException("A blocked recovery outcome requires blocking evidence.", nameof(block));

        IsBlocked = isBlocked;
        IsCommitted = isCommitted;
        CleanupPending = cleanupPending;
        Block = block;
        Warning = warning;
    }

    internal bool IsBlocked { get; }
    internal bool IsCommitted { get; }
    internal bool CleanupPending { get; }
    internal string Scope { get; }
    internal string EvidencePath { get; }
    internal string OperationId { get; }
    internal RecoveryBlock? Block { get; }
    internal string? Warning { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}
