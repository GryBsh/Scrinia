# Task 4 Report — transaction rollback and file-entity publication

## Status

Complete. Whole-memory rollback now invalidates all in-process views after restoring files, and
file-entity publication has a coordinator that treats the body and metadata as one recoverable pair.
Recovery evidence now distinguishes prepared transactions from committed publications, validates
all deserialized paths before mutation, and is wired through the production coordinator seams.

## Changes

- `src/Scrinia.Core/FileMemoryStore.cs`: added scope and entity-fact cache invalidation after an
  out-of-band rollback.
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs`: invalidates the store cache only after
  the original pair and operation-owned archive members have been restored successfully.
- `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`: added path-contained, locked publication
  of file-entity body and metadata members with archive, verification, rollback, committed-state
  recovery evidence, retry cleanup, and traversal rejection.
- `src/Scrinia.Core/FileMemoryStore.cs`: production paths now hold the concrete coordinators but
  invoke them through `IWholeMemoryTransactionCoordinator` and `IFileEntityTransactionCoordinator`
  boundaries; file-entity callers use `ExecuteFileEntityTransactionAsync`.
- `tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs`: added regression coverage proving that
  the same store instance returns the restored pair through read, list, and search views after a
  verification failure.
- `tests/Scrinia.Tests/FileEntityTransactionTests.cs`: added committed-evidence preservation and
  recovery-path traversal regressions.

## Verification

- Focused transaction tests: **47 passed, 0 failed, 0 skipped**.
- Solution build: **13 projects, 0 errors**; existing analyzer warnings remain outside this
  correction.
- Path-limited `git diff --check`: passed.

## Scope

Only the transaction rollback consistency fix, file-entity coordinator, focused regression tests, and
this corrected report belong to Task 4. Other worktree changes remain unstaged. No commit was created
by this coding pass; the orchestrator must stage only the scoped Task 4 files.
