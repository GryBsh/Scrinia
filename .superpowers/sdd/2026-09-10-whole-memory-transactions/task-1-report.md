# Task 1 Implementation Report

## Files

- `src/Scrinia.Core/WholeMemoryTransaction.cs`
  - Added the B1 whole-memory transaction/result contracts, archive-pair plan, recovery metadata, fault-point enums, file-entity mutation contract, and internal coordinator interfaces.
  - Added boundary guards for required text, operation IDs, recovery evidence, and required archive destinations.
  - Preserved `RawMemberSnapshot` as the B0 defensive-copy and SHA-256 validation contract.
- `src/Scrinia.Core/FileMemoryStore.cs`
  - Added the AsyncLocal one-shot whole-memory fault seam and cleared it alongside existing append faults.
- `tests/Scrinia.Tests/WholeMemoryTransactionTests.cs`
  - Added the three Task 1 contract tests from the brief.

No MCP or Core JSON source-generation context needed a registration: Task 1 contracts are internal coordination types and are not serialized yet. Later DTO registrations remain owned by Task 7.

## Decisions

- Reused the existing `RawMemberSnapshot` implementation rather than duplicating byte/hash logic.
- Kept all new transaction contracts internal, matching the accepted B0 adapter boundary and preserving the Core result contract as the sole transaction authority.
- Defensive-copied `FileEntityMutation` byte arrays and archive prune candidates at construction.
- Required recovery evidence for `RecoveryRequired` results/errors and operation IDs for all durable results/errors.
- Did not add a concrete file-entity coordinator implementation; that publication behavior remains assigned to Task 4.

## Tests and commands

- `bash .../subagent-driven-development/scripts/task-brief docs/superpowers/plans/2026-09-10-whole-memory-transactions.md 1`
  - PASS; generated `.superpowers/sdd/2026-09-10-whole-memory-transactions/task-1-brief.md`.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryTransactionTests"`
  - PASS: 3 passed, 0 failed, 0 skipped.
- `rtk dotnet build Scrinia.sln --no-restore`
  - PASS: 13 projects, 0 errors; 344 pre-existing warnings.
- `dotnet format Scrinia.sln --verify-no-changes`
  - FAIL against the existing repository baseline: 269 files require formatting, including unrelated files.
- `dotnet format Scrinia.sln --verify-no-changes --include src/Scrinia.Core/WholeMemoryTransaction.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/WholeMemoryTransactionTests.cs`
  - Reports existing unrelated whitespace violations in `FileMemoryStore.cs`; no violation was reported for the new contract/test file or the changed top-of-file seam lines.
- `rtk git diff --check`
  - PASS for the working tree diff.

## Concerns

- The worktree contains substantial pre-existing unrelated modifications and untracked reports/configuration files. They were not staged or changed by this task.
- Repository-wide format verification is currently baseline-red; this task did not reformat unrelated files.
