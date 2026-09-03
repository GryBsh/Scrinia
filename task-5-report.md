# Task 5 Report — Packaging and Active Documentation Merge Retirement

## Status

Implemented Task 5 only in worktree `merge-retirement`.

- BASE: `5b3073bb6a1ee34b2b08f5e75044f6b6f7bc1772`
- Scope: active publish manifest, release workflow/scripts, and Phase 1-owned living documentation.
- Explicitly untouched: `docs/multi-user-setup.md`, dated planning/review artifacts, and later Phase 5 relocation work.

## Changes

- Removed `scri-merge` from the publish manifest and release archive/test surfaces.
- Removed retired merge-driver comments from `publish.ps1` and `publish.sh`.
- Removed the active `merge-safety` skill.
- Removed active `reconcile`, `--multi-user`, merge-driver, and multi-user references from `cli-reference.md` and `getting-started.md`.
- Added the manual-only conflict cleanup section to `troubleshooting.md`.
- Added packaging/documentation retirement and historical-byte-preservation tests.
- Updated the existing release workflow test to assert the retired executable is absent.

## TDD evidence

The new focused test was run before implementation and failed for the expected missing-retirement reason: the workflow still contained `scri-merge` and the living docs still contained `reconcile`. After the smallest scoped edits, the focused test passed.

## Verification

Command:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PackagingAndDocsMergeRetirementTests|FullyQualifiedName~PublishManifestTests|FullyQualifiedName~SetupMergeRetirementTests|FullyQualifiedName~MergeRetirementAbsenceTests" --no-restore
```

Result: **37 passed, 0 failed, 3 skipped**. The skipped cases are the existing platform-dependent `publish.sh` reader checks.

The test suite also verifies SHA-256 byte identity for `docs/multi-user-setup.md`, the Task 5 plan, the architecture spec, its review, and `CHANGELOG.md`.

## Concerns

- The repository uses a preview .NET 10 SDK and emits existing analyzer/preview warnings.
- Full solution verification and the independent Phase 1 exit gate remain Task 6 scope.

## Round 1 finding fix

At `ec743cc`, the Phase 1 troubleshooting section still contained Phase 3 guidance for
`shared_refresh`, shared-mutation serialization, and shared staging/commit handling. The section is
now narrowed to retired driver-key cleanup and manual conflict resolution only. The regression test
asserts the exact Phase 1 commands and rejects those Phase 3 terms.

- Focused Task 5 test: **5 passed, 0 failed, 0 skipped**.
- Phase 4 `docs/multi-user-setup.md` and Phase 5 relocation ownership remain untouched.
- No separate ledger file exists in this worktree; this report is the Task 5 ledger append.
