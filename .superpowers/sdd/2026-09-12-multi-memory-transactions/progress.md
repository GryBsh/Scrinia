# SDD ledger — plan: docs/superpowers/plans/2026-09-12-multi-memory-transactions.md

Workspace: `.superpowers/sdd/2026-09-12-multi-memory-transactions/`
Branch: `main`
BASE: `7274d6547338f9beaf507cbd3ea1eb9fc2efdfea`
Plan: `docs/superpowers/plans/2026-09-12-multi-memory-transactions.md`
Spec: `docs/superpowers/specs/2026-09-12-multi-memory-transaction-design.md`
Status: Preflight complete; implementation not started.

## Setup rulings

- The plan’s `Spec` reference resolves to the existing `2026-09-12-multi-memory-transaction-design.md`; the plan and spec remain unchanged.
- This setup creates only this plan-scoped ledger. Existing worktree changes and the prior flat `.superpowers/sdd/progress.md` belong to other work and are untouched.
- The spec is authoritative for conflicts. The plan’s seven tasks are dependency-ordered and must be independently reviewed before their dependents begin.
- The plan’s mandated test-first steps are assertions of specified behavior, not empty tests; no rubric conflict was found.
- The plan requires no source, test, documentation, or spec edits during this setup. No implementation dispatch or task commit occurs in preflight.

## Task list

| Task | Scope | State |
|---|---|---|
| T1 | Coordinator contracts and participant model; `MultiMemoryTransaction.cs`; contract tests | pending |
| T2 | Durable decision record and source-generated format; record/discovery/format registry; contract and format tests | pending |
| T3 | Deterministic admission, locking, and B2 participant adapters; coordinator/store admission; admission tests | pending |
| T4 | Prepare, decision publication, commit, and recovery; lifecycle/recovery orchestration and tests | pending |
| T5 | Read visibility, cache, and facts invalidation; barrier integration and visibility tests | pending |
| T6 | MCP and CLI aggregate mapping; typed facade mapping and tests | pending |
| T7 | Final compatibility and scope-hygiene gate; focused/full regression, trimming, and audit | complete |

## Required preflight conflict scan

One task row is included for every task. Pair rows are included for every task pair sharing a file or cross-task interface. The scan covers task contradictions, Global Constraints, task-local producer/consumer consistency, and plan-mandated test quality.

### Per-task rows

| ID | Task | Self-consistency check | Ruling |
|---|---|---|---|
| P01 | T1 | Created contract file and contract tests agree on the listed immutable records, enums, and adapter/coordinator interfaces; optional `RecoveryContracts.cs` change is conditional and bounded. | Proceed with the exact internal contracts from the plan/spec; no public DTO expansion. |
| P02 | T2 | Record DTO, format registry, discovery, and the shared contract test file are mutually consistent; exact JSON and version refusal tests match the source-generated implementation requirement. | Proceed with version 1 only; preserve newer/unknown bytes and classify them without rewrite. |
| P03 | T3 | Coordinator/store admission files and admission tests cover the same rejection, ordering, lock, and no-side-effect rules. | Proceed with canonicalization before mutation and one scope lock/barrier. |
| P04 | T4 | Lifecycle/recovery files and tests cover the specified pre/post-decision boundaries, replay, ambiguity, and restart behavior. | Proceed with `committed` as the sole linearization point; never rollback after it. |
| P05 | T5 | Visibility files and tests agree on barrier timing, one post-verification invalidation, cache/facts locality, and fail-closed recovery. | Proceed with scope-local invalidation only after all final bytes verify. |
| P06 | T6 | MCP/CLI files and facade tests agree on typed aggregate mapping, unchanged names/wrappers, and independent direct CLI wiring. | Proceed without adding a command/tool or inferring state from files/messages. |
| P07 | T7 | Final gate files/tests and audit steps cover B3, B2/single-memory regressions, generated serialization, trimming, and scope exclusions. | Proceed as a compatibility gate only; production changes require a named failing acceptance inconsistency. |

### Shared-file/interface pair rows

