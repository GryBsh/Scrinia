# B2 Task 9 — Final Compatibility and Acceptance Gate

## Status

**REQUEST_CHANGES**. The B2-focused acceptance lane and serial solution build pass, but the ordinary
full solution test run has one compatibility regression in an existing write-surface test.

The exact existing `.superpowers/sdd/task-9-brief.md` is not the B2 brief: it describes the earlier
TransientDetector task. This report uses the B2 Task 9 section in
`docs/superpowers/plans/2026-09-11-journal-convergence.md` and the T9 ledger row as authoritative.

## Scoped fix

- `src/Scrinia.Core/FileMemoryStore.cs`: committed append cleanup evidence is readable without an
  implicit cleanup retry from the read barrier. Cleanup remains available through the explicit
  recovery path, preserving retained evidence and the post-commit warning contract.

## Verification evidence

Commands were run serially with `-m:1` where applicable.

| Command | Exit | Elapsed | Result |
|---|---:|---:|---|
| `dotnet clean Scrinia.sln -m:1` | 0 | 2 s | Clean completed. |
| `dotnet build Scrinia.sln --no-restore -m:1` | 0 | 12 s | 0 errors, 3024 warnings (existing analyzer/preview-SDK warnings). |
| `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Recovery\|FullyQualifiedName~Append\|FullyQualifiedName~JournaledRestoreRecoveryTests\|FullyQualifiedName~FileEntityTransactionTests\|FullyQualifiedName~McpRecoveryBlockedRouteTests\|FullyQualifiedName~CliRecoveryBlockedRouteTests\|FullyQualifiedName~Bundle\|FullyQualifiedName~Nmp2\|FullyQualifiedName~Sidecar\|FullyQualifiedName~Archive\|FullyQualifiedName~Lock\|FullyQualifiedName~Cache\|FullyQualifiedName~Fact\|FullyQualifiedName~Legacy\|FullyQualifiedName~Trim\|FullyQualifiedName~SourceGeneration\|FullyQualifiedName~RegisteredFormats'` | 0 | 403 s | 1111 passed, 0 failed, 0 skipped, total 1111. |
| `dotnet test Scrinia.sln --no-restore -m:1` | 1 | 635 s | 3055 passed, 1 failed, 19 skipped, total 3075. No hang; all solution projects completed. |
| `git diff --check 4dd315e..HEAD` | 0 | <1 s | No whitespace errors in the committed B2 range. |

The full-suite failure is:

- `Scrinia.Tests.FileMemoryStoreWriteSurfaceTests.StoreAsync_RestoresThePreviousContentWhenPersistingTheEntryFails`
  at `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs:329`. A failed whole-memory
  publication leaves append recovery evidence in an unsafe state; the B2 read barrier correctly
  blocks the read with `AppendStoreException` rather than returning potentially inconsistent data.
  This is outside the B2 Task 9 file list and conflicts with the existing test's pre-B2 expectation.
  No broad fail-open change was made.

The B2-focused lane covers append, restore, file-entity, mixed versions, discovery, MCP/CLI,
bundle/import, NMP/2, sidecars, archives, locks, cache/facts, trimming/source-generation checks,
and legacy fixtures. It completed with 1111/1111 passing and no test-created evidence left in the
fixture workspace.

## Acceptance matrix

1. Envelope/body and legacy round trips — passing focused recovery/envelope/legacy tests.
2. Shuffled discovery — passing discovery tests.
3. Restore-before-append-before-file-entity — passing discovery tests.
4. Qualified scope isolation — passing malformed/unsafe/newer/ambiguous contamination tests.
5. Blocked preflight mutation and byte retention — passing focused recovery tests.
6. Commit, failed cleanup, explicit retry — passing append/restore/file-entity retry tests.
7. Partial rollback and cache invalidation — passing operation and cache lanes.
8. MCP/CLI blocked/required/warning envelopes — passing facade and route tests.
9. Source-generation/trimming-safe serialization — passing serialization/format/build checks.
10. B1 contamination ruling — passing focused recovery and scope tests.

The matrix is not a clean compatibility gate until the ordinary full solution test failure is
resolved or explicitly reclassified by the owner of the pre-B2 write-surface contract.

## Scope audit

The committed B2 range `4dd315e..HEAD` contains the planned recovery implementation and tests, but
also contains these paths outside the B2 Task 1–9 file responsibility map:

- `task-3-report.md`
- `src/Scrinia.Core/AppendContracts.cs`
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs`
- `tests/Scrinia.Tests/Formats/FormatVersionTests.cs`
- `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs`
- `tests/Scrinia.Tests/RecoveryBlockedFailureClassCoverageTests.cs`
- `tests/Scrinia.Tests/TestHelpers.cs`

The plan and B2 design specification are the expected B2 planning artifacts. Existing unrelated
worktree changes (`task-3-report.md`, `.agents/`, `.codex/`, and other task reports) were not
absorbed into this Task 9 fix. The exact historical SDD Task 9 brief/report remain unchanged.

## Parked findings

The three Task 1 deferred minors remain parked:

1. Missing append paths in baseline fixtures.
2. Restore body/sidecar paths in baseline fixtures.
3. A production-faithful committed file-entity fixture.

Additional parked finding: Task 3 platform-specific filesystem reparse-point fixture coverage.
The preview SDK warning (`NETSDK1057`) and pre-existing analyzer warnings are non-blocking.

## Commit

The scoped read-barrier fix is pending the orchestrator's review. No unrelated worktree changes are
staged.
