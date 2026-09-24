# P1 Durability: `DurableFile` Directory-Fsync Design Spec (Approach A: Directory Fsync at the Atomic-Write Choke-Point)

> Status: proposed 2026-09-23. Approach A — fsync the destination directory at the end of `DurableFile.AtomicWrite`.

## Context

`DurableFile.AtomicWrite` (`src/Scrinia.Core/DurableFile.cs:62`) replaces one file through a flushed sibling temporary file:

- `File.Run` creates a temp file in the destination's directory (`CreateTemporaryPath`, `:104`) and writes the bytes through `WriteTemporaryFile` (`:111`), which flushes to disk (`stream.Flush(flushToDisk: true)`, `:124`).
- `File.Run` then `File.Move(temporaryPath, path, overwrite: true)` (`:93`), replacing the destination atomically.

The class self-documents the hole at lines 6–9:

```text
/// Replaces one file through a flushed sibling temporary file. The file contents are flushed, but
/// the containing directory is not: a power loss can retain the old file if the rename is lost.
```

The rename's *directory entry* is never made durable — no `FlushFileBuffers`/`fsync` of the destination directory happens anywhere. Consequence: on a crash between the `File.Move` and the (missing) directory sync, the OS may not have persisted the new directory entry, so a subsequent recovery can read the *old* file. Every durable leaf in the store funnels through this one method, so the hole is systemic though it lives in a single method:

- Append journal → `storeDir/versions/.append-journal.json` (`FileMemoryStore.PersistAppendJournal`, `AtomicWriteAllBytesForAppend`)
- Archive members → `versions/` (`WriteArchiveMember`)
- Live artifact/sidecar → `storeDir/` (`WriteArtifactAsync`, `UpsertForAppend` → `WriteSidecar`)
- Restore journal → `storeDir/.restore-journal.json`
- File-entity evidence → `entityDir/.file-entity-*.recovery`
- Import staging → `storeDir/*.staging`

No directory fsync exists in `src/` (`grep` for `fsync`, `FlushFileBuffers`, `Directory.Sync`, `OpenDirectory`, `CreateFile` returns zero matches). The fix closes the hole at the one choke-point so all callers inherit crash-consistency at once, rather than touching each of the ~6 call sites.

## Decision

**Approach A: directory fsync at the atomic-write choke-point.** After the `File.Move` in `AtomicWrite` succeeds, fsync the destination's containing directory so the rename entry is durable.

Alternatives rejected:
- **B — per-caller directory fsync:** touch each of the ~6 durable-leaf call sites instead of the shared choke point. More surface, higher risk, no added durability value.
- **C — best-effort (swallow all dir-sync failures):** silently degrades to the current hole for the cases that matter. Durability should not be a silent no-op.
- **D — hard-throw on every dir-sync failure:** fails writes when the directory is legitimately gone (a moved/deleted workspace). Approach A tolerates the not-found race instead.

### Append durability ordering (post-move phase interaction)

The append write path (`FileMemoryStore.PublishLiveAppendAsync`, `FileMemoryStore.cs:3360`) durably moves the live artifact into place (`WriteArtifactAsync`, line 3363) and the sidecar (`UpsertForAppend` → `WriteSidecarCore`, line 3365) **before** it writes the `Committed` recovery journal (`PersistAppendJournal`, line 3371). The `BeforeDirSync` phase therefore fires *after* the content has already been committed to the destination directory.

Consequence for fault tests: a dir-sync failure during the first durable move surfaces as `DurableWriteException`; the append's in-memory rollback (`TryRestoreAppendTransaction` → `RestoreAppendMember`, `FileMemoryStore.cs:3664`/`:3759`) then removes the partial members. For a **create** append the rollback is a plain `File.Delete` (no durable write, no dir-sync phase). For an **update** append the rollback rewrites the old bytes through a second `AtomicWriteAllBytesForAppend` (`RollbackWrite` fault point) — that second durable write ALSO carries the new `BeforeDirSync` phase. Therefore fault tests must use `B0FaultSeam.FailOnceAt(BeforeDirSync)`, not `FailPersistentlyAt`: a persistent fault would also fail the rollback's second write and the `RolledBack` journal write, degrading the append to `RecoveryRequired` instead of a clean rollback.

## Phased track context

