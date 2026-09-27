# P33 — Recovery-Orchestration Conductor Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P32 complete; append recovery-orchestration phase: P24 `AppendRollbackJournalReader`, P25 `AppendRecoveryBuilder`, P26 `AppendJournalWriter` forwarder consolidation, P27 `AppendRollbackExecutor`, P28 `AppendMemberVerifier`, P29 `AppendArchiveReplayer`, P30 `ArchiveService` prune conductor, P31 `AppendRecoveryVerifier` members-current predicate, P32 `AppendRecoveryEnforcer` recovery-required conductor).

**Phase:** P33 (thirty-third seam extraction; append recovery-orchestration phase, tenth sub-seam and the conductor goal).
**Goal:** move the top-level recovery state-machine conductor `RecoverAppendUnsafeAsync` wholesale off the store into a new owner `AppendRecoveryConductor`, the final step of the recovery-orchestration phase.

## Why this phase

The conductor had two resident methods (`PruneCommittedArchives`, `AreAppendMembersCurrent`, `PersistRecoveryRequiredOrThrow`) that were peeled off in P30–P32. What remained in `RecoverAppendUnsafeAsync` was a pure state machine over the five already-extracted seam-owner instances and the store's frozen static `InjectAppendFault` seam, with a single store-instance-state access: `GetStoreDirForScope(scope)` (which derives the scope→directory mapping through `_workspaceRoot`/`PathRouter`). That single access is the only thing preventing a wholesale move, so P33 threads `storeDirectory` in as a parameter.

### `RecoverAppendUnsafeAsync` (:3024–:3142) — top-level recovery state-machine conductor

`Task<bool>` recovery state machine. After P33 it holds no `FileMemoryStore` instance state — only the five seam-owner instances, the static fault seam, and static/frozen leaves. Its sole change is that `storeDirectory` becomes a constructor-independent parameter threaded in from the caller (the caller already computes `GetStoreDirForScope(scope)` inline at every call site).

Dependencies after the move:

- `_archiveReplayer` / `_appendJournalWriter` / `_recoveryEnforcer` / `_archiveService` / `_rollbackExecutor` — five referenced seam-owner instances (P19/P20/P26, P27, P28, P29, P30, P32). The owner holds all five as constructor dependencies (the same allowance `SearchService` exercises for `ListingService` in P7).
- `InjectAppendFault` — the store's **static** fault seam (:97); the owner routes it through an injected `_injectAppendFault` hook (same shape as `AppendArchiveReplayer`/`AppendJournalWriter`/`AppendRollbackExecutor`).
- `AppendJournalCodec.GetJournalPath` — frozen static.
- `RecoveryEvidenceVerifier.VerifySingleAsync` — frozen static.
- `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` — frozen static.
- `AppendRecoveryBuilder.RecoveryRequired` — frozen static (P25).
- `AppendRecoveryVerifier.AreAppendMembersCurrent` / `HasCommittedHash` — frozen static (P21/P22/P31).
- `AppendRecoveryAdapter` / `IRecoveryAdapter.ConsumeParsed` / `.FromEnvelope` — frozen adapter family.
- `RecoveryScopeBinding` / `RecoveryEvidenceKind` / `RecoveryVerificationOutcome` / `MaintenanceLimits.Hard` / `AppendJournalState` / `AppendFaultPoint` / `AppendTransaction` / `AppendJournal` / `RecoveryVerified*` / `AppendJournalException` / `RecoveryEnvelopeException` — frozen types.
- `ArgumentException.ThrowIfNullOrWhiteSpace` / `CancellationToken.ThrowIfCancellationRequested` / `Task.FromResult` — system/BCL.

The conductor no longer calls `GetStoreDirForScope` — `storeDirectory` is a parameter.

## The seam

### New owner: `AppendRecoveryConductor` (`src/Scrinia.Core/AppendRecoveryConductor.cs`)

Create `src/Scrinia.Core/AppendRecoveryConductor.cs` (new file) with the verbatim instance owner below. The moved body is byte-identical to the store's verbatim conductor except: (1) `storeDirectory` is a constructor parameter instead of `GetStoreDirForScope(scope)`; (2) the `InjectAppendFault(...)` calls become `_injectAppendFault(...)`; (3) the five seam-owner calls use the injected fields (`_archiveReplayer`, `_appendJournalWriter`, `_recoveryEnforcer`, `_archiveService`, `_rollbackExecutor`).

