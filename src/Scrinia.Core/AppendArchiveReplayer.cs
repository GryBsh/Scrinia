namespace Scrinia.Core;

/// <summary>
/// Owns the append archive-member durable-write leaf and the recovery archive-replay conductor moved
/// off <see cref="FileMemoryStore"/> in P29. Holds no <see cref="FileMemoryStore"/> instance — only the
/// injected append-fault hook the store exposes as a frozen static seam and the referenced
/// <see cref="AppendJournalWriter"/> owner (P19/P20/P26). <see cref="WriteArchiveMember"/> is shared with
/// the store's publish path (<c>PublishFrozenArchiveAsync</c>), so this owner is the single home for the
/// durable archive-write leaf; its fault points route through the same <c>DurableFile</c> static seam the
/// store used. No durable journal-state transition is owned here beyond the
/// <c>PersistAppendJournal</c> call that <see cref="ReplayArchiveMembers"/> already performs.
/// </summary>
internal sealed class AppendArchiveReplayer
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;
    private readonly AppendJournalWriter _appendJournalWriter;

    public AppendArchiveReplayer(
        Func<AppendFaultPoint, string, string, Exception?> injectAppendFault,
        AppendJournalWriter appendJournalWriter)
    {
        _injectAppendFault = injectAppendFault;
        _appendJournalWriter = appendJournalWriter;
    }

    /// <summary>Publicate one archive member to <paramref name="destination"/> under the archive-write
    /// fault seam, verifying an existing destination's bytes first. Shared durable-write leaf used by
    /// both the publish path and the recovery replay conductor.</summary>
    public ArchiveMemberStatus WriteArchiveMember(
        string memberName, RawMemberSnapshot member, string destination, string operationId,
        AppendFaultPoint writePoint, AppendFaultPoint flushPoint, CancellationToken cancellationToken)
    {
        if (!member.Existed)
            return ArchiveMemberStatus.NotRequired;

        try
        {
            Exception? injected = _injectAppendFault(writePoint, operationId, destination);
            if (injected is not null)
                throw injected;

            if (File.Exists(destination))
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(destination)!)!;
                RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                    new RecoveryScopeBinding("workspace", root), RecoveryEvidenceKind.AppendJournal,
                    destination, new RecoveryExpectedHash(member.Sha256), MaintenanceLimits.Hard,
                    cancellationToken).AsTask().GetAwaiter().GetResult();
                verification.SourceContent?.Dispose();
                if (verification.Outcome == RecoveryVerificationOutcome.Verified)
                    return ArchiveMemberStatus.AlreadyPresentVerified;

                throw new AppendStoreException(
                    AppendStoreErrorCode.RecoveryRequired,
                    operationId,
                    $"Archive {memberName} destination contains different bytes: {destination}",
                    recovery: new AppendRecoveryInfo(
                        AppendJournalCodec.GetJournalPath(Path.GetDirectoryName(destination)!),
                        AppendJournalState.RecoveryRequired.ToString(),
                        "resolve the retained append journal before retrying",
                        $"Archive {memberName} destination contains different bytes."));
            }

            DurableFile.AtomicWriteAllBytesForAppend(destination, member.Bytes, operationId, writePoint, flushPoint);
        }
        catch (AppendStoreException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendStoreException(
                AppendStoreErrorCode.RecoveryRequired,
                operationId,
                $"Archive {memberName} publication failed at '{destination}'.",
                exception,
                new AppendRecoveryInfo(
                    AppendJournalCodec.GetJournalPath(Path.GetDirectoryName(destination)!),
                    AppendJournalState.RecoveryRequired.ToString(),
                    "resolve the retained append journal before retrying",
                    exception.Message));
        }
        return ArchiveMemberStatus.Created;
    }

    /// <summary>Replay recorded archive members during recovery, persisting an
    /// <c>ArchiveInProgress</c> journal transition after each member. Recovery replay conductor: no
    /// durable journal-state transition beyond the <c>PersistAppendJournal</c> it drives.</summary>
    public AppendTransaction ReplayArchiveMembers(AppendTransaction transaction, string journalPath,
        CancellationToken cancellationToken)
    {
        ArchivePlan plan = transaction.Archive;
        if (!plan.Required)
            return transaction;

        ArchiveMemberStatus artifactStatus = plan.ArtifactStatus;
        if (artifactStatus is not (ArchiveMemberStatus.AlreadyPresentVerified or ArchiveMemberStatus.Created))
            artifactStatus = WriteArchiveMember("artifact", transaction.OldArtifact, plan.ArtifactArchivePath!,
                transaction.OperationId, AppendFaultPoint.ArchiveArtifactWrite,
                AppendFaultPoint.ArchiveArtifactFlush, cancellationToken);
        transaction = transaction with { Archive = plan.WithStatuses(artifactStatus, plan.SidecarStatus) };
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.ArchiveInProgress);

        ArchiveMemberStatus sidecarStatus = plan.SidecarStatus;
        if (sidecarStatus is not (ArchiveMemberStatus.AlreadyPresentVerified or ArchiveMemberStatus.Created))
        {
            sidecarStatus = transaction.OldSidecar.Existed && plan.SidecarArchivePath is not null
                ? WriteArchiveMember("sidecar", transaction.OldSidecar, plan.SidecarArchivePath, transaction.OperationId,
                    AppendFaultPoint.ArchiveSidecarWrite, AppendFaultPoint.ArchiveSidecarFlush, cancellationToken)
                : ArchiveMemberStatus.NotRequired;
        }

        return transaction with { Archive = plan.WithStatuses(artifactStatus, sidecarStatus) };
    }
}
