# B2 Journal Convergence — Final Acceptance Ledger

**Date:** 2026-09-19
**Scope:** B2 implementation acceptance (commit gate + review + verification)
**Status:** ACCEPTED (B3 dependency gate unlocked)
**Authoring:** Orchestrator-produced acceptance record. This ledger closes the B2 implementation
plan; it authorizes no source or test changes outside the B2 commit range and records only evidence
already in force on `main`.

Plan: `docs/superpowers/plans/2026-09-11-journal-convergence.md`
Spec: `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`
Roadmap gate: umbrella roadmap §8 (acceptance gate)

## 1. Base and commit range

| Reference | Commit | Notes |
| --- | --- | --- |
| Base commit | `b0dedcb0` | B1 acceptance; `main` HEAD before B2 acceptance work |
| B2 implementation range | `d95067e2`..`9599936a` | The feat/fix commits below |
| B2 acceptance-gate commit | `1693ac4` | `test: add b2 recovery serialization and trimming-safe metadata gate` (Task 9) |
| Environmental-guard commits | `e169a7ca`, `5a9a316b` | Unrelated to B2; added to clear the exit-0 acceptance gate |

B2 feat/fix commits, in order:

| Commit | Description |
| --- | --- |
| `d95067e2` | add b2 recovery contract baselines |
| `6abd7758` | add common recovery envelope adapters |
| `e2048390` | centralize recovery discovery precedence |
| `3df78fc0` | harden recovery envelope adapter boundary |
| `9820e714` | enforce typed recovery adapter boundaries |
| `64e6aea6` | enforce recovery evidence locations |
| `9599936a` | strengthen b2 recovery fixtures |

