using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Immutable inputs captured for one persistent whole-memory publication.</summary>
internal sealed record WholeMemoryTransaction
{
    internal WholeMemoryTransaction(
        string operationId,
        string scope,
        string target,
        bool isEphemeral,
        RawMemberSnapshot oldArtifact,
        RawMemberSnapshot oldSidecar,
        RawMemberSnapshot newArtifact,
        RawMemberSnapshot newSidecar,
        ArchivePairPlan archive,
        DateTimeOffset startedAt)
    {
        OperationId = RequireText(operationId, nameof(operationId));
        Scope = RequireText(scope, nameof(scope));
        Target = RequireText(target, nameof(target));
        OldArtifact = oldArtifact ?? throw new ArgumentNullException(nameof(oldArtifact));
        OldSidecar = oldSidecar ?? throw new ArgumentNullException(nameof(oldSidecar));
        NewArtifact = newArtifact ?? throw new ArgumentNullException(nameof(newArtifact));
        NewSidecar = newSidecar ?? throw new ArgumentNullException(nameof(newSidecar));
        Archive = archive ?? throw new ArgumentNullException(nameof(archive));
        IsEphemeral = isEphemeral;
        StartedAt = startedAt;
    }

    internal string OperationId { get; }
    internal string Scope { get; }
    internal string Target { get; }
    internal bool IsEphemeral { get; }
    internal RawMemberSnapshot OldArtifact { get; }
    internal RawMemberSnapshot OldSidecar { get; }
    internal RawMemberSnapshot NewArtifact { get; }
    internal RawMemberSnapshot NewSidecar { get; }
    internal ArchivePairPlan Archive { get; }
    internal DateTimeOffset StartedAt { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

/// <summary>Frozen archive destinations and cleanup candidates for one memory write.</summary>
internal sealed record ArchivePairPlan
{
    internal ArchivePairPlan(
        bool required,
        string? artifactPath,
        string? sidecarPath,
        IReadOnlyList<string> pruneCandidates)
    {
        ArgumentNullException.ThrowIfNull(pruneCandidates);
        if (required && string.IsNullOrWhiteSpace(artifactPath))
            throw new ArgumentException("A required archive must have an artifact path.", nameof(artifactPath));
        if (required && string.IsNullOrWhiteSpace(sidecarPath))
            throw new ArgumentException("A required archive must have a sidecar path.", nameof(sidecarPath));

        Required = required;
        ArtifactPath = artifactPath;
        SidecarPath = sidecarPath;
        PruneCandidates = Array.AsReadOnly(pruneCandidates.ToArray());
    }

    internal bool Required { get; }
    internal string? ArtifactPath { get; }
    internal string? SidecarPath { get; }
    internal IReadOnlyList<string> PruneCandidates { get; }
}

internal enum WholeMemoryOutcome
{
    Created,
    Replaced,
    SuccessWithWarning,
    RecoveryRequired
}

internal sealed record WholeMemoryResult
{
    internal WholeMemoryResult(
        WholeMemoryOutcome outcome,
        ArtifactEntry? entry,
        string operationId,
        string? warning,
        RecoveryInfo? recovery)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        if (outcome == WholeMemoryOutcome.RecoveryRequired && recovery is null)
            throw new ArgumentException("Recovery evidence is required for a recovery result.", nameof(recovery));

        Outcome = outcome;
        Entry = entry;
        OperationId = operationId;
        Warning = warning;
        Recovery = recovery;
    }

    internal WholeMemoryOutcome Outcome { get; }
    internal ArtifactEntry? Entry { get; }
    internal string OperationId { get; }
    internal string? Warning { get; }
    internal RecoveryInfo? Recovery { get; }
}

internal sealed class WholeMemoryTransactionException : IOException
{
    internal WholeMemoryTransactionException(
        WholeMemoryErrorCode code,
        string operationId,
        string message,
        Exception? inner = null,
        RecoveryInfo? recovery = null)
        : base(message, inner)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        if (code == WholeMemoryErrorCode.RecoveryRequired && recovery is null)
            throw new ArgumentException("Recovery evidence is required for a recovery error.", nameof(recovery));

        Code = code;
        OperationId = operationId;
        Recovery = recovery;
    }

    internal WholeMemoryErrorCode Code { get; }
    internal string OperationId { get; }
    internal RecoveryInfo? Recovery { get; }
}

internal enum WholeMemoryErrorCode
{
    RecoveryRequired,
    Internal
}

internal enum WholeMemoryFaultPoint
{
    RawArtifactRead,
    RawSidecarRead,
    EvidenceWrite,
    ArchiveArtifactWrite,
    ArchiveSidecarWrite,
    LiveArtifactWrite,
    LiveSidecarWrite,
    VerificationRead,
    RollbackWrite,
    EvidenceCleanup
}

internal sealed record RecoveryInfo
{
    internal RecoveryInfo(string evidencePath, string state, string action, string detail)
    {
        EvidencePath = RequireText(evidencePath, nameof(evidencePath));
        State = RequireText(state, nameof(state));
        Action = RequireText(action, nameof(action));
        Detail = RequireText(detail, nameof(detail));
    }

    internal string EvidencePath { get; }
    internal string State { get; }
    internal string Action { get; }
    internal string Detail { get; }

    private static string RequireText(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", name)
            : value;
}

internal interface IWholeMemoryTransactionCoordinator
{
    Task<WholeMemoryResult> ExecuteAsync(
        WholeMemoryTransaction transaction,
        Func<CancellationToken, Task> stageAndPublish,
        CancellationToken cancellationToken = default);
}

internal sealed record FileEntityMutation
{
    internal FileEntityMutation(
        string entityKind,
        string name,
        string bodyPath,
        string metaPath,
        string versionsDirectory,
        byte[] newBody,
        byte[]? newMeta,
        bool deleteBody,
        bool deleteMeta)
    {
        EntityKind = RequireText(entityKind, nameof(entityKind));
        Name = RequireText(name, nameof(name));
        BodyPath = RequireText(bodyPath, nameof(bodyPath));
        MetaPath = RequireText(metaPath, nameof(metaPath));
        VersionsDirectory = RequireText(versionsDirectory, nameof(versionsDirectory));
        ArgumentNullException.ThrowIfNull(newBody);
        NewBody = newBody.ToArray();
        NewMeta = newMeta?.ToArray();
        DeleteBody = deleteBody;
        DeleteMeta = deleteMeta;
    }

    internal string EntityKind { get; }
    internal string Name { get; }
    internal string BodyPath { get; }
    internal string MetaPath { get; }
    internal string VersionsDirectory { get; }
    internal byte[] NewBody { get; }
    internal byte[]? NewMeta { get; }
    internal bool DeleteBody { get; }
    internal bool DeleteMeta { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

internal interface IFileEntityTransactionCoordinator
{
    Task<FileEntityMutationResult> ExecuteAsync(
        FileEntityMutation mutation,
        CancellationToken cancellationToken = default);
}

internal sealed record FileEntityMutationResult(
    WholeMemoryOutcome Outcome,
    string OperationId,
    string? Warning,
    RecoveryInfo? Recovery);

internal enum FileEntityFaultPoint
{
    RawBodyRead,
    RawMetaRead,
    ArchiveBodyWrite,
    ArchiveMetaWrite,
    BodyWrite,
    MetaWrite,
    VerificationRead,
    AfterBodyDelete,
    RollbackWrite,
    EvidenceCleanup
}
