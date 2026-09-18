namespace Scrinia.Core;

/// <summary>One unified outcome surface adopted from the four existing outcome enums.</summary>
internal enum B0Outcome
{
    Created, Appended, Replaced, Committed, CommittedCleanupPending,
    RolledBack, Rejected, SuccessWithWarning, RecoveryRequired
}

/// <summary>Which failure taxonomy produced a <see cref="B0Failure"/>.</summary>
internal enum B0FailureTaxonomy
{
    AppendStore, WholeMemory, MultiMemory, RecoveryBlock
}

/// <summary>Discriminated container unifying AppendStoreErrorCode, WholeMemoryErrorCode,
/// MultiMemoryFailure (14 members), and RecoveryBlockReason (7 members). Records the source
/// taxonomy so B1-B6 consumers read one failure type instead of four.</summary>
internal sealed record B0Failure(B0FailureTaxonomy Taxonomy, string Code, string? Detail);
