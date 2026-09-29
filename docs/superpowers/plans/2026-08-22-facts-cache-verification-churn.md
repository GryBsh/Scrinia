# Facts Cache Verification Churn Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to execute this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore any tracked `facts.json` cache to its reviewed baseline, verify the change using an isolated Scrinia workspace, and finish with evidence that no cache-only churn remains.

**Architecture:** This is a procedural verification plan, not a source or test change. `facts.json` remains a rebuildable derived cache; derivation version 3 may rebuild an older cache, but final verification writes only to a temporary workspace outside the repository checkout. Any cache churn that already occurred is restored by path, preserving unrelated working-tree changes.

**Tech Stack:** .NET 10, `dotnet test`, Scrinia CLI, Git, PowerShell.

**Spec:** `docs/superpowers/specs/2026-08-22-facts-cache-verification-churn-design.md`

## Global Constraints

- **No production-code changes.**
- **No isolated-test changes.**
- **`facts.json` is a derived cache, not source-of-truth data.**
- **Derivation version 3 intentionally rebuilds an older cache.**
- **Final verification must not generate facts in the repository workspace.**
- **Restore only tracked `.scrinia/**/facts.json` artifacts; never replace them with newly generated copies.**
- **Preserve unrelated source, documentation, test, and working-tree changes.**
- **Do not commit this work.**

---

## File Structure

**Create:**
- `docs/superpowers/plans/2026-08-22-facts-cache-verification-churn.md` — this execution plan.

**Modify:**
- None. The executor may restore tracked derived cache artifacts only when the pre-verification inspection proves they were changed by expected churn.

**Test:**
- None. Existing focused and full suites are executed; no test files are added or edited.

---

### Task 1: Identify and restore tracked cache churn

**Files:**
- Inspect: `.scrinia/**/facts.json` (tracked files only)
- Restore, if necessary: tracked `.scrinia/**/facts.json` files changed by verification churn

**Interfaces:**
- Consumes: the current working tree and the reviewed baseline in `HEAD`.
- Produces: a working tree in which no expected facts-cache rewrite remains, while unrelated changes are untouched.

- [ ] **Step 1: Record the starting working-tree state**

Run from the repository root:

```powershell
git status --short
git diff --name-only
git diff --stat
```

Expected evidence: the output is captured before any restoration. Any pre-existing changes outside `.scrinia/**/facts.json` are explicitly preserved throughout this plan.

- [ ] **Step 2: Enumerate tracked facts caches and inspect their current diff**

```powershell
git ls-files ':(glob).scrinia/**/facts.json'
git status --short -- .scrinia
git diff --name-status -- ':(glob).scrinia/**/facts.json'
git diff -- ':(glob).scrinia/**/facts.json'
```

Expected evidence: every candidate is a tracked `facts.json` derived cache. A changed cache is treated as expected version-3 derivation churn unless the diff or active workspace shows evidence of an independent intentional change.

- [ ] **Step 3: Restore only the tracked facts-cache paths when churn is present**

Run only if Step 2 reports modified tracked `facts.json` files:

```powershell
git restore --worktree -- ':(glob).scrinia/**/facts.json'
```

Expected evidence: `git status --short -- .scrinia` no longer reports those cache files, and `git diff -- ':(glob).scrinia/**/facts.json'` is empty. Do not use `git restore .`, remove `.scrinia`, or copy a newly rebuilt cache over the reviewed baseline.

- [ ] **Step 4: Confirm unrelated changes survived restoration**

```powershell
git status --short
git diff --name-only
```

Expected evidence: all non-cache paths reported in Step 1 are still present with the same status. Only the tracked derived cache changes identified in Step 2 may have disappeared.

---

### Task 2: Prepare an isolated verification workspace

**Files:**
- Create outside the repository checkout: a temporary verification workspace.
- Inspect after verification: repository `.scrinia/**/facts.json` paths.

**Interfaces:**
- Consumes: the repository root and the Scrinia CLI's `--workspace-root` option.
- Produces: an explicit temporary root for any CLI operation that can read or write derived store output.

- [ ] **Step 1: Allocate the isolated workspace**

```powershell
$isolatedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("scrinia-facts-verification-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
$isolatedRoot
```

Expected evidence: PowerShell prints a newly created absolute directory under the system temporary path, not under the repository root or its `.scrinia` directory.

- [ ] **Step 2: Prove the CLI command targets the isolated root**

```powershell
dotnet run --project src/Scrinia -- list --workspace-root "$isolatedRoot" --json
```

Expected evidence: the command exits with status 0 and reads the empty isolated store without creating or rewriting repository `.scrinia/**/facts.json`. The command must include `--workspace-root "$isolatedRoot"`; do not run an equivalent bare command from the repository.

- [ ] **Step 3: Retain the isolated workspace for final verification**

Keep `$isolatedRoot` until Task 4. It is the writable destination for all CLI-generated facts, indexes, and other derived output during final verification.

---

### Task 3: Run focused and full verification without repository facts generation

**Files:**
- Inspect: existing test projects only; do not edit them.
- Write generated output, if any: `$isolatedRoot` or each test's existing temporary test scope, never the repository `.scrinia` directory.