- **P0 (done):** naming/casing single home (`MemoryNaming`). Merged to `main` at `596bcf00`. Prerequisite for all follow-ups.
- **P1 (this spec):** `DurableFile` directory fsync. Contained, single responsibility, closes the crash-consistency hole. No new public API, no JSON/disk-layout/wire changes.
- **P2:** write-path unification — kill the `appendOperationId` null-flag coupling in `StoreAsyncCore` (`FileMemoryStore.cs:2383`), split `StorePersistentAsync` vs `CreateForAppendAsync`, share only `BuildDerivedEntry`.
- **P3:** journal/transaction codec consolidation — unify the triplicated `AppendJournalCodec`/`RestoreJournalCodec`/`RecoveryEnvelopeCodec` + shared canonical-base64/length-cap helpers; merge `ArchivePlan` (`AppendTransaction.cs:44`) and `ArchivePairPlan` (`WholeMemoryTransaction.cs:64`).
- **P4:** god-store seam extraction — `DurabilityWriter` (the fix here), `ArchiveService` (versioning/pruning), `Admission`/`LockService`, `IndexCacheService`, `TransactionCoordinator` family.
- **P5:** maintenance + listing/search; **P6:** context + test-harness.

P1 changes no NMP/2, `.scrinia/` layout, CLI, MCP tool surface, or source-gen JSON shapes — it adds one durable-write phase and a directory sync. P2–P6 build on P1's crash-consistency and the existing fault seam.

## Frozen constraints (carry into P1)

- **NMP/2** artifact format unchanged (`NMP_SPEC.md`).
- **`.scrinia/` layout** unchanged (`memories/`, `skills/`, `agent/`, sidecars, index.json v3).
- **CLI** stays single-file surface (`src/Scrinia/Commands/ScriniaCommands.cs`, `--workspace-root` pattern; no remote mode).
- **MCP 12+11 disjoint:** 12 tools (`guide` + remember/recall/search/list/append/forget/compact/link/restore/facts) disjoint from the 11 CLI command forms.
- **Source-gen JSON** only (`ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`); no reflection serialization.
- **Recovery bypasses** `FileMemoryStore.DiscoverMemoriesRecursive` and `MemoryNaming.ParseScopeLabel` untouched.
- **No new public API** beyond interop; `DurableWriteException` shape unchanged.
- **Every new test** uses `TestHelpers.StoreScope` isolation (AsyncLocal workspace/store/ephemeral/`SessionBudget` redirect + restore of the five context cells).

---

## 1. Architecture + Components

The directory-fsync fix lives entirely in `DurableFile` (`src/Scrinia.Core/DurableFile.cs`) with a one-phase addition to the fault seam (`B0FaultSeam`). All other files are untouched.