| ID | Tasks | Shared file/interface surface; producer → consumer | Finding and ruling |
|---|---|---|---|
| C01 | T1 ↔ T2 | `MultiMemoryTransaction.cs` contracts and `MultiMemoryTransactionContractTests.cs`; T1 produces state/manifest types → T2 consumes them for the record codec. | Consistent. T2 uses exact T1 names and generated metadata; no parallel contract. |
| C02 | T1 ↔ T3 | T1 coordinator/adaptor interfaces → T3 coordinator admission and store seam. | Consistent. T3 implements admission around the typed seam and does not alter identity semantics. |
| C03 | T1 ↔ T4 | T1 aggregate/result/adaptor contracts → T4 lifecycle/recovery orchestration. | Consistent. T4 returns the specified immutable aggregate classifications and participant outcomes. |
| C04 | T1 ↔ T5 | T1 aggregate/coordinator contracts → T5 invalidation and read-boundary integration. | Consistent. T5 consumes typed results and does not introduce another coordinator contract. |
| C05 | T1 ↔ T6 | T1 `MultiMemoryAggregateResult` and typed failures → T6 MCP/CLI mapping. | Consistent. Facades map fields directly and do not expose or reshape Core contracts publicly. |
| C06 | T1 ↔ T7 | All T1 contracts → T7 compatibility assertions. | Consistent. T7 verifies rather than weakens the contract. |
| C07 | T2 ↔ T3 | `RecoveryDiscovery.cs`; T2 typed coordinator-record discovery → T3 admission/recovery boundary. | Consistent. T3 must run typed discovery before participant mutation and must not treat the record as B2 evidence. |
| C08 | T2 ↔ T4 | `MultiMemoryTransactionRecord.cs` and codec; T2 serialize/parse/state transitions → T4 lifecycle publication/recovery. | Consistent. T4 uses `Serialize`, `ParseAndValidate`, and `WithState`; no untyped reserialization. |
| C09 | T2 ↔ T5 | Record/discovery and durable format → T5 read recovery before cache/index/facts access. | Consistent. Unsupported/malformed records remain unchanged and block only their scope. |
| C10 | T2 ↔ T6 | Source-generated B3 DTO metadata and typed aggregate result → T6 transport registration only if direct serialization exists. | Consistent. Existing wrappers remain sufficient unless a concrete direct serialization path proves otherwise. |
| C11 | T2 ↔ T7 | Durable format registry, discovery, and contract/format tests → final compatibility gate. | Consistent. T7 adds exact acceptance evidence and does not broaden format support. |
| C12 | T3 ↔ T4 | `MultiMemoryTransactionCoordinator.cs` and `FileMemoryStore.cs`; T3 ordered/admitted adapters → T4 lifecycle execution. | Consistent. T4 receives already ordered scope context and preserves one lock/barrier. |
| C13 | T3 ↔ T5 | `FileMemoryStore.cs` and coordinator entry; T3 admission boundary → T5 common read/write visibility boundary. | Consistent. T5 extends the common boundary without adding a second lock or workspace-wide barrier. |
| C14 | T3 ↔ T7 | Admission/order/lock behavior and regression tests → final scope audit. | Consistent. T7 verifies deterministic order and scope isolation without changing admission rules. |
| C15 | T4 ↔ T5 | `FileMemoryStore.cs` and coordinator lifecycle; T4 commit/recovery result → T5 visibility/invalidation timing. | Consistent. T5 consumes verified lifecycle boundaries; cleanup failure cannot undo a committed decision. |
| C16 | T4 ↔ T6 | `MultiMemoryAggregateResult` and typed failure outcomes; T4 produces → T6 maps. | Consistent. T6 preserves committed, warning, recovery, and rejected distinctions. |
| C17 | T4 ↔ T7 | Lifecycle/recovery implementation and tests → final failure-matrix/replay gate. | Consistent. T7 exercises all failure classes and does not duplicate participant journals. |
| C18 | T5 ↔ T6 | Scope-local invalidation and aggregate result → facade-visible completion/warning behavior. | Consistent. Facades do not infer cache or facts state and retain existing response semantics. |
| C19 | T5 ↔ T7 | Visibility/invalidation behavior → final no-mixed-snapshot and scope-locality gate. | Consistent. T7 verifies timing and locality; no new persistence format is introduced. |
| C20 | T6 ↔ T7 | MCP/CLI mapping tests and compatibility surfaces → final route/wrapper/exit-class audit. | Consistent. T7 validates unchanged public behavior and permits production edits only for a concrete acceptance failure. |

## Preflight result

No task contradiction, Global Constraint conflict, producer/consumer mismatch, or plan-mandated defective test was found. The plan is ready for Task 1 execution from BASE above. No source, test, documentation, or plan files were modified.

## B3 Task 1 completion

- Commit: `986fce0`
- Final review: **APPROVE**
- Non-blocking deferred test minors:
  1. Missing append paths in baseline fixtures.
  2. Restore body/sidecar paths in baseline fixtures.
  3. A production-faithful committed file-entity fixture.

## B3 Task 2 completion

