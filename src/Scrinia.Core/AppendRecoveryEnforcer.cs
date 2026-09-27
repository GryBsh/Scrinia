namespace Scrinia.Core;

/// <summary>
/// Owns the append recovery-required enforcement conductor moved off <see cref="FileMemoryStore"/> in
/// P32. Holds no <see cref="FileMemoryStore"/> instance — only the referenced
/// <see cref="AppendJournalWriter"/> durable-write seam owner. Its durable-write fault exposure is
/// transitive through <c>AppendJournalWriter.PersistAppendJournal</c>, which routes through the frozen
/// <c>DurableFile</c> append-fault seam. No fault-injection hook is owned here.
/// </summary>
internal sealed class AppendRecoveryEnforcer
{
    private readonly AppendJournalWriter _appendJournalWriter;

    public AppendRecoveryEnforcer(AppendJournalWriter appendJournalWriter) =>
        _appendJournalWriter = appendJournalWriter;

    /// <summary>Enforce a recovery-required append journal transition, folding any journalization failure
    /// into the failure that is ultimately <see cref="throw"/>n. Conductor: no fault-injection hook; the
    /// durable-write fault seam is owned by <see cref="AppendJournalWriter"/>.</summary>
    public void PersistRecoveryRequiredOrThrow(AppendTransaction transaction, string journalPath,
        string detail, Exception? failure, string operationId)
    {
        try
        {
            _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RecoveryRequired, detail: detail);
        }
        catch (Exception journalFailure)
        {
            failure = failure is null
                ? journalFailure
                : new AggregateException(detail, failure, journalFailure);
        }
        throw AppendRecoveryBuilder.RecoveryRequired(journalPath, detail, failure, operationId);
    }
}
