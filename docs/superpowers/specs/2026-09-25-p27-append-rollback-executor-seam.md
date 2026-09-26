# P27 — Append-Rollback-Executor Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P26 complete). Append recovery-orchestration phase: P24 (`AppendRollbackJournalReader`), P25 (`AppendRecoveryBuilder`), P26 (`AppendJournalWriter` forwarder consolidation).

**Phase:** P27 (twenty-seventh seam extraction; append recovery-orchestration phase, fourth sub-seam).
**Goal:** move the append rollback decision trees and their conductors off the store into a new instance owner `AppendRollbackExecutor` (mirroring the `AppendJournalWriter` fault-hook pattern), leaving the top-level recovery conductor `RecoverAppendUnsafeAsync` resident.

## Why this phase

The rollback family (`FileMemoryStore.cs` :3315–:3403) is the compensation logic that runs when an append must be rolled back after a durable failure or cancellation:

- `TryRestoreAppendTransaction` (:3315) — restores both original live members and proves their recorded state.
- `TryRemoveCreatedArchiveMembers` (:3325) — deletes archive members the cancelled append created.
- `ReconcileArchiveMember` (:3338) — one member's rollback decision (retain if byte-matching, else fault-guarded delete).
- `RestoreAppendMember` (:3375) — one member's rollback dual-path (re-write original if it existed, else fault-guarded delete of the new member).

All four are `private static` and use **only static dependencies**:

- `AppendRecoveryVerifier` predicates (`MatchesAppendMember`/`MatchesAppendState`/`MatchesArchiveBytes`) — P21/P22 owner, static.
- `AppendFaultPoint` — frozen enum.
- `DurableFile.AtomicWriteAllBytesForAppend` — frozen durable-write leaf (its append-fault seam routes through the store's static `InjectAppendFault`, exactly as `AppendJournalWriter.PersistAppendJournal` does for JournalWrite/Flush).
- `File.Exists`/`File.Delete` — system.
- `InjectAppendFault` — the store's **static** method (:97) = `B0FaultSeam.ThrowIfInjectedOrNull(...) ?? AppendFaultInjector?.Invoke(...)`, where `AppendFaultInjector` is a static `AsyncLocal` and `B0FaultSeam` is frozen.

No `FileMemoryStore` instance state is used by any of these. The fault-injection seam (`FileMemoryStore.AppendFaultInjector` / `B0FaultSeam`) is static and is exercised by tests (`AppendRecoveryTests.cs`, `AppendDurabilityTests.cs`) via `FileMemoryStore.AppendFaultInjector = ...` and `store.FailOnceAt(...)` — both resolved by the static `InjectAppendFault`. So an owner holding an injected `_injectAppendFault` hook wired to `FileMemoryStore.InjectAppendFault` preserves the seam identically, following the `AppendJournalWriter` pattern.

This is a **larger method move** than P24–P26 (four methods, ~90 lines, including the durable-write-coupled `RestoreAppendMember`), but it introduces **no new fault seam** and requires **no `DurableFile` refactor** — the RollbackWrite durable-write fault flows through `DurableFile.AtomicWriteAllBytesForAppend`'s existing static seam, and only the explicit `RollbackDelete` durable-delete fault needs the owner's injected hook (mirroring `AppendJournalWriter.TryDeleteAppendJournal`).

The top-level conductor `RecoverAppendUnsafeAsync` (:3133) remains resident — it owns the journal-state machine (verify → `ReplayArchiveMembers` → committed/cleanup branching → rollback) and injects faults at :3152 (`JournalParse`) and :3198 (`VerificationRead`). `ReplayArchiveMembers` (:3253), `PersistRecoveryRequiredOrThrow` (:3280), and `VerifyAppendMemberAsync` (:3296) remain resident — entangled with durable writes and journal-state transitions.

## The seam

### New owner: `AppendRollbackExecutor` (`src/Scrinia.Core/AppendRollbackExecutor.cs`)

Create `src/Scrinia.Core/AppendRollbackExecutor.cs` (new file) with the verbatim instance owner class below. The `_injectAppendFault` hook is wired to the store's static `InjectAppendFault`. `ReconcileArchiveMember` and `RestoreAppendMember` replace their `InjectAppendFault(...)` calls with `_injectAppendFault(...)`. The `DurableFile.AtomicWriteAllBytesForAppend(...)` call in `RestoreAppendMember` is unchanged (its fault flows through the existing static seam).

```csharp
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
```

### Store wiring (`FileMemoryStore.cs`)

1. **Declare** the field at :155 (after `_appendJournalWriter`):
   ```csharp
   private readonly AppendRollbackExecutor _rollbackExecutor;
   ```
2. **Construct** it in the store constructor at :321 (after the `_appendJournalWriter` construction block):
   ```csharp
   _rollbackExecutor = new AppendRollbackExecutor(
       InjectAppendFault);
   ```
3. **Delete** the four rollback methods (:3315–:3403): `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`.
4. **Repoint** the 6 external call sites (all within the store's recovery/orchestration methods that stay resident):
   - :2279 `if (!TryRestoreAppendTransaction(createTransaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(createTransaction))`
   - :2733 `TryRemoveCreatedArchiveMembers(rollbackTransaction, CancellationToken.None);` → `_rollbackExecutor.TryRemoveCreatedArchiveMembers(rollbackTransaction, CancellationToken.None);`
   - :2735 `TryRestoreAppendTransaction(rollbackTransaction);` → `_rollbackExecutor.TryRestoreAppendTransaction(rollbackTransaction);`
   - :2797 `if (!TryRestoreAppendTransaction(transaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`
   - :3230 `if (!TryRestoreAppendTransaction(transaction))` → `if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`
   - :3234 `else if (!TryRestoreAppendTransaction(transaction))` → `else if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))`

Confirm via grep that no remaining bare `TryRestoreAppendTransaction(` / `TryRemoveCreatedArchiveMembers(` / `ReconcileArchiveMember(` / `RestoreAppendMember(` call site is left in `FileMemoryStore.cs` after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — **do NOT refactor `DurableFile`; the RollbackWrite durable-write fault flows through its existing static seam**), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26 — do NOT re-edit that owner), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AppendRollbackJournalReader` (P24), `AppendRecoveryBuilder` (P25), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `AppendRollbackExecutor` owns the rollback decision trees + conductors; `FileMemoryStore` retains only the repointed call sites within its recovery/orchestration methods.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner; `DurableFile` unchanged.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRollbackExecutor|FullyQualifiedName~RestoreAppendMember|FullyQualifiedName~ReconcileArchiveMember"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if the two-sided filter matches zero tests, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
