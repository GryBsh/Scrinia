# P32 — Append-Recovery-Required Enforcer Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P31 complete; append recovery-orchestration phase: P24 `AppendRollbackJournalReader`, P25 `AppendRecoveryBuilder`, P26 `AppendJournalWriter` forwarder consolidation, P27 `AppendRollbackExecutor`, P28 `AppendMemberVerifier`, P29 `AppendArchiveReplayer`, P30 `ArchiveService` prune conductor, P31 `AppendRecoveryVerifier` members-current predicate).

**Phase:** P32 (thirty-second seam extraction; append recovery-orchestration phase, ninth sub-seam).
**Goal:** hoist the recovery-required enforcement conductor `PersistRecoveryRequiredOrThrow` off the store into a new owner `AppendRecoveryEnforcer` that holds the referenced `_appendJournalWriter` seam-owner — the last resident method in the top-level conductor `RecoverAppendUnsafeAsync`.

## Why this phase

`RecoverAppendUnsafeAsync` (`FileMemoryStore.cs` :3022–:3140) is the top-level recovery state-machine. Its two resident methods have been peeled off in order:
- P30 hoisted the shared durable-write prune conductor (`PruneCommittedArchives`) into `ArchiveService`.
- P31 hoisted the pure static predicate (`AreAppendMembersCurrent`) into `AppendRecoveryVerifier`.

The only remaining resident method is `PersistRecoveryRequiredOrThrow` (:3143–:3157). It is a genuine conductor (it enforces durable journal state and always throws), so it is extracted into a new owner rather than a predicate seam — the final cut before the conductor is thin enough to move wholesale.

### `PersistRecoveryRequiredOrThrow` (:3143–:3157) — recovery-required enforcement conductor

```csharp
private void PersistRecoveryRequiredOrThrow(AppendTransaction transaction, string journalPath,
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
```

Dependencies:

- `_appendJournalWriter.PersistAppendJournal` — the referenced durable-write seam owner (`AppendJournalWriter`, P19/P20/P26). The new owner holds it as a constructor dependency (the same allowance `AppendArchiveReplayer` exercises for `_appendJournalWriter`).
- `AppendJournalState.RecoveryRequired` — frozen enum.
- `AppendRecoveryBuilder.RecoveryRequired` — frozen static builder (P25).
- `AggregateException` / `Exception` — system/BCL.

No `FileMemoryStore` instance state. No direct fault seam — its durable-write fault exposure is transitive through `AppendJournalWriter.PersistAppendJournal` → the frozen `DurableFile.AtomicWriteAllBytesForAppend(...)` append-fault seam.

## The seam

### New owner: `AppendRecoveryEnforcer` (`src/Scrinia.Core/AppendRecoveryEnforcer.cs`)

Create `src/Scrinia.Core/AppendRecoveryEnforcer.cs` (new file) mirroring `AppendArchiveReplayer`:

```csharp
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
```

### Store wiring (`FileMemoryStore.cs`)

1. **Declare** the field alongside the other append seam-owners (near `_appendJournalWriter`, around :155):
   ```csharp
   private readonly AppendRecoveryEnforcer _recoveryEnforcer;
   ```
2. **Construct** it next to the `_appendJournalWriter` construction (around :323):
   ```csharp
   _recoveryEnforcer = new AppendRecoveryEnforcer(_appendJournalWriter);
   ```
3. **Delete** `PersistRecoveryRequiredOrThrow` (:3143–:3157) from the store.
4. **Repoint** the three call sites inside `RecoverAppendUnsafeAsync`:
   - :3079 `PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)` → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)`
   - :3125 `PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", null, operationId)` → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", null, operationId)`
   - :3136 `PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)` → `_recoveryEnforcer.PersistRecoveryRequiredOrThrow(transaction, journalPath, "...", exception, operationId)`

Confirm via grep that no remaining bare `PersistRecoveryRequiredOrThrow(` call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — do NOT refactor `DurableFile`; the durable-write fault flows through its static seam), the `DurableWriter` durable-write leaves (P9–D16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26 — do NOT re-edit that owner's `PersistAppendJournal` body, only pass the instance through), `AppendArchiveReplayer` (P29), `AppendRollbackExecutor` (P27), `AppendMemberVerifier` (P28), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AreAppendMembersCurrent` (P31 — do NOT alter), `AppendRecoveryBuilder` (P25), `AppendRollbackJournalReader` (P24), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `PruneCommittedArchives`, `PublishFrozenArchiveAsync`, `PublishLiveAppendAsync`).

## Acceptance

- `AppendRecoveryEnforcer` owns `PersistRecoveryRequiredOrThrow`; `FileMemoryStore` retains only the three repointed call sites within `RecoverAppendUnsafeAsync`.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"`.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
