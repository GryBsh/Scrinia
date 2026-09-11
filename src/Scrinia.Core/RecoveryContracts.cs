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
        string detail)
    {
        Scope = RequireText(scope, nameof(scope));
        EvidencePath = RequireText(evidencePath, nameof(evidencePath));
        OperationId = RequireText(operationId, nameof(operationId));
        Detail = RequireText(detail, nameof(detail));
        Operation = operation;
        Reason = reason;
    }

    internal string Scope { get; }
    internal string EvidencePath { get; }
    internal RecoveryOperation? Operation { get; }
    internal string OperationId { get; }
    internal RecoveryBlockReason Reason { get; }
    internal string Detail { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal sealed record RecoveryDiscoveryResult
{
    internal RecoveryDiscoveryResult(
        IReadOnlyList<RecoveryCandidate> candidates,
        RecoveryBlock? blockingEvidence)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Candidates = Array.AsReadOnly(candidates.ToArray());
        BlockingEvidence = blockingEvidence;
    }

    internal IReadOnlyList<RecoveryCandidate> Candidates { get; }
    internal RecoveryBlock? BlockingEvidence { get; }
}

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
