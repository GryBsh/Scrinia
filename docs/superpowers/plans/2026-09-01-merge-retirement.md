# Merge Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove every active merge, reconcile, and multi-user merge surface while leaving only the explicitly named Phase 2 migration-recognition constants, Core types, and tests needed to identify legacy configuration safely.

**Architecture:** Phase 1 retires behavior, commands, packaging, hooks, references, and active documentation. It does not delete the migration-only recognition contract owned by Phase 2: `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` and `tests/Scrinia.Tests/LegacyMergeRecognitionTests.cs`. Those artifacts recognize exact legacy `.gitattributes` lines and exact `merge.config` bytes; they perform no cleanup, invoke no Git process, and are consumed only by Phase 2’s migration implementation. No adapter or fallback merge implementation remains.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, `dotnet build`, `dotnet test`, filesystem-only validation, `DurableFile`, and `PathContainment`. No new project or dependency.

**Spec:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §§4.1, 7.1, 7.2, 8.3, 9, 13, 13.1, 14 item 10, 16, 17. Review record `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R3-01, R3-02, R5-04, R6-06.

## Global Constraints

- Scrinia never launches Git: no `Process.Start("git")`, `RunGit`, Git integration test harness, or hidden Git fallback anywhere in `src/` or `tests/`.
- Phase 1 removes active merge behavior and all current command/config/hook/package/docs surfaces; only the named Phase 2 migration-recognition files may retain the two exact legacy `.gitattributes` lines, exact legacy `merge.config` bytes, and static unset-command strings.
- `LegacyMergeRecognition` is recognition-only: it has no file mutation, command execution, registry mutation, merge algorithm, conflict resolution, or active classifier registration.
- Preserve `/temp/...` ephemeral addressing and single-user `restore` unchanged.
- Do not modify `docs/multi-user-setup.md`; Phase 4 owns its replacement. Preserve dated planning/review artifacts and released changelog entries byte-for-byte.
- Use UTF-8 without BOM, LF endings, `PathContainment` for retained path checks, and explicit Git paths only; `cmds.txt` remains untracked and nothing is pushed.
- The architecture spec is `Proposed`; this plan makes no approval claim (review R3-02).

---

## Files

### Removed or swept

- Delete `src/Scrinia.Merge/` and `tests/Scrinia.Merge.Tests/`, including every merge algorithm, driver, repair helper, integration harness, and project reference.
- Delete active merge code under `src/Scrinia.Core/Merge/`; retain only the named `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` contract for Phase 2.
- Remove `reconcile` from `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia/Commands/ScriniaCommands.cs`, golden responses, fixtures, tests, and embedded tool descriptions.
- Remove `setup --multi-user`, merge-driver installation/repair, merge hooks, `merge.config` writes, `.gitattributes` writes, `scri-merge` references, and active merge keys from `src/Scrinia/Commands/ScriniaCommands.Setup.cs` and `WorkspaceSetup.cs`.
- Remove `Scrinia.Merge`/`scri-merge` from `Scrinia.sln`, `publish.projects.tsv`, `.github/workflows/release.yml`, `publish.ps1`, and `publish.sh`.
- Remove the `merge-safety` skill and its active rows/sentences from `docs/getting-started.md`; remove only the named `reconcile` and `--multi-user` sections from `docs/cli-reference.md`.

### Retained Phase 2 migration-only files

- `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs` — exact constants and pure recognizers used by Phase 2; no active merge behavior.
- `tests/Scrinia.Tests/LegacyMergeRecognitionTests.cs` — exact recognition, malformed-input, false-positive, and no-process tests. This is the only merge-named test file permitted to remain.

### Modified

- `src/Scrinia/Commands/ScriniaCommands.Setup.cs` — replace retired setup behavior with `PrintMergeRemovedHint`, which prints exact agent-run conflict cleanup and legacy driver-unset commands without executing them.
- `docs/troubleshooting.md` — add only `Manual Git conflict cleanup after merge retirement`, containing the spec §7.2/§8.3 manual commands; do not rewrite other headings.
- `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` — reword the stale merge-driver comment to describe unknown-key survival.

## Interfaces

### Produced for Phase 2 only

```csharp
public static class LegacyMergeRecognition
{
    public const string ScriniaMetaAttributeLine = "*.meta.json merge=scrinia-meta";
    public const string ScriniaNmp2AttributeLine = "*.nmp2 merge=scrinia-nmp2";
    public static bool IsRecognizedGitAttributesLine(string line);
    public static bool IsExactLegacyMergeConfig(ReadOnlySpan<byte> bytes);
    public static IReadOnlyList<string> DriverUnsetArgv();
}
```

`IsRecognizedGitAttributesLine` matches only the two exact lines (with the §8.3 line-ending rule); `IsExactLegacyMergeConfig` matches only the exact legacy bytes plus one LF; `DriverUnsetArgv` returns the two static `git config --local --unset-all` argument sequences in classifier order. No method starts a process or changes a file. Phase 2 owns the implementation and all migration-state behavior.

### Retained active contracts

- `IMemoryStore`, `FileMemoryStore`, `DurableFile`, `PathContainment`, `/temp/...`, and `restore` remain unchanged.
- `MemoryTools` exposes no `Reconcile` or `MemoryReconcile`; the remaining memory operations continue to compile.

## Test Plan and Assigned Tasks

Every gate below is assigned to a concrete task. Each task is a TDD cycle: write the failing test, run it, implement the smallest change, rerun it, and commit explicit paths.

### Task 1 — Freeze the migration-only boundary

**Files:** Create `src/Scrinia.Core/Migration/LegacyMergeRecognition.cs`; create `tests/Scrinia.Tests/LegacyMergeRecognitionTests.cs`.

- [ ] Write tests for both exact `.gitattributes` lines, exact legacy `merge.config` bytes, malformed/near-match rejection, ordered unset argv, and source inspection proving no `Process.Start`, `RunGit`, file writes, or merge algorithm exists in the type.
- [ ] Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter LegacyMergeRecognitionTests`; expect failure before the named type exists.
- [ ] Implement only the pure constants/recognizers/signatures above; do not add a classifier registration or cleanup operation.
- [ ] Rerun the filter and confirm pass; commit only the two named files.

