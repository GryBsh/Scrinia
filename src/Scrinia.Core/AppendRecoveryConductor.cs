namespace Scrinia.Core;

/// <summary>
/// Owns the top-level append recovery state-machine conductor moved off <see cref="FileMemoryStore"/> in
/// P33. Holds no <see cref="FileMemoryStore"/> instance — only the five referenced append seam-owner
/// instances (P19/P20/P26, P27, P28, P29, P30, P32) and the injected append-fault hook the store exposes as
/// a frozen static seam. The conductor's scope directory is threaded in as a parameter (the caller already
/// derives it via the frozen <c>GetStoreDirForScope</c>), so the owner touches no store instance state.
/// </summary>
internal sealed class AppendRecoveryConductor
{
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;
    private readonly AppendArchiveReplayer _archiveReplayer;
    private readonly AppendJournalWriter _appendJournalWriter;
    private readonly AppendRecoveryEnforcer _recoveryEnforcer;
    private readonly ArchiveService _archiveService;
    private readonly AppendRollbackExecutor _rollbackExecutor;

    public AppendRecoveryConductor(
        Func<AppendFaultPoint, string, string, Exception?> injectAppendFault,
        AppendArchiveReplayer archiveReplayer,
        AppendJournalWriter appendJournalWriter,
        AppendRecoveryEnforcer recoveryEnforcer,
        ArchiveService archiveService,
        AppendRollbackExecutor rollbackExecutor)
    {
        _injectAppendFault = injectAppendFault;
        _archiveReplayer = archiveReplayer;
        _appendJournalWriter = appendJournalWriter;
        _recoveryEnforcer = recoveryEnforcer;
        _archiveService = archiveService;
        _rollbackExecutor = rollbackExecutor;
    }

    /// <summary>Recover a committed or aborted append journal for a scope without mutating the public store.
    /// Top-level recovery state machine: no store instance state — <paramref name="storeDirectory"/> is threaded
    /// in by the caller, the fault seam is the injected hook, and every durable leaf is a referenced owner.</summary>
    public async Task<bool> RecoverAppendUnsafeAsync(string scope, string storeDirectory,
        string operationId = "recovery", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ct.ThrowIfCancellationRequested();
        string journalPath = AppendJournalCodec.GetJournalPath(Path.Combine(storeDirectory, "versions"));
        RecoveryVerificationResult journalVerification = await RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
            journalPath, default, MaintenanceLimits.Hard, ct).ConfigureAwait(false);
        if (journalVerification.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            return false;

        AppendJournal journal;
        try
        {
            if (journalVerification.Outcome != RecoveryVerificationOutcome.Verified ||
                journalVerification.SourceContent is null)
                throw new IOException("Append journal could not be verified.");
            using RecoveryVerificationContent verifiedJournal = journalVerification.SourceContent;
            Exception? parseFailure = _injectAppendFault(AppendFaultPoint.JournalParse, operationId, journalPath);
            if (parseFailure is not null)
                throw parseFailure;
            var adapter = new AppendRecoveryAdapter();
            RecoveryVerifiedFileMetadata metadata = verifiedJournal.Metadata;
            RecoveryScopeBinding scopeBinding = new(scope, storeDirectory);
            RecoveryVerifiedEnvelope verifiedEnvelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
                in verifiedJournal, in scopeBinding, journalPath, storeDirectory, RecoveryEvidenceKind.AppendJournal, in metadata);
            RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope);
            journal = adapter.FromEnvelope(envelope, journalPath);
        }
        catch (Exception exception) when (exception is AppendJournalException or RecoveryEnvelopeException or IOException)
        {
            string recoveryOperationId = exception is AppendJournalException journalException
                ? journalException.OperationId ?? operationId
                : exception is RecoveryEnvelopeException
                    ? ((exception as RecoveryEnvelopeException)?.OperationId ?? operationId)
                    : operationId;
            throw AppendRecoveryBuilder.RecoveryRequired(journalPath, "The append journal failed integrity verification.", exception,
                recoveryOperationId);
        }

        // From this boundary onward the journal is the source of truth for correlation. The
        // caller's ID is only meaningful while admitting the journal; recovery work belongs to
        // the append attempt recorded by the durable evidence.
        operationId = journal.OperationId;
        AppendTransaction transaction = journal.ToTransaction();
        if (journal.State is AppendJournalState.RecoveryRequired or AppendJournalState.ArchiveInProgress)
        {
            try
            {
                transaction = _archiveReplayer.ReplayArchiveMembers(transaction, journalPath, ct);
                _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.Archived,
                    cancellationToken: ct,
                    detail: "Recorded archive members were deterministically completed.");
            }
            catch (Exception exception)
            {
                _recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath,
                    "Archive recovery could not prove every recorded archive member.", exception, operationId);
            }
        }

        bool currentMembers = await AppendRecoveryVerifier.AreAppendMembersCurrentAsync(transaction, ct).ConfigureAwait(false);
        if (journal.State is AppendJournalState.Committed or AppendJournalState.CleanupPending)
        {
            Exception? verificationFailure = _injectAppendFault(
                AppendFaultPoint.VerificationRead, journal.OperationId, journalPath);
            if (verificationFailure is not null)
                throw AppendRecoveryBuilder.RecoveryRequired(journalPath, "Committed append verification could not be completed.",
                    verificationFailure, journal.OperationId);

            if (currentMembers && !await AppendRecoveryVerifier.HasCommittedHashAsync(transaction, journal.CommittedHash, ct).ConfigureAwait(false))
                throw AppendRecoveryBuilder.RecoveryRequired(journalPath, "The committed append hash does not match its recorded value",
                    operationId: operationId);

            if (currentMembers)
            {
                ArchiveCleanupResult cleanup = _archiveService.PruneCommittedArchives(transaction, ct);
                if (cleanup.Failed)
                    throw AppendRecoveryBuilder.RecoveryRequired(journalPath,
                        cleanup.Detail,
                        operationId: transaction.OperationId);

                try
                {
                    _appendJournalWriter.TryDeleteAppendJournal(journalPath, transaction.OperationId);
                }
                catch (Exception exception)
                {
                    throw AppendRecoveryBuilder.RecoveryRequired(journalPath,
                        "Committed append cleanup could not remove its journal evidence.",
                        exception, transaction.OperationId);
                }
                return currentMembers;
            }

            if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))
                throw AppendRecoveryBuilder.RecoveryRequired(journalPath, "The committed append does not match its recorded bytes",
                    operationId: operationId);
        }
        else if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))
        {
            _recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath,
                "Raw rollback could not prove the original pair.", null, operationId);
        }

        try
        {
            _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RolledBack);
            _appendJournalWriter.TryDeleteAppendJournal(journalPath, transaction.OperationId);
        }
        catch (Exception exception)
        {
            _recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath,
                "Rollback completed but final journalization could not be proven.", exception, operationId);
        }
        return currentMembers;
    }
}
