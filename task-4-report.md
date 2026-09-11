# Task 4 Report — transaction rollback and file-entity publication

## Status

Complete. Whole-memory rollback now invalidates all in-process views after restoring files, and
file-entity publication has a coordinator that treats the body and metadata as one recoverable pair.

## Changes

- `src/Scrinia.Core/FileMemoryStore.cs`: added scope and entity-fact cache invalidation after an
  out-of-band rollback.
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs`: invalidates the store cache only after
  the original pair and operation-owned archive members have been restored successfully.
- `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`: added path-contained, locked publication
  of file-entity body and metadata members with archive, verification, rollback, and recovery evidence.
- `tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs`: added regression coverage proving that
  the same store instance returns the restored pair through read, list, and search views after a
  verification failure.

## Verification

- Path-limited `git diff --check`: passed.

## Scope

Only the transaction rollback consistency fix, file-entity coordinator, focused regression test, and
this corrected report belong to Task 4. Other worktree changes remain unstaged.