```csharp
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
    public Task<bool> RecoverAppendUnsafeAsync(string scope, string storeDirectory,
        string operationId = "recovery", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ct.ThrowIfCancellationRequested();
        string journalPath = AppendJournalCodec.GetJournalPath(Path.Combine(storeDirectory, "versions"));
        RecoveryVerificationResult journalVerification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
            journalPath, default, MaintenanceLimits.Hard, ct).AsTask().GetAwaiter().GetResult();
        if (journalVerification.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            return Task.FromResult(false);

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

        bool publicationWasVisible = AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct);
        if (journal.State is AppendJournalState.Committed or AppendJournalState.CleanupPending)
        {
            Exception? verificationFailure = _injectAppendFault(
                AppendFaultPoint.VerificationRead, journal.OperationId, journalPath);
            if (verificationFailure is not null)
                throw AppendRecoveryBuilder.RecoveryRequired(journalPath, "Committed append verification could not be completed.",
                    verificationFailure, journal.OperationId);

            bool currentMembers = AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct);
            if (currentMembers && !AppendRecoveryVerifier.HasCommittedHash(transaction, journal.CommittedHash, ct))
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
                return Task.FromResult(publicationWasVisible);
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
        return Task.FromResult(publicationWasVisible);
    }
}
```

Note: the moved body removes the `string storeDirectory = GetStoreDirForScope(scope);` line and gains `storeDirectory` as a constructor-independent parameter. `ArchiveCleanupResult` remains owned by `ArchiveService` (P30); the conductor references it via `_archiveService.PruneCommittedArchives`'s return type, which is public.

### Store wiring (`FileMemoryStore.cs`)

1. **Declare** the field alongside the other append seam-owners (near :154–:159):
   ```csharp
   private readonly AppendRecoveryConductor _recoveryConductor;
   ```
2. **Construct** it next to the other append seam-owner constructions (around :318–:333), passing the frozen static `InjectAppendFault` seam and the five already-constructed seam-owner instances:
   ```csharp
   _recoveryConductor = new AppendRecoveryConductor(
       InjectAppendFault, _archiveReplayer, _appendJournalWriter, _recoveryEnforcer, _archiveService, _rollbackExecutor);
   ```
3. **Delete** `RecoverAppendUnsafeAsync` (:3024–:3142) from the store.
4. **Repoint** the five call sites, passing `GetStoreDirForScope(scope)` as `storeDirectory`. To avoid recomputing the frozen scope→directory derivation where `RecoverScopeUnsafe` already needs it, bind a local at the two sites that call `RecoverScopeUnsafe` first; pass inline at the three that do not:
   - :2345–:2346 (`StorePersistentAsync`) — bind `string storeDirectory = GetStoreDirForScope(scope);` and use it for both `RecoverScopeUnsafe(scope, storeDirectory, ...)` and `_recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, "store-recovery", cancellationToken)`.
   - :2550–:2551 (`AppendAsync`) — bind `string storeDirectory = GetStoreDirForScope(scope);` and use it for both `RecoverScopeUnsafe(scope, storeDirectory, ...)` and `_recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, operationId, ct)`.
   - :2618 (`AppendAsyncCore`) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), operationId, ct)`.
   - :3007 (`RecoverAppendAsync`) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), "recovery", cancellationToken)`.
   - :3011 (`RecoverAppendParticipantForCoordinatorAsync`) — `_recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirForScope(scope), "coordinator-recovery", cancellationToken)`.

Confirm via grep that no remaining bare `RecoverAppendUnsafeAsync(` call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate (frozen primitive — the caller still derives `storeDirectory` via it; only the conductor no longer calls it), `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — do NOT refactor `DurableFile`; the durable-write fault flows through its static seam), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26), `AppendArchiveReplayer` (P29), `AppendRollbackExecutor` (P27), `AppendMemberVerifier` (P28), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AreAppendMembersCurrent` (P31), `AppendRecoveryEnforcer` (P32), `AppendRecoveryBuilder` (P25), `AppendRollbackJournalReader` (P24), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`PruneCommittedArchives`, `PublishFrozenArchiveAsync`, `PublishLiveAppendAsync`).

## Acceptance

- `AppendRecoveryConductor` owns `RecoverAppendUnsafeAsync`; `FileMemoryStore` retains only the five repointed call sites (four methods: `StorePersistentAsync`, `AppendAsync`, `AppendAsyncCore`, `RecoverAppendAsync`, `RecoverAppendParticipantForCoordinatorAsync`).
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"`.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
