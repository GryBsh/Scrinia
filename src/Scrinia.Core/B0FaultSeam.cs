using System.Collections.Immutable;

namespace Scrinia.Core;

/// <summary>One unified test-only fault-injection seam over the twelve existing seams.
/// Production code never registers a fault and never sets <see cref="Inject"/>; the seam is a
/// no-op unless a test schedules a fault at a <see cref="FaultPhase"/>.</summary>
/// <remarks>
/// The scheduled state and the exception factory are <c>AsyncLocal</c>-scoped (rather than a plain
/// process-wide static) so that the unification preserves the per-flow isolation the twelve original
/// seams already provided. Each original seam tracked its faults differently: the durable-write,
/// append, whole-memory, restore-journal, and multi-memory seams used context-local
/// <see cref="AsyncLocal{T}"/> closures, so a once-fault consumed in a forked continuation is not
/// visible back in the scheduling flow; the file-entity coordinator used an instance-scoped queue,
/// so its once-fault is consumed wherever the coordinator runs. To preserve both behaviours the
/// seam keeps two independent schedulers. The <c>mutable</c> scheduler (shared <see cref="HashSet{T}"/>
/// reference across the parent/child boundary) mirrors the instance-scoped coordinator queue; the
/// <c>immutable</c> scheduler (copy-on-write <see cref="ImmutableHashSet{T}"/>) mirrors the context-local
/// closures, so sibling (parallel) flows keep their own copies and never step on each other's
/// <see cref="ClearFaults"/>.
/// </remarks>
internal static class B0FaultSeam
{
    /// <summary>Instance-scoped-style scheduler (file-entity coordinator seam). Mutable set is shared
    /// across parent/child flow boundaries so consumption is visible to a later coordinator in the same
    /// root flow.</summary>
    private static readonly AsyncLocal<HashSet<(FaultPhase, string)>> _onceMut = new();
    private static readonly AsyncLocal<HashSet<(FaultPhase, string)>> _persistMut = new();

    /// <summary>Context-local scheduler (durable-write, append, whole-memory, restore-journal,
    /// multi-memory seams). Copy-on-write immutable set is fork-isolated so a once-fault consumed in a
    /// continuation leaves the scheduling flow's copy intact.</summary>
    private static readonly AsyncLocal<ImmutableHashSet<(FaultPhase, string)>> _onceImm = new();
    private static readonly AsyncLocal<ImmutableHashSet<(FaultPhase, string)>> _persistImm = new();

    private static readonly AsyncLocal<Func<FaultPhase, string, Exception?>?> _inject = new();

    /// <summary>Test-only exception factory. Production code never sets it; tests set it directly.</summary>
    public static Func<FaultPhase, string, Exception?>? Inject
    {
        get => _inject.Value;
        set => _inject.Value = value;
    }

    /// <summary>File-entity coordinator phases are instance-scoped; everything else is context-local.</summary>
    private static bool IsInstanceScoped(FaultPhase phase) => phase >= FaultPhase.FileEntityRawBodyRead;

    public static void FailOnceAt(FaultPhase phase, string tag)
    {
        if (IsInstanceScoped(phase))
        {
            HashSet<(FaultPhase, string)> current = _onceMut.Value ?? new();
            current.Add((phase, tag));
            _onceMut.Value = current;
        }
        else
        {
            _onceImm.Value = (_onceImm.Value ?? ImmutableHashSet<(FaultPhase, string)>.Empty).Add((phase, tag));
        }
    }

    public static void FailPersistentlyAt(FaultPhase phase, string tag)
    {
        if (IsInstanceScoped(phase))
        {
            HashSet<(FaultPhase, string)> current = _persistMut.Value ?? new();
            current.Add((phase, tag));
            _persistMut.Value = current;
        }
        else
        {
            _persistImm.Value = (_persistImm.Value ?? ImmutableHashSet<(FaultPhase, string)>.Empty).Add((phase, tag));
        }
    }

    public static void ClearFaults()
    {
        _onceMut.Value = new();
        _persistMut.Value = new();
        _onceImm.Value = ImmutableHashSet<(FaultPhase, string)>.Empty;
        _persistImm.Value = ImmutableHashSet<(FaultPhase, string)>.Empty;
    }

    public static void ThrowIfInjected(FaultPhase phase, string tag) =>
        throw ThrowIfInjectedOrNull(phase, tag)
            ?? new InvalidOperationException(
                $"Fault {phase} (tag={tag}) was scheduled but no exception factory produced one.");

    internal static Exception? ThrowIfInjectedOrNull(FaultPhase phase, string tag)
    {
        if (IsInstanceScoped(phase))
        {
            if (_persistMut.Value?.Contains((phase, tag)) == true)
                return Fault();
            if (_onceMut.Value?.Contains((phase, tag)) == true)
            {
                _onceMut.Value.Remove((phase, tag));
                return Fault();
            }
        }
        else
        {
            if (_persistImm.Value?.Contains((phase, tag)) == true)
                return Fault();
            if (_onceImm.Value?.Contains((phase, tag)) == true)
            {
                _onceImm.Value = _onceImm.Value.Remove((phase, tag));
                return Fault();
            }
        }

        return null;

        Exception Fault() => Inject?.Invoke(phase, tag)
            ?? new IOException($"Injected fault scheduled at {phase} (tag={tag}).");
    }