| Component | Change |
|---|---|
| `DurableFile.AtomicWrite` (`:62-102`) | After the `File.Run(() => File.Move(...))` block succeeds, fsync the destination's containing directory via the new `SyncDirectory(destination)` helper. The temp file's own flush (`stream.Flush(flushToDisk: true)`, `:124`) is unchanged and still provides content durability; the added directory sync provides *entry* durability. |
| `DurableFile.SyncDirectory` (new) | `private static void SyncDirectory(string directory)`. Resolves the directory for the destination (`Path.GetDirectoryName(destination)`, falling back to `.` for a rootless name — same rule as `CreateTemporaryPath`, `:106`), then fsyncs it through `FileRetry.Run` (consistent with the move/temp retry policy). |
| `DurableFile.SyncDirectory` interop (new, Windows) | `CreateFile(dir, FILE_FLAG_BACKUP_SEMANTICS | GENERIC_WRITE)` → `FlushFileBuffers(handle)` → `CloseHandle`. `FILE_FLAG_BACKUP_SEMANTICS` is required to open a directory handle on Windows; `GENERIC_WRITE` is required because `FlushFileBuffers` returns Win32 error 5 (access denied) on a read-only directory handle — a directory handle needs write access to flush its entries. |
| `DurableFile.SyncDirectory` interop (new, POSIX) | `open(dir, O_RDONLY | O_DIRECTORY)` → `fsync(fd)` → `close`. `O_DIRECTORY` guards that the handle is a directory; `fsync` on a directory fd flushes its entries. |
| `DurableFile.SyncDirectory` race (new) | If `CreateFile`/`open` fails with not-found (the directory was deleted between the caller's `Exists` check and this call), swallow and treat the sync as satisfied — the file move already succeeded, there is nothing to persist. Any other `IOException` propagates; `AtomicWrite`'s existing catch (`:96-101`) wraps it in `DurableWriteException`. |
| `DurableFile.SyncDirectory` fault seam (new) | Before performing the directory fsync, call the existing `ThrowInjected(DurableWritePhase.BeforeDirSync, directory)` helper (`DurableFile.cs:127`). This wires the new phase into BOTH the unified `B0FaultSeam` and the `FailureInjector` AsyncLocal seam — the same helper the write/flush/move phases already use — so a test can inject a failure at the dir-sync step and exercise the identical `DurableWriteException` → append-rollback path a real power-loss/disk failure would. |
| `DurableWritePhase` enum (`:150`) | Add `BeforeDirSync`. |
| `B0FaultSeam.MapDurableWritePhase` (`:120`) | Add `DurableWritePhase.BeforeDirSync => FaultPhase.BeforeDirSync`. |
| `B0FaultSeam.FaultPhase` enum (`:209`) | Add `BeforeDirSync`. |
| `DurableFile` interop pragma | `#pragma warning disable CA1838, CA2101, CA1865` around the interop, matching `RecoveryHandleIdentityVerifier` (`src/Scrinia.Core/RecoveryHandleIdentity.cs:7`). |

### Platform dispatch

Follow the exact convention already in `src/Scrinia.Core/RecoveryHandleIdentity.cs` (`:49`):

```csharp
return OperatingSystem.IsWindows() ? <windows> : <posix>;
```

`src/Scrinia.Core/Scrinia.Core.csproj` targets `net10.0`; the interop is gated at runtime, not compile time, so the same binary runs on Windows, Linux, and macOS.

---

## 2. Data flow

### Write (`remember` / `append` / `StoreAsync`) — the only path touched

1. `AtomicWrite` creates the temp file in the destination directory and flushes it (`WriteTemporaryFile`, `:111-125`).
2. `AtomicWrite` moves the temp over the destination (`File.Move`, `:93`).
3. **NEW:** `AtomicWrite` fsyncs the destination's containing directory (`SyncDirectory`, after the move block). The rename entry is now durable.
4. `AtomicWrite` returns; the caller (`PersistAppendJournal`, `WriteArtifactAsync`, `WriteSidecar`, etc.) proceeds.

The fault phase `BeforeDirSync` fires between step 2 and step 3, so a test-injected failure at the new seam exercises the exact same `DurableWriteException` → append-rollback path the production failure would.

### Recovery — unchanged

Recovery reads already-committed entries (journal, snapshot, sidecar). It does not perform durable single-file replacements, so it does not fsync directories. The `DiscoverMemoriesRecursive` / `ParseScopeLabel` recovery bypasses stay outside this change.

---

## 3. Error handling

- **Not-found race (directory gone):** `CreateFile`/`open` fails with not-found → swallow, treat sync as satisfied. The file move already succeeded, so the write is durable; the missing directory has nothing to update. This avoids failing a write when the workspace was legitimately moved or deleted.
- **All other `IOException`:** propagate to `AtomicWrite`'s existing catch (`:96-101`), which wraps it in `DurableWriteException(operation, path, exception)`. The `AppendFaultInjector` / `FailureInjector` fault path already treats `DurableWriteException` as a rollback trigger, so the append rolls back (no leftover temp, journal state consistent) exactly as it does for any other durable-write failure.
- **Retry:** `SyncDirectory` runs inside `FileRetry.Run`, which retries transient `IOException` (50/100/200/400/800ms, ~1.5s total) and surfaces permanent ones immediately (`IsPermanentIoFailure`: `FileNotFoundException`, `DirectoryNotFoundException`, `PathTooLongException`). A permanently-broken directory (e.g. `PathTooLongException`) is surfaced, not swallowed.

---

## 4. Testing + rollout

### Matrix

| Test | Pins |
|---|---|
| **Positive — dir is fsynced on every durable write:** install `DurableFile.FailureInjector` = `(phase, _) => { phases.Add(phase); return null; }` (a recorder), perform an append **create** of a new memory, then assert `phases` contains `DurableWritePhase.BeforeDirSync`. Proves the destination directory is fsynced on the durable writes inside a real append (`WriteArtifactAsync`, `WriteSidecarCore`, `PersistAppendJournal`). | New phase reachable through the existing `FailureInjector` seam; `ThrowInjected` fires it after the move. |
| **Fault/rollback (append):** install `DurableFile.FailureInjector` = `(phase, _) => phase == DurableWritePhase.BeforeDirSync && dirSyncFailures++ == 0 ? new IOException("Injected dir-sync failure.") : null` (fail the first post-move dir-sync only) before an append **create** of a brand-new memory. The first durable move succeeds and its dir-sync fails → `PublishLiveAppendAsync` throws `DurableWriteException`; the append catch (`FileMemoryStore.cs:3057-3105`) rolls back the partial members via `TryRestoreAppendTransaction` → `RestoreAppendMember` (create → `File.Delete`), rewrites nothing, and the second durable write (the `RolledBack` journal) is not faulted. Assert the append throws `DurableWriteException`, the new artifact path and sidecar path both no longer exist, and no recovery journal is left behind. Use the first-call-fails closure (not a persistent injector): a persistent injector would also fail the `RolledBack` journal write and degrade the append to `RecoveryRequired`. See the ordering note under 'Decision'. | Parity with `AppendRollbackTests.AppendAsync_CreateFailureRemovesOnlyItsPartialMembers` (partial members removed by the in-memory rollback); post-move failure semantics per the ordering note. |
| **Not-found tolerance:** a nonexistent/removed destination directory does not fail an otherwise-successful write (temp flush + move complete, dir-sync swallow). | The race is tolerated; durability is not degraded for the common case. |
| **Regression pins (must stay green):** `AppendDurabilityTests`, `AppendRollbackTests`, `AppendRecoveryTests`, `AtomicConfigWriteTests`, `ConfigCommandDurabilityTests`. | The fix changes only the post-move step; pre-move semantics are identical. |

### Rollout (mechanical, no JSON/disk-shape changes)

1. **`SyncDirectory`:** add the method to `DurableFile`. Before the directory fsync, invoke `ThrowInjected(DurableWritePhase.BeforeDirSync, directory)` (the existing helper at `:127`, shared with the write/flush/move phases) so the new phase is reachable through both the `B0FaultSeam` and the `FailureInjector` seam. Then add the Windows (`kernel32`) + POSIX (`libc`) interop, `OperatingSystem.IsWindows()` dispatch, and the `#pragma warning disable` block — mirroring `RecoveryHandleIdentityVerifier`.
2. **Race + retry:** wrap the interop call in `FileRetry.Run`; swallow not-found; let other `IOException` propagate to `AtomicWrite`'s catch.
3. **Call `SyncDirectory`** in `AtomicWrite` after the `File.Run(() => File.Move(...))` block.
4. **Fault seam:** add `BeforeDirSync` to `DurableWritePhase` and `FaultPhase`, and the one-case `MapDurableWritePhase` switch arm.
5. **Tests:** add `DurableFileDirectorySyncTests.cs` with two deterministic tests — a positive test (recorder `FailureInjector` asserts `DurableWritePhase.BeforeDirSync` appears on a durable append write) and a fault/rollback test (first-call-fails `FailureInjector` asserts the append throws `DurableWriteException`, removes the partial create members, and leaves no recovery journal; the `RolledBack` journal write is not re-faulted). Each uses `TestHelpers.StoreScope` isolation. The directory-vanished race tolerance is defensive (a narrow post-move window, not deterministically reproducible in CI) and is verified by code review.
6. **Verify:** `dotnet build Scrinia.sln` green, then `dotnet test Scrinia.sln` green.

## Out of scope

- Touching any of the ~6 durable-leaf call sites directly — they inherit the fix through `AtomicWrite`.
- Other god-store seams (write-path unification, codec consolidation, admission/lock/index-cache extraction).
- New public API beyond the fault-seam phase.
- NMP/2, `.scrinia/` layout, CLI, MCP tool surface, or source-gen JSON changes.
- `DiscoverMemoriesRecursive` / `ParseScopeLabel` recovery bypasses.