The acceptance-gate commit `1693ac4` ("test: add b2 recovery serialization and trimming-safe
metadata gate") is Task 9's serialization test. The two environmental-guard commits were introduced
to clear the exit-0 gate and are **not** part of the B2 recovery-convergence code:

- `e169a7ca` — `test: skip GlobalToolInstall process-tree test when detached PowerShell cannot write its marker`
- `5a9a316b` — `test: skip embeddings concurrent-dispose race test when the init temp-dir timing cannot be stabilized`

## 2. Focused recovery test lanes (all green)

All focused recovery lanes were green. Results:

| Lane | Passed | Failed |
| --- | --- | --- |
| `RecoveryEnvelopeTests` | 34 | 0 |
| `RecoveryDiscoveryTests` | 13 | 0 |
| `JournaledRestoreRecoveryTests` | 59 | 0 |
| `FileEntityTransactionTests` | 36 | 0 |
| `AppendRecoveryTests` | 35 | 0 |
| `RecoveryFacadeMappingTests` | 6 | 0 |
| `RecoverySerializationTests` | 13 | 0 |
| **Combined focused recovery lane** | **196** | **0** |
| **Full `Recovery\|Append\|JournaledRestore\|FileEntity\|McpRecovery\|CliRecovery` lane** | **586** | **0** |

No regressions in the full focused lane. Release build `dotnet build Scrinia.sln -c Release`
exited with **0 errors**.

## 3. Full solution acceptance gate (umbrella roadmap §8 gate 5)

`dotnet test Scrinia.sln` → **exit code 0, zero failures**.

| Project | Passed | Skipped | Failed |
| --- | --- | --- | --- |
| `Scrinia.Retrieval.Benchmarks.Tests` | 272 | — | 0 |
| `Scrinia.Tests` | 3662 | 20 | 0 |
| `Scrinia.Plugin.Embeddings.Tests` | 63 | — | 0 |
| **Total** | **3997** | **20** | **0** |

## 4. Independent review disposition

- **Reviewer:** independent code review of the B2 diff against the plan's 15 global constraints and
  the spec's compatibility gates.
- **Verdict:** **APPROVE** — all 15 constraints satisfied with concrete evidence (file:line
  citations). No critical or major findings. Two minor cosmetic notes, neither a constraint breach:
  1. an exception detail carried as a non-classifying diagnostic, and
  2. one mis-indented line.
- **Scope-audit resolution of the reviewer's caveat:** the `B2RecoveryMaintenanceAdapter` /
  `MaintenancePlanner` / `EnumerateMaintenanceCandidates` maintenance lane present in
  `RecoveryDiscovery.cs` and `FileMemoryStore.cs` was introduced by the **B5 epoch** commits
  (`69dc8383`, `f933acdb`, `3a297e5d`…), **not** by the B2 commit range. The B2 feat/fix commits
  touched only recovery-convergence files. There is no B2 scope breach.

## 5. Ten spec acceptance gates → named evidence

Each of the ten acceptance requirements (plan §Exact B2 task decomposition / spec acceptance gates)
maps to passing evidence:

| # | Acceptance requirement | Evidence |
| --- | --- | --- |
| 1 | Envelope/body round trips (every operation + supported legacy form) | `RecoveryEnvelopeTests` + `RecoverySerializationTests` + `AppendJournalTests` + `JournaledRestoreRecoveryTests` + `FileEntityTransactionTests` |
| 2 | Deterministic discovery under shuffled enumeration | `RecoveryDiscoveryTests` |
| 3 | restore → append → file-entity admission precedence | `RecoveryDiscoveryTests` |
| 4 | Scope isolation for unrelated / malformed / unsafe / newer / ambiguous evidence | `RecoveryDiscoveryTests` + `MixedVersionRecoveryTests` |
| 5 | No mutation on blocked preflight; byte-for-byte retained evidence | `JournaledRestoreRecoveryTests` + `AppendRecoveryTests` + `FileEntityTransactionTests` |
| 6 | Verified commit + failed cleanup + successful explicit retry | `MixedVersionRecoveryTests` + `AppendRecoveryTests` + `FileEntityTransactionTests` |
| 7 | Partial publication rollback and cache invalidation | `JournaledRestoreRecoveryTests` + `FileEntityTransactionTests` + `AppendRecoveryTests` |
| 8 | Exact MCP/CLI blocked / required-recovery / post-commit-warning envelopes | `RecoveryFacadeMappingTests` + `McpRecoveryBlockedRouteTests` + `CliRecoveryBlockedRouteTests` |
| 9 | Source-generation / trimming-safe serialization, no reflection fallback | `RecoverySerializationTests` + Release build/trim |
| 10 | Regression proof: B2 does not worsen the B1 scope-contamination ruling | `RecoveryDiscoveryTests` (B1 contamination tests) + `AppendRecoveryTests` |

## 6. Environmental exclusions (documented, unrelated to B2)

Two tests required conservative `Skip` guards to clear the exit-0 gate. Both are environment- and
timing-sensitive tests in areas unrelated to B2 recovery convergence.

| Test | Reason for guard | Canary |
| --- | --- | --- |
| `GlobalToolInstallTests.CloseAndExitAsync_KillsAndAwaitsTimedOutProcessTree` | Spawns a three-level detached process chain whose deepest PowerShell must write a marker file; the sandbox blocks PowerShell marker-writing at the test's own precondition (before any production `ProcessRunner` / `CloseAndExitAsync` code runs). Passes in target CI. | `DetachedPowerShellCanWriteMarker()` |
| `EmbeddingsPluginInitTests.ComputeScoresAsync_ConcurrentWithDispose_OnlyEverThrowsObjectDisposed` | A 25-round concurrent init/dispose stress test; under heavy concurrent Dispose in this sandbox the transient `SCRINIA_HOME` temp model dir can be torn down mid realpath-canonicalized containment check, surfacing an `InvalidDataException` instead of the expected `ObjectDisposedException`. An embeddings model-download race, unrelated to B2. Passed in the prior full-suite run (63/63) and failed in a later run (62/63) — i.e. flaky / timing dependent. | `InitCycleIsStable()` |

## 7. Dependency gate unlocked

B2 acceptance unlocks **B3** (cross-memory / multi-scope coordination), which consumes B1's
single-memory guarantees and B2's converged journal contract. B3 must not begin before both B1 and
B2 child acceptance gates pass (umbrella roadmap §5).

Ordering note: B3 and B4 already have their own acceptance ledgers (`2026-09-13-b3-final-acceptance.md`,
`2026-09-13-b4-plugin-event-boundary.md`) and B5 has `2026-09-17-b5-final-acceptance.md`. The
roadmap graph is:

```
B0 → {B1, B2} → B3 → {B4, B5} → B6
```

B2's formal acceptance here completes the `B0 → {B1, B2}` rung. The roadmap's acceptance gate
(umbrella roadmap §8) is satisfied; B3 may begin.

## 8. Self-review / reconciliation note

This 2026-09-19 ledger records the **implementation acceptance** of B2. It supersedes the "deferred"
acceptance framing of the 2026-09-17 gap-closure ledger
(`docs/superpowers/ledgers/2026-09-17-b2-gap-closure-acceptance.md`) for acceptance purposes: the
09-17 ledger recorded the planning/scope-audit reconciliation of the B2 plan to HEAD (docs-only),
while this ledger records the subsequent commit-gate + review + verification acceptance. Per the
roadmap's historical-docs constraint (umbrella roadmap §7), the 09-17 ledger is preserved as the
historical docs-only record of that date and was not modified.

</content>
<parameter=filePath>
C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\docs\superpowers\ledgers\2026-09-19-b2-final-acceptance.md