    // Six 1:1 enum adapters. Overlapping member names across Append/Whole are disambiguated by the
    // subsystem prefix; DurableWritePhase members are unique and stay unprefixed.
    internal static FaultPhase MapDurableWritePhase(DurableWritePhase phase) => phase switch
    {
        DurableWritePhase.BeforeWrite => FaultPhase.BeforeWrite,
        DurableWritePhase.BeforeFlush => FaultPhase.BeforeFlush,
        DurableWritePhase.BeforeMove => FaultPhase.BeforeMove,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown durable write phase.")
    };

    internal static FaultPhase MapAppendFaultPoint(AppendFaultPoint point) => point switch
    {
        AppendFaultPoint.RawArtifactRead => FaultPhase.AppendRawArtifactRead,
        AppendFaultPoint.RawSidecarRead => FaultPhase.AppendRawSidecarRead,
        AppendFaultPoint.JournalWrite => FaultPhase.AppendJournalWrite,
        AppendFaultPoint.JournalFlush => FaultPhase.AppendJournalFlush,
        AppendFaultPoint.JournalParse => FaultPhase.AppendJournalParse,
        AppendFaultPoint.JournalDelete => FaultPhase.AppendJournalDelete,
        AppendFaultPoint.ArchiveArtifactWrite => FaultPhase.AppendArchiveArtifactWrite,
        AppendFaultPoint.ArchiveArtifactFlush => FaultPhase.AppendArchiveArtifactFlush,
        AppendFaultPoint.ArchiveSidecarWrite => FaultPhase.AppendArchiveSidecarWrite,
        AppendFaultPoint.ArchiveSidecarFlush => FaultPhase.AppendArchiveSidecarFlush,
        AppendFaultPoint.LiveArtifactWrite => FaultPhase.AppendLiveArtifactWrite,
        AppendFaultPoint.LiveArtifactFlush => FaultPhase.AppendLiveArtifactFlush,
        AppendFaultPoint.LiveSidecarWrite => FaultPhase.AppendLiveSidecarWrite,
        AppendFaultPoint.LiveSidecarFlush => FaultPhase.AppendLiveSidecarFlush,
        AppendFaultPoint.VerificationRead => FaultPhase.AppendVerificationRead,
        AppendFaultPoint.RollbackWrite => FaultPhase.AppendRollbackWrite,
        AppendFaultPoint.RollbackDelete => FaultPhase.AppendRollbackDelete,
        AppendFaultPoint.ArchiveCleanup => FaultPhase.AppendArchiveCleanup,
        AppendFaultPoint.PruningDelete => FaultPhase.AppendPruningDelete,
        AppendFaultPoint.BeforeEvent => FaultPhase.AppendBeforeEvent,
        AppendFaultPoint.AfterEvent => FaultPhase.AppendAfterEvent,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown append fault point.")
    };

    internal static FaultPhase MapWholeMemoryFaultPoint(WholeMemoryFaultPoint point) => point switch
    {
        WholeMemoryFaultPoint.RawArtifactRead => FaultPhase.WholeRawArtifactRead,
        WholeMemoryFaultPoint.RawSidecarRead => FaultPhase.WholeRawSidecarRead,
        WholeMemoryFaultPoint.EvidenceWrite => FaultPhase.WholeEvidenceWrite,
        WholeMemoryFaultPoint.ArchiveArtifactWrite => FaultPhase.WholeArchiveArtifactWrite,
        WholeMemoryFaultPoint.ArchiveSidecarWrite => FaultPhase.WholeArchiveSidecarWrite,
        WholeMemoryFaultPoint.LiveArtifactWrite => FaultPhase.WholeLiveArtifactWrite,
        WholeMemoryFaultPoint.LiveSidecarWrite => FaultPhase.WholeLiveSidecarWrite,
        WholeMemoryFaultPoint.VerificationRead => FaultPhase.WholeVerificationRead,
        WholeMemoryFaultPoint.RollbackWrite => FaultPhase.WholeRollbackWrite,
        WholeMemoryFaultPoint.EvidenceCleanup => FaultPhase.WholeEvidenceCleanup,
        WholeMemoryFaultPoint.ArchiveCleanup => FaultPhase.WholeArchiveCleanup,
        WholeMemoryFaultPoint.CleanupStateWrite => FaultPhase.WholeCleanupStateWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown whole-memory fault point.")
    };

