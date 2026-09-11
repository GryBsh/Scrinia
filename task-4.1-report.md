# Task 4.1 report — release, package retirement, and history guards

## Status

Complete. Release publishing now uses only the checked-in manifest publishers for every supported
RID and optional embeddings expansion. Retired merge packaging remains absent, while active guidance
and protected historical artifacts retain their required contracts.

## Root-cause evidence

- Independent `PublishManifestTests` run failed only at
  `ReleaseWorkflow_UsesExactManifestPublisherInvocationsForEverySupportedTarget`.
- Exact offending command: `.github/workflows/release.yml:52-53`,
  `dotnet publish src/Scrinia/Scrinia.csproj -c Release -o publish`.
- That workflow-level publish bypassed `publish.projects.tsv`, `publish.ps1`, and `publish.sh`,
  so the direct-publish guard correctly rejected it.
- `PackagingAndDocsMergeRetirementTests` passed independently; no merge package or merge-driver
  workflow surface was restored.
- The required protected artifacts were independently hashed and matched exactly:
  `docs/multi-user-setup.md` (`4ffb120fe214463e82bfe95257346b5ac37c2f79d724954f1ea3df74cd7cdd82`),
  merge-retirement plan (`272df673359ca1087ab2fdb995a30055c1e681a4b614a95b282dc82b6780dd13`),
  shared-memory design (`f61e2fbfe3b268cbd0097ec3a7efb2ba191d0c12f8895c618631776dba555f8a`),
  specification review (`3d1364da2278ee7652debcbd88de33b8c6e4a0ebb86ab7344344b10568f6f00a`),
  and `CHANGELOG.md` (`22181b7765314dd7aae8453aabe785bd3ac6ad335f5b2e5070d2d383a82aa071`).

## TDD evidence

- RED: the existing direct-publish regression guard failed on the exact workflow command above.
- GREEN: removing only that bypass restored the guard; manifest publisher invocations and archive
  validation remain unchanged.
- No golden snapshot required modification: this task changes release workflow routing only.

## Verification

- `PublishManifestTests`: **22 passed, 0 failed, 3 skipped** (bash-dependent tests skipped on
  Windows).
- `PackagingAndDocsMergeRetirementTests`: **5 passed, 0 failed**.
- `OnboardingOwnershipTests`: **3 passed, 0 failed**.
- Combined focused command: **27 passed, 0 failed, 3 skipped**.
- Test-project build: **0 errors, 0 warnings**.
- `git diff --check`: **passed**.

## Scope

Only `.github/workflows/release.yml` and this report were changed for Task 4.1. Existing user and
other Phase A changes remain untouched; no commit was created.
