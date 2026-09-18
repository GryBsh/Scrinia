namespace Scrinia.Core;

/// <summary>Read-only projection from the canonical <see cref="RecoveryLifecycleState"/> to the four
/// machine-specific vocabularies. Pure function of the input enum: no I/O, no write path, no
/// transition logic, no caching. Does NOT rewrite or reference any existing state machine; the
/// canonical enum stays authoritative.</summary>
internal static class RecoveryLifecycleStateMapper
{
    /// <summary>Canonical enum to its lowercase wire string (matches the converter at
    /// <c>RecoveryEnvelope.cs:96</c>).</summary>
    public static string ToWireString(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => "prepared",
        RecoveryLifecycleState.Applying => "applying",
        RecoveryLifecycleState.Committed => "committed",
        RecoveryLifecycleState.CleanupPending => "cleanupPending",
        RecoveryLifecycleState.Cleaned => "cleaned",
        RecoveryLifecycleState.RecoveryRequired => "recoveryRequired",
        RecoveryLifecycleState.RolledBack => "rolledBack",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown recovery lifecycle state.")
    };

    /// <summary>Canonical enum to the closest existing <see cref="AppendJournalState"/> value, or
    /// <c>null</c> when no counterpart exists.</summary>
    public static string? ToAppendJournalState(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => nameof(AppendJournalState.Prepared),
        RecoveryLifecycleState.Applying => nameof(AppendJournalState.ArchiveInProgress),
        RecoveryLifecycleState.Committed => nameof(AppendJournalState.Committed),
        RecoveryLifecycleState.CleanupPending => nameof(AppendJournalState.CleanupPending),
        RecoveryLifecycleState.RecoveryRequired => nameof(AppendJournalState.RecoveryRequired),
        RecoveryLifecycleState.RolledBack => nameof(AppendJournalState.RolledBack),
        RecoveryLifecycleState.Cleaned => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown recovery lifecycle state.")
    };

    /// <summary>Canonical enum to the closest existing <see cref="WholeMemoryOutcome"/> value, or
    /// <c>null</c> when no counterpart exists.</summary>
    public static string? ToWholeMemoryOutcome(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.RecoveryRequired => nameof(WholeMemoryOutcome.RecoveryRequired),
        _ => null
    };

    /// <summary>Canonical enum to the closest existing <see cref="MultiMemoryTransactionState"/>
    /// value, or <c>null</c> when no counterpart exists.</summary>
    public static string? ToMultiMemoryState(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => nameof(MultiMemoryTransactionState.Prepared),
        RecoveryLifecycleState.Committed => nameof(MultiMemoryTransactionState.Committed),
        RecoveryLifecycleState.RecoveryRequired => nameof(MultiMemoryTransactionState.RecoveryRequired),
        _ => null
    };
}
