using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>One Core result facade over the four existing Core results. Thin and behavior-preserving.</summary>
internal sealed record B0Result(
    B0Outcome Outcome,
    B0Failure? Failure,
    ArtifactEntry? Artifact,
    string OperationId,
    string CorrelationId,
    RecoveryInfo? Recovery,
    string? Warning);

/// <summary>Single typed exception surface over the existing append/whole/multi exceptions. Carries the
/// original exception unchanged; B1-B6 read one exception type without losing the wrapped type/message.</summary>
internal sealed record B0Exception(Exception Source)
{
    internal static B0Exception Of(Exception exception) => new(exception);
}

internal static class B0ResultWrappers
{
    internal static B0Result FromAppend(AppendStoreResult result) => new(
        MapAppendOutcome(result.Outcome),
        null,
        result.Entry,
        result.OperationId,
        result.OperationId,
        result.Recovery is null
            ? null
            : new RecoveryInfo(result.Recovery.JournalPath, result.Recovery.State, result.Recovery.Action, result.Recovery.Detail),
        result.Warning is null ? null : $"{result.Warning.Code}:{result.Warning.Message}");

    internal static B0Result FromWhole(WholeMemoryResult result) => new(
        MapWholeOutcome(result.Outcome),
        null,
        result.Entry,
        result.OperationId,
        result.OperationId,
        result.Recovery,
        result.Warning);

    internal static B0Result FromMulti(MultiMemoryAggregateResult result) => new(
        MapMultiOutcome(result.Outcome),
        result.Failure is null
            ? null
            : new B0Failure(B0FailureTaxonomy.MultiMemory, result.Failure.ToString()!, null),
        null,
        result.TransactionId,
        result.TransactionId,
        result.EvidencePath is null || result.Outcome is not MultiMemoryOutcome.RecoveryRequired
            ? null
            : new RecoveryInfo(result.EvidencePath, "recoveryRequired", "inspect coordinator record", result.Warning ?? "coordinator record retained for review"),
        result.Warning);

    internal static B0Result FromRecovery(RecoveryOutcome result)
    {
        B0Failure? failure = result.Block is null
            ? null
            : new B0Failure(B0FailureTaxonomy.RecoveryBlock, result.Block.Reason.ToString(), result.Block.Detail);
        return new B0Result(
            result.IsCommitted
                ? (result.CleanupPending ? B0Outcome.CommittedCleanupPending : B0Outcome.Committed)
                : B0Outcome.RecoveryRequired,
            failure,
            null,
            result.OperationId,
            result.OperationId,
            result.Block is null
                ? null
                : new RecoveryInfo(result.Block.EvidencePath, "recoveryRequired", "resolve retained evidence", result.Block.Detail),
            result.Warning);
    }

    private static B0Outcome MapAppendOutcome(AppendStoreOutcome o) => o switch
    {
        AppendStoreOutcome.Created => B0Outcome.Created,
        AppendStoreOutcome.Appended => B0Outcome.Appended,
        AppendStoreOutcome.SuccessWithWarning => B0Outcome.SuccessWithWarning,
        AppendStoreOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown append outcome.")
    };

    private static B0Outcome MapWholeOutcome(WholeMemoryOutcome o) => o switch
    {
        WholeMemoryOutcome.Created => B0Outcome.Created,
        WholeMemoryOutcome.Replaced => B0Outcome.Replaced,
        WholeMemoryOutcome.SuccessWithWarning => B0Outcome.SuccessWithWarning,
        WholeMemoryOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown whole-memory outcome.")
    };

    private static B0Outcome MapMultiOutcome(MultiMemoryOutcome o) => o switch
    {
        MultiMemoryOutcome.Committed => B0Outcome.Committed,
        MultiMemoryOutcome.CommittedCleanupPending => B0Outcome.CommittedCleanupPending,
        MultiMemoryOutcome.RolledBack => B0Outcome.RolledBack,
        MultiMemoryOutcome.Rejected => B0Outcome.Rejected,
        MultiMemoryOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown multi-memory outcome.")
    };
}