### Task 2 — Delete projects and active merge implementations

**Files:** Delete `src/Scrinia.Merge/**`, `tests/Scrinia.Merge.Tests/**`, and active `src/Scrinia.Core/Merge/**`; modify `Scrinia.sln` and remove active merge references from all source/tests.

- [ ] Add `MergeRetirementAbsenceTests.cs` with path/reference assertions that exclude only the two named migration files and their tests.
- [ ] Run the focused test; expect failure while projects/types/references remain.
- [ ] Delete the active projects/types and remove solution/test references without deleting `LegacyMergeRecognition`.
- [ ] Run the focused test and `dotnet build Scrinia.sln`; confirm no reverse or dangling reference remains.

### Task 3 — Remove reconcile and current CLI/MCP surfaces

**Files:** `MemoryTools.cs`, `MemoryTools.Lifecycle.cs`, `ScriniaCommands.cs`, golden response files/cases, and all active reconcile tests.

- [ ] Add `ReconcileRemovalTests.cs` asserting unknown CLI command, no reflected MCP tool, absent golden case, and absence of active `Reconcile`/`MemoryReconcile` symbols.
- [ ] Run focused tests; expect failure.
- [ ] Remove the command/tool/switch/helpers/fixtures while preserving all other lifecycle tools.
- [ ] Run focused tests and the MCP/core test projects; confirm pass.

### Task 4 — Remove setup merge behavior and Git execution

**Files:** `ScriniaCommands.Setup.cs`, `WorkspaceSetup.cs`, setup tests, hooks, and active merge config fixtures.

- [ ] Add `SetupMergeRetirementTests.cs` covering absence of `ConfigureMultiUser`, `SerializeMergeConfig`, `ResolveMergeDriverPath`, `RunGit`, merge hooks, writes to `.gitattributes`/`merge.config`, and any `Process.Start("git")` across `src/` and `tests/`; separately assert the two named recognition files are allowed.
- [ ] Run focused tests; expect failure.
- [ ] Remove all active setup/config/hook logic and implement `PrintMergeRemovedHint` with exact print-only commands from §§7.2 and 8.3.
- [ ] Run focused tests and verify the command output contains commands but launches no process.

### Task 5 — Remove packaging and active documentation surfaces

**Files:** `publish.projects.tsv`, release workflow/scripts, `docs/cli-reference.md`, `docs/getting-started.md`, and `docs/troubleshooting.md`.

- [ ] Add `PackagingAndDocsMergeRetirementTests.cs` asserting no `scri-merge`, active `reconcile`, `--multi-user`, merge-safety skill, or active merge workflow; assert the named troubleshooting heading contains manual guidance only and `docs/multi-user-setup.md` is unchanged.
- [ ] Run focused tests; expect failure.
- [ ] Remove only the owned active sections, package rows, and script comments; add the one named troubleshooting heading.
- [ ] Run focused tests and hash-check all historical files and untouched documentation sections.

### Task 6 — Independent Phase 1 exit gate

- [ ] Run repository-wide searches for active merge/reconcile/Git execution references, explicitly exclude only `LegacyMergeRecognition.cs`, `LegacyMergeRecognitionTests.cs`, this plan’s citations, and the named manual-help text.
- [ ] Run `dotnet build Scrinia.sln` and `dotnet test Scrinia.sln` with benchmarks excluded.
- [ ] Confirm `/temp/scratch`, `/temp/probe`, and `restore` tests pass; confirm `docs/multi-user-setup.md`, dated docs, and released changelog hashes are unchanged.
- [ ] Record the Phase 2 handoff: only the exact recognition contract remains, and Phase 2 owns its migration states/tests.

## Exit Criteria

- `Scrinia.Merge`, `Scrinia.Merge.Tests`, `scri-merge`, active merge hooks/configuration, `reconcile`, `setup --multi-user`, and active merge skill/docs are absent from source, tests, solution, packaging, and current command surfaces.
- No Scrinia-launched Git process exists; `PrintMergeRemovedHint` is print-only.
- The only permitted legacy merge strings/types/tests are the exact named Phase 2 recognition files and their exact constants/tests; no mutation or merge behavior is present there.
- `/temp/...` and single-user `restore` remain functional; Phase 4’s `docs/multi-user-setup.md` and historical artifacts remain byte-identical.
- `dotnet build Scrinia.sln` and `dotnet test Scrinia.sln` (benchmarks excluded) pass independently after all removals.

## Coverage Matrix

| Spec/review ruling | Requirement | Assigned task |
|---|---|---|
| §9, §13 Phase 1 | Remove all active merge/reconcile/multi-user surfaces | Tasks 2–5 |
| §7.1 | No Git process invocation | Task 4; Task 6 repository-wide gate |
| §4.1 | No active conflict/reconciliation/scratch classifier | Task 2; Task 6 |
| §8.3, R5-04 | Legacy recognition is migration-only and explicit | Task 1; Phase 2 owns migration task |
| §13.1, R3-01 | Disjoint docs and Phase 4 handoff | Task 5 |
| §16, R3-02, R6-06 | Preserve history and declare source labels | Task 5; Task 6 |
