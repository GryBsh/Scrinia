using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Describes the durable outcome of one append operation.</summary>
/// <param name="Outcome">The durable outcome classification.</param>
/// <param name="Entry">The committed entry, or <see langword="null"/> when no entry is available.</param>
/// <param name="OperationId">The correlation identifier for this append attempt.</param>
/// <param name="Warning">A non-fatal warning produced after the durable commit.</param>
/// <param name="Recovery">Structured recovery evidence when the append needs recovery.</param>
public sealed record AppendStoreResult(
    AppendStoreOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    AppendStoreWarning? Warning = null,
    AppendRecoveryInfo? Recovery = null);

/// <summary>Outcomes returned by the store-facing append contract.</summary>
public enum AppendStoreOutcome
{
    /// <summary>The append created a previously missing memory.</summary>
    Created,
    /// <summary>The append added a chunk to an existing memory.</summary>
    Appended,
    /// <summary>The append committed, but a non-fatal warning was produced.</summary>
    SuccessWithWarning,
    /// <summary>The store retained evidence and requires recovery before retry.</summary>
    RecoveryRequired
}

/// <summary>A non-fatal condition that accompanied a durable append.</summary>
/// <param name="Code">The stable warning category.</param>
/// <param name="Message">Actionable detail describing the warning.</param>
public sealed record AppendStoreWarning(AppendWarningCode Code, string Message);

/// <summary>Stable warning categories for append operations.</summary>
public enum AppendWarningCode
{
    /// <summary>Post-commit archive cleanup or pruning did not complete.</summary>
    PostCommitCleanupFailed,
    /// <summary>Post-commit event delivery failed or was cancelled.</summary>
    EventFailed
}

/// <summary>Represents an append failure that cannot be expressed as a normal result.</summary>
public sealed class AppendStoreException : Exception
{
    /// <summary>Initializes an append exception with its stable category and correlation ID.</summary>
    /// <param name="code">The only permitted append exception category.</param>
    /// <param name="operationId">The append operation correlation identifier.</param>
    /// <param name="message">A safe diagnostic message.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    /// <param name="recovery">Structured retained-journal evidence, when recovery is blocked.</param>
    public AppendStoreException(
        AppendStoreErrorCode code,
        string operationId,
        string message,
        Exception? innerException = null,
        AppendRecoveryInfo? recovery = null)
        : base(message, innerException)
    {
        if (code is not AppendStoreErrorCode.RecoveryRequired and not AppendStoreErrorCode.Internal)
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported append store error code.");

        Code = code;
        OperationId = operationId;
        Recovery = recovery;
    }

    internal AppendStoreException(
        AppendStoreErrorCode code,
        string operationId,
        string message,
        RecoveryBlock discoveryBlock)
        : this(code, operationId, message, recovery: new AppendRecoveryInfo(
            discoveryBlock.EvidencePath,
            "recoveryRequired",
            "resolve retained evidence",
            discoveryBlock.Detail))
    {
        DiscoveryBlock = discoveryBlock;
    }

    /// <summary>Stable category describing why the exception escaped the normal result contract.</summary>
    public AppendStoreErrorCode Code { get; }
    /// <summary>The correlation identifier for the failed append attempt.</summary>
    public string OperationId { get; }
    /// <summary>Structured retained-journal evidence for recovery admission failures.</summary>
    public AppendRecoveryInfo? Recovery { get; }
    internal RecoveryBlock? DiscoveryBlock { get; }
}

internal sealed class AppendPostCommitCleanupException : Exception
{
    internal AppendPostCommitCleanupException(string operationId, ArtifactEntry entry, Exception innerException)
        : base("Append committed, but journal cleanup failed.", innerException)
    {
        OperationId = operationId;
        Entry = entry;
    }

    internal string OperationId { get; }
    internal ArtifactEntry Entry { get; }
}

/// <summary>Preserves the append operation ID when cancellation interrupts the store boundary.</summary>
public sealed class AppendOperationCanceledException : OperationCanceledException
{
    /// <summary>Initializes a cancellation exception with the append correlation identifier.</summary>
    /// <param name="operationId">The append operation correlation identifier.</param>
    /// <param name="innerException">The cancellation or boundary failure that interrupted the append.</param>
    public AppendOperationCanceledException(string operationId, Exception innerException)
        : base("Append was cancelled before its durable commit.", innerException)
        => OperationId = operationId;

    /// <summary>The correlation identifier for the cancelled append attempt.</summary>
    public string OperationId { get; }
}

/// <summary>Stable error categories for append exceptions.</summary>
public enum AppendStoreErrorCode
{
    /// <summary>The store could not prove a safe durable state.</summary>
    RecoveryRequired,
    /// <summary>An invariant or unexpected internal failure occurred.</summary>
    Internal
}

/// <summary>Safe recovery instructions for an append that needs operator action.</summary>
/// <param name="JournalPath">The safe path to the retained append evidence.</param>
/// <param name="State">The durable append state.</param>
/// <param name="Action">The recovery action the caller should take.</param>
/// <param name="Detail">Additional safe diagnostic detail without raw memory bytes.</param>
public sealed record AppendRecoveryInfo(
    string JournalPath,
    string State,
    string Action,
    string Detail);

internal enum AppendFaultPoint
{
    RawArtifactRead,
    RawSidecarRead,
    JournalWrite,
    JournalFlush,
    JournalParse,
    JournalDelete,
    ArchiveArtifactWrite,
    ArchiveArtifactFlush,
    ArchiveSidecarWrite,
    ArchiveSidecarFlush,
    LiveArtifactWrite,
    LiveArtifactFlush,
    LiveSidecarWrite,
    LiveSidecarFlush,
    VerificationRead,
    RollbackWrite,
    RollbackDelete,
    ArchiveCleanup,
    PruningDelete,
    BeforeEvent,
    AfterEvent
}

/// <summary>Factory for the append recovery-blocked exception, co-located with the
/// <see cref="AppendStoreException"/> / <see cref="AppendRecoveryInfo"/> / <see cref="AppendStoreErrorCode"/>
/// contract it builds. Pure builder: no fault injection, no durable write, no rollback bookkeeping.</summary>
internal static class AppendRecoveryBuilder
{
    /// <summary>Builds the <see cref="AppendStoreException"/> that signals an append needs operator
    /// recovery before it can be retried. <paramref name="operationId"/> defaults to "recovery";
    /// <paramref name="inner"/> defaults to null.</summary>
    public static AppendStoreException RecoveryRequired(string journalPath, string detail,
        Exception? inner = null, string operationId = "recovery") =>
        new(AppendStoreErrorCode.RecoveryRequired, operationId,
            $"Append recovery is required at '{journalPath}': {detail}", inner,
            new AppendRecoveryInfo(
                journalPath,
                AppendJournalState.RecoveryRequired.ToString(),
                "resolve the retained append journal before retrying",
                detail));
}
