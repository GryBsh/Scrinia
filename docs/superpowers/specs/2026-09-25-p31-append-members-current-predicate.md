# P31 — Append-Members-Current Predicate Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P30 complete; append recovery-orchestration phase: P24 `AppendRollbackJournalReader`, P25 `AppendRecoveryBuilder`, P26 `AppendJournalWriter` forwarder consolidation, P27 `AppendRollbackExecutor`, P28 `AppendMemberVerifier`, P29 `AppendArchiveReplayer`, P30 `ArchiveService` prune conductor).

**Phase:** P31 (thirty-first seam extraction; append recovery-orchestration phase, eighth sub-seam).
**Goal:** move the static recovery predicate `AreAppendMembersCurrent` off the store into the existing recovery-verification owner `AppendRecoveryVerifier` — the cleanest resident leaf in the top-level conductor `RecoverAppendUnsafeAsync`.

## Why this phase

`RecoverAppendUnsafeAsync` (`FileMemoryStore.cs` :3022–:3140) is the top-level recovery state-machine. Its two resident methods are:

- `PersistRecoveryRequiredOrThrow` (:3143) — a durable-write enforcement conductor that holds the `_appendJournalWriter` seam-owner and always throws. It is a genuine conductor (not a leaf); it is the next sub-seam (P32), not this one.
- `AreAppendMembersCurrent` (:3159) — a pure static predicate: the boolean AND of two `AppendRecoveryVerifier.MatchesAppendMember` calls. Zero store-instance state, zero seam-owner reference, zero durable write.

`AreAppendMembersCurrent` is the cleanest leaf to extract now. Its sole dependency is `MatchesAppendMember`, which already lives in `AppendRecoveryVerifier` (P21/P22), the recovery-verification owner. Moving it there co-locates the predicate with its verification primitives and removes one resident method from the conductor — no new class, no new hook, no fault seam to relocate.

### `AreAppendMembersCurrent` (:3159–:3160) — pure static predicate

```csharp
private static bool AreAppendMembersCurrent(AppendTransaction transaction, CancellationToken cancellationToken = default) =>
    AppendRecoveryVerifier.MatchesAppendMember(transaction.NewArtifact, cancellationToken) &&
    AppendRecoveryVerifier.MatchesAppendMember(transaction.NewSidecar, cancellationToken);
```

Dependencies:

- `AppendRecoveryVerifier.MatchesAppendMember` — frozen static (owner `AppendRecoveryVerifier`, P21/P22). The `cancellationToken` is forwarded to it; `MatchesAppendMember` uses it for `RecoveryEvidenceVerifier.VerifySingleAsync`. **Keep the `cancellationToken` parameter** — dropping it would alter the token's flow to the verifier and change behavior.
- `AppendTransaction.NewArtifact` / `NewSidecar` — frozen DTO properties.
- `CancellationToken` — system.

No `FileMemoryStore` instance state. No fault seam.

## The seam

### `AppendRecoveryVerifier` changes (`src/Scrinia.Core/AppendRecoveryVerifier.cs`)

Add a new public static method to the existing `AppendRecoveryVerifier` class (after `MatchesAppendState`, before `HasCommittedHash`):

```csharp
    /// <summary>Prove that a recovered append's new artifact + sidecar members are both present and
    /// verified. Pure predicate: no fault injection, no durable write, no rollback bookkeeping.</summary>
    public static bool AreAppendMembersCurrent(AppendTransaction transaction, CancellationToken cancellationToken = default) =>
        MatchesAppendMember(transaction.NewArtifact, cancellationToken) &&
        MatchesAppendMember(transaction.NewSidecar, cancellationToken);
```

The body is verbatim except the `AppendRecoveryVerifier.` qualifier is dropped (the method now lives in the same class as `MatchesAppendMember`).

### Store changes (`src/Scrinia.Core/FileMemoryStore.cs`)

1. **Delete** `AreAppendMembersCurrent` (:3159–:3160) from the store.
2. **Repoint** the two call sites inside `RecoverAppendUnsafeAsync`:
   - :3084 `AreAppendMembersCurrent(transaction, ct)` → `AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct)`
   - :3093 `AreAppendMembersCurrent(transaction, ct)` → `AppendRecoveryVerifier.AreAppendMembersCurrent(transaction, ct)`

Confirm via grep that no remaining bare `AreAppendMembersCurrent(` call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService` (P17), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26), `AppendArchiveReplayer` (P29), `AppendRollbackExecutor` (P27), `AppendMemberVerifier` (P28), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier` — do NOT alter these members' bodies, only add `AreAppendMembersCurrent`; `AppendRecoveryBuilder` (P25), `AppendRollbackJournalReader` (P24), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `PersistRecoveryRequiredOrThrow`, `PruneCommittedArchives`, `PublishFrozenArchiveAsync`, `PublishLiveAppendAsync`).

## Acceptance

- `AppendRecoveryVerifier` owns `AreAppendMembersCurrent`; `FileMemoryStore` retains only the two repointed call sites within `RecoverAppendUnsafeAsync`.
- No new public API on the `IMemoryStore` surface; `AppendRecoveryVerifier`'s existing public surface unchanged except the added static method.
- No `FileMemoryStore` instance state referenced by the moved predicate.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"`.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