    internal static FaultPhase MapRestoreJournalTransition(RestoreJournalTransition t) => t switch
    {
        RestoreJournalTransition.BeforeRecoveryBarrierRead => FaultPhase.RestoreBeforeRecoveryBarrierRead,
        RestoreJournalTransition.BeforePreparedWrite => FaultPhase.RestoreBeforePreparedWrite,
        RestoreJournalTransition.AfterPreparedWrite => FaultPhase.RestoreAfterPreparedWrite,
        RestoreJournalTransition.BeforeReplayBody => FaultPhase.RestoreBeforeReplayBody,
        RestoreJournalTransition.BeforeReplaySidecar => FaultPhase.RestoreBeforeReplaySidecar,
        RestoreJournalTransition.AfterReplayDestinations => FaultPhase.RestoreAfterReplayDestinations,
        RestoreJournalTransition.BeforeCompletedWrite => FaultPhase.RestoreBeforeCompletedWrite,
        RestoreJournalTransition.AfterCompletedWrite => FaultPhase.RestoreAfterCompletedWrite,
        RestoreJournalTransition.BeforeCleanupDelete => FaultPhase.RestoreBeforeCleanupDelete,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown restore transition.")
    };

    internal static FaultPhase MapMultiMemoryTransition(MultiMemoryTransactionTransition t) => t switch
    {
        MultiMemoryTransactionTransition.PreparedPublished => FaultPhase.MultiPreparedPublished,
        MultiMemoryTransactionTransition.CommittedPublished => FaultPhase.MultiCommittedPublished,
        MultiMemoryTransactionTransition.CompletionProofPublished => FaultPhase.MultiCompletionProofPublished,
        MultiMemoryTransactionTransition.RecordDeleted => FaultPhase.MultiRecordDeleted,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown multi-memory transition.")
    };

    internal static FaultPhase MapFileEntityFaultPoint(FileEntityFaultPoint point) => point switch
    {
        FileEntityFaultPoint.RawBodyRead => FaultPhase.FileEntityRawBodyRead,
        FileEntityFaultPoint.RawMetaRead => FaultPhase.FileEntityRawMetaRead,
        FileEntityFaultPoint.ArchiveBodyWrite => FaultPhase.FileEntityArchiveBodyWrite,
        FileEntityFaultPoint.ArchiveMetaWrite => FaultPhase.FileEntityArchiveMetaWrite,
        FileEntityFaultPoint.BodyWrite => FaultPhase.FileEntityBodyWrite,
        FileEntityFaultPoint.MetaWrite => FaultPhase.FileEntityMetaWrite,
        FileEntityFaultPoint.VerificationRead => FaultPhase.FileEntityVerificationRead,
        FileEntityFaultPoint.AfterBodyDelete => FaultPhase.FileEntityAfterBodyDelete,
        FileEntityFaultPoint.RollbackWrite => FaultPhase.FileEntityRollbackWrite,
        FileEntityFaultPoint.EvidenceCleanup => FaultPhase.FileEntityEvidenceCleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown file-entity fault point.")
    };

    public enum FaultPhase
    {
        BeforeWrite, BeforeFlush, BeforeMove,
        AppendRawArtifactRead, AppendRawSidecarRead, AppendJournalWrite, AppendJournalFlush,
        AppendJournalParse, AppendJournalDelete, AppendArchiveArtifactWrite,
        AppendArchiveArtifactFlush, AppendArchiveSidecarWrite, AppendArchiveSidecarFlush,
        AppendLiveArtifactWrite, AppendLiveArtifactFlush, AppendLiveSidecarWrite,
        AppendLiveSidecarFlush, AppendVerificationRead, AppendRollbackWrite,
        AppendRollbackDelete, AppendArchiveCleanup, AppendPruningDelete, AppendBeforeEvent,
        AppendAfterEvent,
        WholeRawArtifactRead, WholeRawSidecarRead, WholeEvidenceWrite,
        WholeArchiveArtifactWrite, WholeArchiveSidecarWrite, WholeLiveArtifactWrite,
        WholeLiveSidecarWrite, WholeVerificationRead, WholeRollbackWrite,
        WholeEvidenceCleanup, WholeArchiveCleanup, WholeCleanupStateWrite,
        RestoreBeforeRecoveryBarrierRead, RestoreBeforePreparedWrite, RestoreAfterPreparedWrite,
        RestoreBeforeReplayBody, RestoreBeforeReplaySidecar, RestoreAfterReplayDestinations,
        RestoreBeforeCompletedWrite, RestoreAfterCompletedWrite, RestoreBeforeCleanupDelete,
        MultiPreparedPublished, MultiCommittedPublished, MultiCompletionProofPublished,
        MultiRecordDeleted,
        FileEntityRawBodyRead, FileEntityRawMetaRead, FileEntityArchiveBodyWrite,
        FileEntityArchiveMetaWrite, FileEntityBodyWrite, FileEntityMetaWrite,
        FileEntityVerificationRead, FileEntityAfterBodyDelete, FileEntityRollbackWrite,
        FileEntityEvidenceCleanup
    }
}