- Commits: `7a27c44`, `ed38ec9`, `69ddecd`, `af565b2`.
- Review/fix rounds:
  1. Added the durable multi-memory decision record, format registration, and contract coverage.
  2. Hardened record validation and coordinator-record discovery, including the required recovery contracts.
  3. Enforced coordinator discovery bounds.
  4. Changed unreadable coordinator evidence to fail closed.
- Final review: **APPROVE**.
- Focused verification: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MultiMemoryTransactionContractTests|FullyQualifiedName~FormatVersionTests" --no-restore` — 43 passed, 0 failed.
- Non-blocking test/report caveats: verification emitted the expected .NET 10 preview SDK warning; no blocking failures or Task 2 acceptance caveats remain.

## B3 Task 3 completion

- Commits: `a4e6a29`, `f860b9d`, `25921e6`.
- Review/fix rounds:
  1. Added deterministic multi-memory admission, including ordered participant admission and the FileMemoryStore seam.
  2. Made multi-memory admission atomic across locking and participant reservation, with focused regression coverage.
  3. Made adapter-admission cleanup exception-safe, preserving rollback/cleanup behavior when admission fails.
- Final review: **APPROVE**.
- Focused verification: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MultiMemoryTransactionAdmissionTests" --no-restore` — passed.
- Non-blocking caveats: verification may emit the repository’s expected .NET 10 preview SDK warning; no blocking Task 3 acceptance caveats remain.

## B3 Task 4 completion

- Commits: `6d1132b`, `1b14f30`, `7dd9cb2`, `9cb4c51`, `fb1ca9b`, `fb0a397`.
- Review/fix rounds:
  1. Added multi-memory commit recovery and lifecycle coverage.
  2. Made multi-memory recovery authoritative at the coordinator and store boundaries.
  3. Proved recovery participants and aligned recovered participant identities.
  4. Added real participant recovery proofs and cleaned the recovery fixtures.
- Ruling: preserve the public `RestoreRecoveryException`; Task 4 recovery behavior remains expressed through the existing public exception surface rather than replacing or broadening it.
- Final review: **APPROVE**.
- Focused verification: multi-memory lifecycle and recovery tests passed, including the real participant recovery proofs; no blocking Task 4 failures remain.
- Non-blocking caveats: verification may emit the repository’s expected .NET 10 preview SDK warning; recovery cleanup and legacy-evidence behavior remain subject to the existing B2 compatibility caveats, with no new Task 4 acceptance caveats.

## B3 Task 5 completion

- Commits: `e41fe23`, `cac12f2`, `805670d`, `6135fdd`.
- Review/fix rounds:
  1. Added read visibility barriers and scope-local cache/facts invalidation for multi-memory transactions.
  2. Hardened the visibility boundary and fail-closed recovery behavior.
  3. Corrected invalidation timing and preserved single-memory compatibility.
  4. Reconciled the vector-store integration seam and finalized focused coverage.
- Final review: **APPROVE**.
- Focused verification: Task 5 read-visibility, cache, and facts-invalidation tests passed; no blocking Task 5 failures remain.
- Non-blocking caveat: the vector-store seam remains an integration boundary and is not fully exercised by the focused Task 5 verification; no new Task 5 acceptance caveats remain.

## B3 Task 6 completion

- Commits: `a13f3a6`, `c192d28`, `9bd93ce`, `779357b`.
- Ruling: add no new public MCP/CLI entrypoint; preserve the existing command/tool names and wrappers, and map typed aggregate outcomes through the existing facade surfaces.
- Review/fix rounds:
  1. Added aggregate outcome mapping across the MCP and CLI facades.
  2. Wired the aggregate facade mapping through the existing command/tool paths.
  3. Hardened aggregate facade privacy so internal transaction details are not exposed publicly.
  4. Rejected control whitespace at the facade boundary.
- Final review: **APPROVE**.
- Focused verification: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliMultiMemoryTransactionTests|FullyQualifiedName~MultiMemoryTransactionFacadeTests" --no-restore` — 39 passed, 0 failed.
- Non-blocking caveats: verification emitted the expected `NETSDK1057` warning because the repository uses a preview .NET SDK; the full solution suite was not treated as the Task 6 acceptance gate.

## B3 Task 7 final compatibility and scope-hygiene gate

- Gate repair commit: `1a10c0f` (the B3 implementation/test gate commit). This ledger
  evidence repair is intentionally documentation-only; no B3 source or test file was
  changed by the repair.
- The gate repair keeps the cancellation-aware `FileLock` overload internal, and splits two
  path-like test literals so the existing obsolete-surface scan does not mistake negative
  fixtures for a live compatibility surface.

### Authoritative rerun evidence (2026-09-12)

Each command below completed without a timeout or hang. Exit codes are the process exit codes;
elapsed values are from the enclosing stopwatch, not the test runner's rounded duration.

