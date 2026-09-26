namespace Scrinia.Core;

/// <summary>
/// Owns the pure journal → <see cref="AppendTransaction"/> reconstruction used by the
/// cancellation/pre-commit rollback path. Static and side-effect free of store instance
/// state: it reads and verifies a verified append-recovery journal, then reconstructs an
/// in-memory transaction so a cancelled publish can compensate.
///
/// Compensation is deliberately non-cancellable so rollback can establish a safe state.
/// This owner never injects faults and performs no durable write — it only reads and verifies.
/// </summary>
internal static class AppendRollbackJournalReader
{
    internal static AppendTransaction? TryReadAppendTransactionForRollback(string journalPath, string scope)
    {
        try
        {
            var adapter = new AppendRecoveryAdapter();
            string storeDirectory = Path.GetDirectoryName(Path.GetDirectoryName(journalPath))!;
            // Compensation is deliberately non-cancellable so rollback can establish a safe state.
            RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                new RecoveryScopeBinding(scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
                journalPath, default, MaintenanceLimits.Hard, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
                return null;
            using RecoveryVerificationContent verifiedJournal = verification.SourceContent;
            RecoveryVerifiedFileMetadata metadata = verifiedJournal.Metadata;
            RecoveryScopeBinding scopeBinding = new(scope, storeDirectory);
            RecoveryVerifiedEnvelope verifiedEnvelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
                in verifiedJournal, in scopeBinding, journalPath, storeDirectory, RecoveryEvidenceKind.AppendJournal, in metadata);
            RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope);
            return adapter.FromEnvelope(envelope, journalPath).ToTransaction();
        }
        catch
        {
            return null;
        }
    }
}