**Interfaces:**
- Consumes: the restored cache baseline from Task 1 and the isolated root from Task 2.
- Produces: focused-test and full-suite evidence with zero test failures and no repository-workspace cache generation.

- [ ] **Step 1: Run the focused facts-cache tests**

```powershell
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~EntityFactSidecarVersionTests|FullyQualifiedName~EntityFactCacheInvalidationTests"
```

Expected evidence: the command exits 0; all selected tests pass; failed, skipped, and total counts are recorded from the test output. These tests use their own temporary store roots and must not be changed to accommodate this verification.

- [ ] **Step 2: Run the full solution suite**

```powershell
dotnet test Scrinia.sln -c Debug --nologo
```

Expected evidence: the command exits 0 with `0 failed`. Record the reported passed, failed, skipped, and total counts. No test command in this task may initialize or use the repository checkout as a writable Scrinia store; any test-created stores remain in the test helpers' temporary locations.

- [ ] **Step 3: Perform any final CLI read against the isolated root**

```powershell
dotnet run --project src/Scrinia -- list --workspace-root "$isolatedRoot" --json
```

Expected evidence: the command exits 0 and any generated `facts.json`, indexes, or other derived files are confined to `$isolatedRoot`. The repository is inspected but is not the writable store.

---

### Task 4: Inspect the final change set and clean up the isolated workspace

**Files:**
- Inspect: all working-tree changes, especially tracked `.scrinia/**/facts.json`.
- Delete: `$isolatedRoot` only after all evidence has been collected.

**Interfaces:**
- Consumes: test results and the isolated CLI root from Tasks 2–3.
- Produces: final evidence that the only intended change is this plan and that no unexplained cache-only modification remains.

- [ ] **Step 1: Confirm no repository facts cache was rewritten**

```powershell
git status --short -- .scrinia
git diff --name-status -- ':(glob).scrinia/**/facts.json'
git diff -- ':(glob).scrinia/**/facts.json'
```

Expected evidence: all three outputs are empty. If a tracked cache changed despite the isolated verification, stop and restore only that path with the Task 1 command, then repeat this check. Do not accept a generated version-3 cache as the baseline.

- [ ] **Step 2: Inspect the complete final diff**

```powershell
git status --short
git diff --check
git diff --name-only
Get-Content -LiteralPath docs/superpowers/plans/2026-08-22-facts-cache-verification-churn.md
```

Expected evidence: `git diff --check` reports no whitespace errors; `git diff --name-only` contains only tracked pre-existing changes, while `Get-Content` verifies the untracked plan itself. The status output contains this plan and any unrelated pre-existing user changes, but no source-code, test-file, or cache-only change introduced by this work.

- [ ] **Step 3: Remove the isolated workspace**

```powershell
Remove-Item -LiteralPath $isolatedRoot -Recurse -Force
Test-Path -LiteralPath $isolatedRoot
```

Expected evidence: the final command prints `False`. This cleanup is outside the repository and does not affect the reviewed tracked cache.

- [ ] **Step 4: Recheck status after cleanup**

```powershell
git status --short
git diff --name-only
```

Expected evidence: the plan file is the only change introduced by this documentation task; no `.scrinia/**/facts.json` path appears; unrelated pre-existing changes remain intact. Do not commit.

---

## Acceptance Criteria

- [ ] `facts.json` is explicitly handled as a rebuildable derived cache, not source data.
- [ ] The plan records that derivation version 3 intentionally rebuilds an older cache.
- [ ] Any tracked cache changed by expected verification churn is restored to its reviewed `HEAD` baseline, never replaced with a generated copy.
- [ ] Restoration is path-scoped and demonstrably preserves unrelated working-tree changes.
- [ ] Focused facts-cache tests complete with zero failures.
- [ ] The full solution suite completes with zero failures.
- [ ] All CLI verification uses `--workspace-root "$isolatedRoot"`; no final verification writes facts to the repository workspace.
- [ ] Final `git diff --check` is clean.
- [ ] Final status contains no unexplained cache-only modification and no source or isolated-test modification.
- [ ] No source code or test file is modified, and no commit is created.

## Spec Coverage

| Spec requirement | Plan location |
|---|---|
| Recognize `facts.json` as derived and version-3 rebuild churn as expected | Global Constraints; Task 1, Steps 1–2 |
| Restore the reviewed tracked cache baseline | Task 1, Steps 3–4 |
| Keep final facts generation outside the repository workspace | Task 2; Task 3, Steps 2–3 |
| Run focused and full tests | Task 3, Steps 1–2 |
| Preserve unrelated changes and inspect the final diff | Task 1, Step 4; Task 4, Steps 1–2 and 4 |
| No production-code or isolated-test changes | File Structure; Global Constraints; Acceptance Criteria |
| No unexplained cache-only modification | Task 4, Steps 1–4 |

## Self-Review

- **Placeholders:** none; every step names its command, expected evidence, and scope.
- **Consistency:** the plan uses the same `$isolatedRoot` for all CLI verification and restores only the Git pathspec `:(glob).scrinia/**/facts.json`.
- **Scope:** no implementation, test authoring, cache regeneration, or commit step is included.