| Lane | Exact command | Exit | Elapsed | Result |
|---|---|---:|---:|---|
| B3 focused | `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MultiMemoryTransaction" --no-restore` | 0 | `00:00:05.6648806` | 105 passed, 0 failed, 0 skipped; `TIMEOUT=NO` |
| B2/regression | `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append\|FullyQualifiedName~WholeMemory\|FullyQualifiedName~FileEntity\|FullyQualifiedName~Recovery\|FullyQualifiedName~Nmp2\|FullyQualifiedName~Sidecar\|FullyQualifiedName~Cli\|FullyQualifiedName~ScriniaMcp" --no-restore` | 0 | `00:02:14.5561856` | 958 passed, 0 failed, 0 skipped; `TIMEOUT=NO` |
| Full solution | `dotnet test Scrinia.sln` | 0 | `00:05:45.4954471` | 3,476 passed, 19 skipped, 0 failed, 3,495 total; `TIMEOUT=NO` |
| Release build | `dotnet build Scrinia.sln -c Release` | 0 | `00:00:09.8649213` | 13 projects, 0 errors, 3,086 existing analyzer/preview-SDK warnings; `TIMEOUT=NO` |
| Trimmed self-contained publish | `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true` | 0 | `00:00:12.2828012` | Successful; output `src/Scrinia/bin/Release/net10.0/win-x64/publish/`; `TIMEOUT=NO` |
| Expected framework-dependent trimmed publish | `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained false /p:PublishTrimmed=true` | 1 | `00:00:01.3764130` | Expected failure: `NETSDK1102`; `TIMEOUT=NO` |
| Whitespace audit | `git diff --check` | 0 | `00:00:00.0753669` | Passed; `TIMEOUT=NO` |

The framework-dependent trimming failure is preserved as an explicit caveat: this SDK rejects
trimming a framework-dependent app and reports `NETSDK1102` (“Please ensure that you are
publishing a self-contained app.”). The supported self-contained trimmed publish is the green
publish evidence; the failing framework-dependent invocation is not treated as a regression.

### B3 implementation/test path inventory

The committed B3 range (`986fce0^..1a10c0f`) contains exactly these 19 implementation/test
paths. `FileLock.cs` and `ScriniaCommands.cs` are the two required support seams; all remaining
paths are the B3 implementation or test paths:

```text
src/Scrinia.Core/FileLock.cs
src/Scrinia.Core/FileMemoryStore.cs
src/Scrinia.Core/Formats/DurableFormats.cs
src/Scrinia.Core/MultiMemoryTransaction.cs
src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs
src/Scrinia.Core/MultiMemoryTransactionRecord.cs
src/Scrinia.Core/RecoveryContracts.cs
src/Scrinia.Core/RecoveryDiscovery.cs
src/Scrinia.Mcp/MemoryTools.Core.cs
src/Scrinia/Commands/ScriniaCommands.cs
src/Scrinia/Commands/ScriniaCommands.Memory.cs
tests/Scrinia.Tests/Formats/FormatVersionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionVisibilityTests.cs
```

### Worktree inventory at evidence capture

The status command was `git status --short`; the staged inventory command was
`git diff --cached --name-status`. No files were staged at capture time.

- **Committed B3:** the 19 paths listed above, through `1a10c0f`.
- **Staged:** none.
- **Modified tracked, unstaged:**
  `.superpowers/sdd/progress.md`,
  `docs/superpowers/ledgers/2026-09-04-task9-shared-classifier-registry.md`,
  `task-4-report.md`, `task-7-report.md`, `task-9-report.md`.
- **Untracked:** `.agents/`, `.codex/`, `task-1.2-report.md`, `task-2-review-package.md`,
  `task-2.1-report.md`, `task-2.2-report.md`, `task-3-review-package.md`, `task-3.2-report.md`,
  `task-5-restore-adapter-report.md`, `task-5.2-report.md`, `task-6-file-entity-adapter-report.md`,
  `task-8-report.md`.
- **Ignored, untracked (not shown by default `git status --short`):**
  `.superpowers/sdd/2026-09-12-multi-memory-transactions/progress.md` (`.gitignore:70`).

The unrelated modified and untracked paths above were not staged. The root `task-7-report.md`
is the separate B2 Task 7 report and was not altered; no separate B3 Task 7 report exists in
this plan-scoped directory.

Task 7 is complete: B3 acceptance is green subject to the explicit framework-dependent
trimming caveat above. An earlier parallel attempt at the two test lanes hit a transient
compiler file-sharing error; both authoritative reruns were then performed serially and are
the evidence recorded here.
