# P24 — Append-Rollback-Journal-Reader Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P23 complete). The append recovery-orchestration region (`FileMemoryStore.cs` :3129–:3530) is now being de-godded in a multi-phase recovery-orchestration phase. Recovery-orchestration methods are entangled with fault injection (`InjectAppendFault`) and durable writes, so the phase proceeds from the cleanest leaf upward.

**Phase:** P24 (twenty-fourth seam extraction; recovery-orchestration phase, first sub-seam).
**Goal:** move the pure journal→transaction reconstruction (`TryReadAppendTransactionForRollback`) off the store into a new self-contained owner, leaving the fault-injecting rollback orchestration resident for a later phase.

## Why this phase

`TryReadAppendTransactionForRollback` (`FileMemoryStore.cs` :3348–:3373) reconstructs an `AppendTransaction` from a verified append-recovery journal so a cancellation/pre-commit rollback can compensate. It is the cleanest leaf in the recovery-orchestration region:

- **Static, no instance state** — takes only `(string journalPath, string scope)`.
- **No fault injection** — it never calls `InjectAppendFault`; it deliberately uses `CancellationToken.None` ("Compensation is deliberately non-cancellable so rollback can establish a safe state").
- **No durable write** — it only *reads* and verifies the journal, then reconstructs an in-memory transaction.
- **Single call site** — `PublishFrozenArchiveAsync`'s cancellation-rollback path (`FileMemoryStore.cs` :2730).
- **Self-contained** — every dependency is a frozen primitive already used by the P21/P22 `AppendRecoveryVerifier` owner: `AppendRecoveryAdapter`, `RecoveryEvidenceVerifier.VerifySingleAsync`, `RecoveryScopeBinding`, `RecoveryEvidenceKind`, `MaintenanceLimits.Hard`, `RecoveryVerificationOutcome`, `RecoveryVerificationContent`, `RecoveryVerifiedFileMetadata`, `RecoveryEnvelopeCodec.ParseVerifiedEnvelope`, `RecoveryVerifiedEnvelope`, `RecoveryEnvelopeDocument`, `IRecoveryAdapter`, `AppendTransaction.ToTransaction()`.

The surrounding recovery-orchestration (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`) remains resident — it is genuinely entangled with `InjectAppendFault` durable-inject + `DurableFile` writes + rollback bookkeeping, and will be a larger, later phase. `RecoveryRequired` (:3442, frozen exception builder) and `AreAppendMembersCurrent` (:3325, thin combiner) are low-value churn already extracted or not worth extracting.

## The seam

Move `TryReadAppendTransactionForRollback` (:3348–:3373) verbatim into a new owner. The body is unchanged (no `FileMemoryStore` instance state).

### New owner: `AppendRollbackJournalReader` (`src/Scrinia.Core/AppendRollbackJournalReader.cs`)

Create `src/Scrinia.Core/AppendRollbackJournalReader.cs` with the verbatim static owner method from the store (the full `TryReadAppendTransactionForRollback` body).

### Store wiring (`FileMemoryStore.cs`)

Two edits:

1. **Replace** the `TryReadAppendTransactionForRollback` body (:3348–:3373) with a one-line forwarder:
   ```csharp
   private static AppendTransaction? TryReadAppendTransactionForRollback(string journalPath, string scope) =>
       AppendRollbackJournalReader.TryReadAppendTransactionForRollback(journalPath, scope);
   ```
2. **Repoint** the single call site (:2730) — no change needed here since the forwarder keeps the same name; the call site `TryReadAppendTransactionForRollback(journalPath, scope)` continues to resolve to the store's forwarder.

Confirm via grep that the only call site is :2730 and that no residual recovery-journal reconstruction logic remains in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen — the owner uses these directly, unchanged), the `DurableWriter` durable-write leaves (P9–D16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `ReplayArchiveMembers`, `PersistRecoveryRequiredOrThrow`, `VerifyAppendMemberAsync`, `AreAppendMembersCurrent`, `TryRestoreAppendTransaction`, `TryRemoveCreatedArchiveMembers`, `ReconcileArchiveMember`, `RestoreAppendMember`, `RecoveryRequired`, `DeleteAppendJournal`, `PruneCommittedArchives`).

## Acceptance

- `AppendRollbackJournalReader` owns the journal→transaction reconstruction; `FileMemoryStore` retains only the one-line forwarder and the fault-injecting rollback orchestration.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendRollbackJournalReader|FullyQualifiedName~TryReadAppendTransactionForRollback"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
