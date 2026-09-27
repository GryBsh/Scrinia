namespace Scrinia.Core;

/// <summary>
/// Owns the append rollback decision trees moved off <see cref="FileMemoryStore"/> in P27. Holds no
/// <see cref="FileMemoryStore"/> instance — only the injected append-fault hook the store exposes.
/// Every other dependency is a frozen seam owner (<see cref="AppendRecoveryVerifier"/>) or a frozen
/// durable-write leaf (<see cref="DurableFile"/>) whose append-fault seam routes through the store's
/// static <c>InjectAppendFault</c>. No durable journal-state transition, so the owner stays free of
/// the recovery conductors that remain resident in the store.
/// </summary>
internal sealed class AppendRollbackExecutor
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

    public AppendRollbackExecutor(Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
    {
        _injectAppendFault = injectAppendFault;
    }

    /// <summary>Roll back a committed append by restoring each original live member and reconciling its
    /// recorded state. Pure rollback bookkeeping: no journal-state transition, no durable journal write.</summary>
    public bool TryRestoreAppendTransaction(AppendTransaction transaction)
    {
        bool artifactRestored = RestoreAppendMember(
            transaction.OldArtifact, transaction.NewArtifact, transaction.OperationId);
        bool sidecarRestored = RestoreAppendMember(
            transaction.OldSidecar, transaction.NewSidecar, transaction.OperationId);
        return artifactRestored && sidecarRestored &&
            AppendRecoveryVerifier.MatchesAppendState(transaction.OldArtifact) && AppendRecoveryVerifier.MatchesAppendState(transaction.OldSidecar);
    }

    /// <summary>Remove archive members the cancelled append created but that are no longer required. Pure
    /// rollback bookkeeping: no journal-state transition, no durable journal write.</summary>
    public bool TryRemoveCreatedArchiveMembers(AppendTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArchivePlan plan = transaction.Archive;
        return ReconcileArchiveMember(plan.ArtifactStatus, plan.ArtifactArchivePath,
                   transaction.OldArtifact, transaction.OperationId, cancellationToken) &&
                ReconcileArchiveMember(plan.SidecarStatus, plan.SidecarArchivePath,
                 transaction.OldSidecar, transaction.OperationId, cancellationToken);
    }

    /// <summary>Reconcile one archive member during cancellation rollback: retain it if it is no longer
    /// planned (or already present and byte-matching), otherwise prove ownership by matching bytes and
    /// delete it under the rollback-delete fault seam. Pure rollback decision tree.</summary>
    public bool ReconcileArchiveMember(
        ArchiveMemberStatus status, string? destination, RawMemberSnapshot expected, string operationId,
        CancellationToken cancellationToken = default)
    {
        if (status is ArchiveMemberStatus.NotRequired or ArchiveMemberStatus.AlreadyPresentVerified)
            return true;
        if (destination is null)
            return false;

        try
        {
            if (status is not ArchiveMemberStatus.Created)
            {
                if (!File.Exists(destination))
                    return true;
                // A planned member may already be present from an earlier operation. Matching
                // bytes make it safe to retain; a mismatch is evidence that ownership cannot be
                // established, so the journal must remain for recovery.
                return AppendRecoveryVerifier.MatchesArchiveBytes(destination, expected, cancellationToken);
            }
            if (!File.Exists(destination))
                return true;
            if (!AppendRecoveryVerifier.MatchesArchiveBytes(destination, expected, cancellationToken))
                return false;

            Exception? failure = _injectAppendFault(AppendFaultPoint.RollbackDelete, operationId, destination);
            if (failure is not null)
                throw failure;
            File.Delete(destination);
            return !File.Exists(destination);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore one original live member during rollback: re-write it if it previously existed
    /// (under the rollback-write fault seam), or delete the new live member if the original did not exist
    /// (under the rollback-delete fault seam). Pure rollback decision tree.</summary>
    public bool RestoreAppendMember(RawMemberSnapshot oldMember, RawMemberSnapshot newMember,
        string operationId)
    {
        try
        {
            if (oldMember.Existed)
            {
                if (File.Exists(oldMember.Path) && !AppendRecoveryVerifier.MatchesAppendMember(newMember) && !AppendRecoveryVerifier.MatchesAppendMember(oldMember))
                    return false;
                DurableFile.AtomicWriteAllBytesForAppend(oldMember.Path, oldMember.Bytes, operationId,
                    AppendFaultPoint.RollbackWrite, AppendFaultPoint.RollbackWrite);
                return true;
            }

            if (!File.Exists(oldMember.Path))
                return true;
            if (!AppendRecoveryVerifier.MatchesAppendMember(newMember))
                return false;
            Exception? failure = _injectAppendFault(AppendFaultPoint.RollbackDelete, operationId, oldMember.Path);
            if (failure is not null)
                throw failure;
            File.Delete(oldMember.Path);
            return !File.Exists(oldMember.Path);
        }
        catch
        {
            return false;
        }
    }
}
