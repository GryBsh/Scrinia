using System.Security.Cryptography;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Publishes one prepared whole-memory pair and retains recovery evidence on ambiguity.</summary>
internal sealed class WholeMemoryTransactionCoordinator : IWholeMemoryTransactionCoordinator
{
    private readonly FileMemoryStore _store;

    internal WholeMemoryTransactionCoordinator(FileMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<WholeMemoryResult> ExecuteAsync(
        WholeMemoryTransaction transaction,
        Func<CancellationToken, Task> stageAndPublish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(stageAndPublish);

        cancellationToken.ThrowIfCancellationRequested();
        string journalPath = _store.WholeMemoryJournalPath(transaction.Scope);
        string committedHash = ComputeCommittedHash(transaction);
        try
        {
            PersistEvidence(transaction, journalPath);
            PersistState(transaction, AppendJournalState.ArchiveInProgress, journalPath);
            await stageAndPublish(cancellationToken).ConfigureAwait(false);
            PersistState(transaction, AppendJournalState.Archived, journalPath);
            PersistState(transaction, AppendJournalState.NewPairInProgress, journalPath);
            PersistState(transaction, AppendJournalState.Committed, journalPath, committedHash);
        }
        catch (Exception failure)
        {
            if (TryRollback(transaction, journalPath))
            {
                try
                {
                    PersistState(transaction, AppendJournalState.RolledBack, journalPath);
                    DeleteEvidence(transaction, journalPath);
                    TryDeleteEmptyDirectory(Path.GetDirectoryName(journalPath));
                }
                catch (Exception cleanupFailure)
                {
                    throw RecoveryRequired(transaction, journalPath,
                        "Rollback completed but recovery evidence could not be removed.", cleanupFailure);
                }

                if (failure is OperationCanceledException)
                    throw;
                throw new WholeMemoryTransactionException(
                    WholeMemoryErrorCode.Internal,
                    transaction.OperationId,
                    "Whole-memory publication failed; the original pair was restored.",
                    failure);
            }

            throw RecoveryRequired(transaction, journalPath,
                "Whole-memory publication failed and byte-for-byte rollback could not be proved.", failure);
        }

        string[] pruneFailures = _store.PruneWholeArchives(
            transaction.Archive.PruneCandidates, transaction.OperationId);
        if (pruneFailures.Length > 0)
        {
            string detail = $"Whole-memory archive cleanup did not complete: {string.Join("; ", pruneFailures)}";
            try
            {
                FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.CleanupStateWrite, journalPath);
                PersistState(transaction, AppendJournalState.CleanupPending, journalPath, committedHash, detail);
            }
            catch (Exception stateFailure)
            {
                detail = $"{detail} Recovery state could not be persisted: {stateFailure.Message}";
            }

            return new WholeMemoryResult(
                WholeMemoryOutcome.SuccessWithWarning,
                null,
                transaction.OperationId,
                "Whole-memory commit succeeded, but archive cleanup requires attention.",
                new RecoveryInfo(journalPath, "cleanupPending", "resolve retained evidence", detail));
        }

        try
        {
            DeleteEvidence(transaction, journalPath);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(journalPath));
        }
        catch (Exception cleanupFailure)
        {
            try
            {
                FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.CleanupStateWrite, journalPath);
                PersistState(transaction, AppendJournalState.CleanupPending, journalPath, committedHash);
            }
            catch (Exception stateFailure)
            {
                cleanupFailure = new AggregateException(cleanupFailure, stateFailure);
            }

