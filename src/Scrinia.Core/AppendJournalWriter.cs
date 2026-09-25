namespace Scrinia.Core;

/// <summary>
/// Owns the append-journal durable-write leaf moved off <see cref="FileMemoryStore"/> in P19.
/// Holds no <see cref="FileMemoryStore"/> instance — only the injected store-directory hook the
/// store exposes as a frozen primitive. Every other dependency is a frozen static codec or model.
/// </summary>
internal sealed class AppendJournalWriter
{
    private readonly Func<string, string> _storeDirForScope;

    public AppendJournalWriter(Func<string, string> storeDirForScope) => _storeDirForScope = storeDirForScope;

    public void PersistAppendJournal(AppendTransaction transaction, AppendJournalState state,
        string? committedHash = null, string? detail = null, CancellationToken? cancellationToken = null)
    {
        string storeDirectory = _storeDirForScope(transaction.Scope);
        Directory.CreateDirectory(Path.Combine(storeDirectory, "versions"));
        string path = AppendJournalCodec.GetJournalPath(Path.Combine(storeDirectory, "versions"));
        var adapter = new AppendRecoveryAdapter();
        AppendJournal current;
        if (!File.Exists(path))
        {
            current = AppendJournalCodec.CreatePrepared(transaction);
        }
        else
        {
            // Compensation is deliberately non-cancellable so rollback can establish a safe state.
            RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                new RecoveryScopeBinding(transaction.Scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
                path, default, MaintenanceLimits.Hard, cancellationToken.GetValueOrDefault()).AsTask().GetAwaiter().GetResult();
            if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
                throw new IOException("Existing append journal could not be verified.");
            using RecoveryVerificationContent verifiedJournal = verification.SourceContent;
            RecoveryVerifiedFileMetadata metadata = verifiedJournal.Metadata;
            RecoveryScopeBinding scopeBinding = new(transaction.Scope, storeDirectory);
            RecoveryVerifiedEnvelope verifiedEnvelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
                in verifiedJournal, in scopeBinding, path, storeDirectory, RecoveryEvidenceKind.AppendJournal, in metadata);
            current = adapter.FromEnvelope(((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope), path);
        }
        current = current with
        {
            ArchiveArtifactStatus = transaction.Archive.ArtifactStatus,
            ArchiveSidecarStatus = transaction.Archive.SidecarStatus
        };
        AppendJournal journal = AppendJournalCodec.WithState(current, state, committedHash, detail);
        byte[] bytes = RecoveryEnvelopeCodec.Serialize(adapter.ToEnvelope(journal));
        DurableFile.AtomicWriteAllBytesForAppend(path, bytes, transaction.OperationId,
            AppendFaultPoint.JournalWrite, AppendFaultPoint.JournalFlush);
    }
}
