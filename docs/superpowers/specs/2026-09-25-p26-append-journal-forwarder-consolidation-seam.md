# P26 — Append-Journal Forwarder Consolidation Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P25 complete). Append recovery-orchestration phase: P24 (`AppendRollbackJournalReader`), P25 (`AppendRecoveryBuilder`) extracted the two pure leaves; the remaining recovery methods are entangled with fault injection and durable writes.

**Phase:** P26 (twenty-sixth seam extraction; append recovery-orchestration phase, third sub-seam).
**Goal:** consolidate the append-journal durable lifecycle into the existing `AppendJournalWriter` owner (P19/P20) by moving `DeleteAppendJournal`'s path derivation there and deleting the three redundant private forwarders, so the store no longer carries dangling 1:1 delegations.

## Why this phase

The store currently holds three private append-journal forwarders inside the recovery-orchestration region (`FileMemoryStore.cs` :3296–:3307):

```csharp
private void PersistAppendJournal(AppendTransaction transaction, AppendJournalState state,
    string? committedHash = null, string? detail = null, CancellationToken? cancellationToken = null) =>
    _appendJournalWriter.PersistAppendJournal(transaction, state, committedHash, detail, cancellationToken);

private void DeleteAppendJournal(AppendTransaction transaction)
{
    string path = AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirForScope(transaction.Scope), "versions"));
    TryDeleteAppendJournal(path, transaction.OperationId);
}

private void TryDeleteAppendJournal(string path, string operationId) =>
    _appendJournalWriter.TryDeleteAppendJournal(path, operationId);
```

`AppendJournalWriter` (`src/Scrinia.Core/AppendJournalWriter.cs`, P19/P20 owner) **already implements** `PersistAppendJournal` (owner :20) and `TryDeleteAppendJournal` (owner :60), and already holds the injected `storeDirForScope` hook (owner :10, used at :23) plus the `AppendJournalCodec.GetJournalPath` frozen call (owner :25). The store's `DeleteAppendJournal` is therefore a path-derivation wrapper whose only extra work (`GetStoreDirForScope` → the owner's `_storeDirForScope` hook, byte-identical via the constructor wiring `new AppendJournalWriter(scope => GetStoreDirForScope(scope), ...)`) belongs in the owner. All three forwarders are literal 1:1 pass-throughs with zero logic — pure indirection the store no longer needs.

Moving them removes the last dangling append-journal forwarder indirection from the god store and makes `AppendJournalWriter` the sole owner of the full append-journal lifecycle (prepare → state-transition → delete). The behavior is identical: every forwarder argument order and default is preserved on the already-existing owner methods.

The remaining recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`) stay resident — they are entangled with `InjectAppendFault` durable-inject + `DurableFile` writes + rollback bookkeeping (and `DurableFile.AtomicWriteAllBytesForAppend` couples to `FileMemoryStore.InjectAppendFault` at `DurableFile.cs:27`), a larger later phase. `AreAppendMembersCurrent` (:3325) is a one-line AND over already-extracted predicates — low-value churn.

## The seam

### Extend `AppendJournalWriter` (`src/Scrinia.Core/AppendJournalWriter.cs`)

Append the `DeleteAppendJournal` lifecycle method (verbatim path derivation; uses the owner's existing `_storeDirForScope` hook + `TryDeleteAppendJournal`, no new fields/hooks):

```csharp
    /// <summary>Derive the <c>versions/</c> append-journal path for a transaction and delete it through
    /// the fault-guarded durable-delete leaf. Pure lifecycle method: no recovery verification, no
    /// rollback bookkeeping.</summary>
    public void DeleteAppendJournal(AppendTransaction transaction)
    {
        string path = AppendJournalCodec.GetJournalPath(Path.Combine(_storeDirForScope(transaction.Scope), "versions"));
        TryDeleteAppendJournal(path, transaction.OperationId);
    }
```

### Store wiring (`FileMemoryStore.cs`)

1. **Delete** the three private forwarders (:3296–:3307).
2. **Repoint** all internal call sites (all within `FileMemoryStore.cs`; the forwarders are `private`, so no external/test owners):
   - `PersistAppendJournal(...)` → `_appendJournalWriter.PersistAppendJournal(...)` (~22 sites)
   - `DeleteAppendJournal(transaction)` → `_appendJournalWriter.DeleteAppendJournal(transaction)` (5 sites)
   - `TryDeleteAppendJournal(path, operationId)` → `_appendJournalWriter.TryDeleteAppendJournal(path, operationId)` (2 sites)

   Every repoint preserves argument order, names, and defaults (the owner methods carry the same signatures/defaults). Confirm via grep that no remaining `PersistAppendJournal(` / `DeleteAppendJournal(` / `TryDeleteAppendJournal(` bare call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — the owner uses these directly, unchanged), the `DurableWriter` durable-write leaves (P9–D16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AppendRollbackJournalReader` (P24), `AppendRecoveryBuilder` (P25), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`, `BuildDerivedEntry`). Do NOT re-edit `AppendJournalWriter`'s existing `PersistAppendJournal`/`TryDeleteAppendJournal` bodies — only ADD `DeleteAppendJournal`.

## Acceptance

- `AppendJournalWriter` owns the full append-journal lifecycle (`PersistAppendJournal`/`DeleteAppendJournal`/`TryDeleteAppendJournal`); `FileMemoryStore` retains no append-journal forwarders.
- No new public API; `IMemoryStore` surface unchanged.
- Zero logic moved — every repoint is a byte-identical pass-through to an existing owner method.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendJournalWriter|FullyQualifiedName~DeleteAppendJournal"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if the two-sided filter matches zero tests, run `--filter "FullyQualifiedName~AppendJournalWriter"` alone or the behavioral `--filter "FullyQualifiedName~Append"` instead.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