            return new WholeMemoryResult(
                WholeMemoryOutcome.SuccessWithWarning,
                null,
                transaction.OperationId,
                "Whole-memory commit succeeded, but recovery evidence cleanup failed.",
                new RecoveryInfo(journalPath, "cleanupPending", "resolve retained evidence",
                    cleanupFailure.Message));
        }

        return new WholeMemoryResult(
            transaction.OldArtifact.Existed || transaction.OldSidecar.Existed
                ? WholeMemoryOutcome.Replaced
                : WholeMemoryOutcome.Created,
            null, transaction.OperationId, null, null);
    }

    private void PersistEvidence(WholeMemoryTransaction transaction, string journalPath)
    {
        FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.EvidenceWrite, journalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        DurableFile.AtomicWriteAllBytes(journalPath, SerializeJournal(transaction, AppendJournalState.Prepared));
    }

    private void PersistState(WholeMemoryTransaction transaction, AppendJournalState state, string journalPath,
        string? committedHash = null, string? detail = null)
    {
        if (state != AppendJournalState.CleanupPending)
            FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.EvidenceWrite, journalPath);
        DurableFile.AtomicWriteAllBytes(journalPath, SerializeJournal(transaction, state, committedHash, detail));
    }

    private void DeleteEvidence(WholeMemoryTransaction transaction, string journalPath)
    {
        FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.EvidenceCleanup, journalPath);
        File.Delete(journalPath);
    }

    private bool TryRollback(WholeMemoryTransaction transaction, string journalPath)
    {
        try
        {
            FileMemoryStore.InjectWholeFault(WholeMemoryFaultPoint.RollbackWrite, journalPath);
            Restore(transaction.OldArtifact);
            Restore(transaction.OldSidecar);
            DeleteIfCreated(transaction.Archive.ArtifactPath);
            DeleteIfCreated(transaction.Archive.SidecarPath);
            _store.InvalidateScopeCacheAfterRollback(transaction.Scope);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Restore(RawMemberSnapshot snapshot)
    {
        if (!snapshot.Existed)
        {
            File.Delete(snapshot.Path);
            return;
        }

        DurableFile.AtomicWriteAllBytes(snapshot.Path, snapshot.Bytes);
    }

    private static void DeleteIfCreated(string? path)
    {
        if (path is not null)
            File.Delete(path);
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;
        if (!Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    private static byte[] SerializeJournal(WholeMemoryTransaction transaction, AppendJournalState state,
        string? committedHash = null, string? detail = null)
    {
        var appendTransaction = new AppendTransaction(
            transaction.OperationId,
            MemoryAddress.Parse(transaction.Target),
            transaction.Scope,
            !transaction.OldArtifact.Existed && !transaction.OldSidecar.Existed,
            transaction.OldArtifact,
            transaction.OldSidecar,
            transaction.NewArtifact,
            transaction.NewSidecar,
            new ArchivePlan(transaction.Archive.Required, transaction.Archive.ArtifactPath,
                transaction.Archive.SidecarPath,
                transaction.Archive.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
                transaction.Archive.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
                transaction.Archive.PruneCandidates),
            transaction.StartedAt);
        AppendJournal journal = AppendJournalCodec.CreatePrepared(appendTransaction);
        journal = state switch
        {
            AppendJournalState.Prepared => journal,
            AppendJournalState.NewPairInProgress => AppendJournalCodec.WithState(
                journal, AppendJournalState.NewPairInProgress),
            AppendJournalState.Archived => AppendJournalCodec.WithState(
                AppendJournalCodec.WithState(
                    journal, AppendJournalState.ArchiveInProgress),
                AppendJournalState.Archived),
            AppendJournalState.Committed => AppendJournalCodec.WithState(
                AppendJournalCodec.WithState(journal, AppendJournalState.NewPairInProgress),
                AppendJournalState.Committed, committedHash, detail),
            AppendJournalState.CleanupPending => AppendJournalCodec.WithState(
                AppendJournalCodec.WithState(
                    AppendJournalCodec.WithState(journal, AppendJournalState.NewPairInProgress),
                    AppendJournalState.Committed, committedHash, detail),
                AppendJournalState.CleanupPending, committedHash, detail),
            _ => AppendJournalCodec.WithState(journal, state)
        };
        return RecoveryEnvelopeCodec.Serialize(AppendJournalCodec.ToEnvelope(journal));
    }

    private static string ComputeCommittedHash(WholeMemoryTransaction transaction) =>
        Convert.ToHexStringLower(SHA256.HashData(
            transaction.NewArtifact.Bytes.Concat(transaction.NewSidecar.Bytes).ToArray()));

    private static WholeMemoryTransactionException RecoveryRequired(
        WholeMemoryTransaction transaction, string journalPath, string detail, Exception failure) =>
        new(WholeMemoryErrorCode.RecoveryRequired, transaction.OperationId, detail, failure,
            new RecoveryInfo(journalPath, "recoveryRequired", "resolve retained evidence", detail));
}
