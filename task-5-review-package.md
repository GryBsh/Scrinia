# Task 5 Review Package

## Review target

Task 5 of `docs/superpowers/plans/2026-09-01-merge-retirement.md`, based on `5b3073bb6a1ee34b2b08f5e75044f6b6f7bc1772`.

## Owned files

- `publish.projects.tsv`
- `.github/workflows/release.yml`
- `publish.ps1`
- `publish.sh`
- `src/Scrinia.Mcp/skills/merge-safety.md` (deleted)
- `docs/cli-reference.md`
- `docs/getting-started.md`
- `docs/troubleshooting.md`
- `tests/Scrinia.Tests/PublishManifestTests.cs`
- `tests/Scrinia.Tests/PackagingAndDocsMergeRetirementTests.cs`
- `task-5-report.md`

## Review checklist

- [x] No `scri-merge` remains in active packaging or release workflow surfaces.
- [x] No active `reconcile`, `--multi-user`, or `merge-safety` documentation remains in the owned living docs.
- [x] Troubleshooting guidance is manual-only and does not execute Git.
- [x] Phase 4 multi-user documentation remains byte-identical.
- [x] Historical planning/review files and released changelog remain byte-identical.
- [x] No Phase 5 packaging relocation was introduced.
- [x] Focused retirement and publish-manifest tests pass.

## Test evidence

`dotnet test ... --filter "FullyQualifiedName~PackagingAndDocsMergeRetirementTests|FullyQualifiedName~PublishManifestTests|FullyQualifiedName~SetupMergeRetirementTests|FullyQualifiedName~MergeRetirementAbsenceTests" --no-restore`

**37 passed, 0 failed, 3 skipped.**
