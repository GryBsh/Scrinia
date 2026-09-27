# P30 — Append-Commit Prune Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P29 complete; append recovery-orchestration phase: P24 `AppendRollbackJournalReader`, P25 `AppendRecoveryBuilder`, P26 `AppendJournalWriter` forwarder consolidation, P27 `AppendRollbackExecutor`, P28 `AppendMemberVerifier`, P29 `AppendArchiveReplayer`).

**Phase:** P30 (thirtieth seam extraction; append recovery-orchestration phase, seventh sub-seam).
**Goal:** hoist the shared durable-write prune conductor `PruneCommittedArchives` (+ its private `ArchiveCleanupResult` record) off the store into the durable-write owner `ArchiveService`, the first cut into the top-level conductor `RecoverAppendUnsafeAsync`.

## Why this phase

`RecoverAppendUnsafeAsync` (`FileMemoryStore.cs` :3086–:3204) is the top-level recovery state-machine. It cannot be moved wholesale in a single phase; several of the leaves it drives are **shared** with the store's write paths and so cannot be orphaned into a recovery owner. The prune leaf `PruneCommittedArchives` is shared between the **write** path (`AppendAsyncCore` :2854) and the **recovery** path (`RecoverAppendUnsafeAsync` :3164), and it is the first shared durable-write conductor to disentangle.

### `PruneCommittedArchives` (:2992–:3048) — shared durable-write prune conductor

Returns an `ArchiveCleanupResult` describing post-prune failure (or cancellation). Dependencies:

- `AppendFaultPoint` — frozen enum.
- `InjectAppendFault` — the store's **static** fault seam (:97). `ArchiveService` gains a new `_injectAppendFault` hook wired to it (same shape as `AppendRollbackExecutor`/`AppendJournalWriter`).
- `PathContainment.IsWithin` — frozen static.
- `AppendTransaction.PruneCandidates` / `.OperationId` / `.Scope` / `.Archive.ArtifactArchivePath` / `.NewArtifact.Path` / `.NewSidecar.Path` — frozen `AppendTransaction`/`ArchivePlan` members.
- `ArchiveCleanupResult` — a **private record** at :2985, referenced only by `PruneCommittedArchives` (4 references: :2985, :2996, :3001, :3047). It moves WITH `PruneCommittedArchives` into `ArchiveService`.
- `File.Delete` — system.

No `FileMemoryStore` instance state. The only seam `ArchiveService` must gain is `_injectAppendFault`.

### `ArchiveCleanupResult` (:2985–:2990) — private record

`private sealed record ArchiveCleanupResult(bool Failed, string[] Failures)` with a `Detail` computed property. Move it verbatim into `ArchiveService` (the only owner of `PruneCommittedArchives`).

## The seam

### `ArchiveService` changes (`src/Scrinia.Core/ArchiveService.cs`)

1. **Add** the `_injectAppendFault` field + constructor param:
   ```csharp
   private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;
   ```
   Constructor gains `Func<AppendFaultPoint, string, string, Exception?> injectAppendFault` and assigns it. The existing four hooks (`_artifactPath`, `_storeDirForScope`, `_sanitizeName`, `_metaPath`) are unchanged; this is the fifth.

2. **Move** `ArchiveCleanupResult` (verbatim, :2985–:2990) into `ArchiveService.cs`, placed after the closing brace of `ArchiveService` OR as a nested record inside `ArchiveService` (author's choice; prefer a **top-level** record in the same file, following the file's `using`/`namespace` layout, to keep it out of the store's private-record namespace).

3. **Move** `PruneCommittedArchives` (verbatim, :2992–:3048) into `ArchiveService` as a **public instance method** `public ArchiveCleanupResult PruneCommittedArchives(...)`. Replace both `InjectAppendFault(...)` call sites with `_injectAppendFault(...)`.

The moved body is otherwise byte-identical to the store's verbatim method.

### Store wiring (`FileMemoryStore.cs`)

1. **Update** the `ArchiveService` construction to pass the frozen static `InjectAppendFault` seam. Locate the `_archiveService = new ArchiveService(...)` construction and append the new `injectAppendFault` argument in the correct positional slot (after the four existing hooks). The store's static `InjectAppendFault` (:97) is the value passed.

2. **Delete** `PruneCommittedArchives` (:2992–:3048) and `ArchiveCleanupResult` (:2985–:2990) from the store.

3. **Repoint** the three call sites to `_archiveService.PruneCommittedArchives(...)`:
   - `AppendAsyncCore` :2854 `PruneCommittedArchives(transaction, ct)` → `_archiveService.PruneCommittedArchives(transaction, ct)`
   - `PruneCommittedArchivesForTest` :2929 `PruneCommittedArchives(transaction, CancellationToken.None).Failures` → `_archiveService.PruneCommittedArchives(transaction, CancellationToken.None).Failures`
   - `RecoverAppendUnsafeAsync` :3164 `PruneCommittedArchives(transaction, ct)` → `_archiveService.PruneCommittedArchives(transaction, ct)`

Confirm via grep that no remaining bare `PruneCommittedArchives(` call site is left in the store after the edit.

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`/`RecoveryEnvelopeCodec`/`AppendJournalCodec`/`AppendRecoveryAdapter` (all frozen), the `DurableWriter` durable-write leaves (P9–P16), `ArchiveService`'s existing four hooks and `ArchiveVersionUnsafe` (P17 — do NOT alter `ArchiveVersionUnsafe`, only add the `_injectAppendFault` hook + move `PruneCommittedArchives`), `ArtifactIdentityMapper` (P18), `AppendJournalWriter` (P19/P20/P26), `AppendArchiveReplayer` (P29), `AppendRollbackExecutor` (P27), `AppendMemberVerifier` (P28), the P21/P22 verification predicates (`MatchesAppendMember`/`MatchesArchiveBytes`/`MatchesAppendState`/`HasCommittedHash`) in `AppendRecoveryVerifier`, `AppendRecoveryBuilder` (P25), `AppendRollbackJournalReader` (P24), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), and the recovery-orchestration methods (`RecoverAppendUnsafeAsync`, `PersistRecoveryRequiredOrThrow`, `AreAppendMembersCurrent`, `PublishFrozenArchiveAsync`, `PublishLiveAppendAsync`).

## Acceptance

- `ArchiveService` owns the durable prune conductor (`PruneCommittedArchives`) + `ArchiveCleanupResult`; `FileMemoryStore` retains only the repointed call sites within `AppendAsyncCore`, `PruneCommittedArchivesForTest`, and `RecoverAppendUnsafeAsync`.
- No new public API on the `IMemoryStore` surface; `ArchiveService`'s existing public surface (`ArchiveVersionUnsafe`) unchanged, `PruneCommittedArchives` added as a new public method.
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
