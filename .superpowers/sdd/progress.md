# B2 Journal Convergence — SDD execution ledger

Workspace: `.superpowers/sdd/`
Branch: `main`
BASE: `a63a291d2efac3c3d733e039fdb0bfec0a9b4c65`
Plan: `docs/superpowers/plans/2026-09-11-journal-convergence.md`
Spec: `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`
Status: preflight complete; implementation not started

## Preflight rulings

- The plan's spec reference is resolved to the existing `-design.md` file above; no plan rewrite is required.
- This execution ledger is the only file in scope for setup. Do not modify source or tests during preflight.
- Existing worktree changes (`task-3-report.md`, `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs`, `.agents/`, `.codex/`, and the untracked task reports) predate this ledger and are not part of B2.
- Task commits named by the plan are execution checkpoints, not preflight actions. Each task must stop at its stated review gate.
- Shared production files are serialized by task dependency; later tasks may extend an earlier owner only through the typed interfaces and invariants recorded below.
- Any ruling that names a defect class requires enumeration of every instance in the touched surface before implementation dispatch; no agents are dispatched by this setup.

## Task list

| Task | Scope | Preflight ruling |
|---|---|---|
| T1 | Baseline fixtures, common contracts, source-generation registry | Establish the exact internal contract first; preserve all ordinary format registrations and public surfaces. |
| T2 | Common envelope and append/restore/file-entity adapters | Parser boundary only; dispatch typed bodies immediately; legacy reads are byte-preserving and operation invariants stay in adapters. |
| T3 | Scope-local discovery and deterministic precedence | Inspect only exact preserved locations under the existing scope boundary; unrelated evidence cannot classify another scope. |
| T4 | Append adapter integration and durability | Preserve every B1 append lock, hash, path, prune, rollback, and cleanup distinction; no committed cleanup retry may roll back. |
| T5 | Restore adapter integration and staging | Preserve snapshot isolation, selected-scope barrier/re-entry behavior, and staging containment before filesystem use. |
| T6 | File-entity adapter integration | Treat body/meta as one pair; validate all paths and verify publication before cleanup or cache invalidation. |
| T7 | Mixed versions, cleanup retry, and typed recovery errors | Parse candidates independently, block conservative conflicts, retain bytes, and make committed cleanup retry idempotent. |
| T8 | MCP and CLI typed facade wiring | Keep command/tool names, success payloads, exit classes, and recovery distinctions unchanged; never parse exception text or infer from file existence. |
| T9 | Final serialization, acceptance, and compatibility gate | Tests only unless a concrete acceptance inconsistency requires production repair; prove all ten acceptance gates and planned-path cleanliness. |

## Required preflight conflict table

One row is recorded for every task and every task pair sharing a file or interface. A pair row combines all shared surfaces for that pair.

| ID | Tasks | Shared file/interface surface | Ruling |
|---|---|---|---|
| C01 | T1 | Task baseline | T1 owns initial contract names, versions, fixtures, and registry entry; no adapter behavior is pulled forward. |
| C02 | T2 | T1 contracts; `DurableFormats.cs`; `RecoveryEnvelopeTests.cs` | T2 consumes T1's immutable shapes and may add only the common envelope/body registrations; ordinary formats remain unchanged. |
| C03 | T3 | T2 adapter interfaces; `JournaledRestoreRecoveryTests.cs` | Discovery calls adapters only after exact-location qualification; it owns ordering, not operation replay rules. |
| C04 | T4 | T2 adapters; T3 coordinator; `AppendJournal.cs`; `FileMemoryStore.cs`; `AppendJournalTests.cs` | Append changes are limited to append admission/replay and must use T3's qualified candidate; all existing append invariants remain authoritative. |
| C05 | T5 | T2 adapters; T3 coordinator; `RestoreJournal.cs`; `FileMemoryStore.cs`; `JournaledRestoreRecoveryTests.cs` | Restore owns staging/replay/cleanup, while T3 owns candidate admission; preserve the existing selected-scope barrier. |
| C06 | T6 | T1 `WholeMemoryTransaction.cs`; T2 adapter; T3 coordinator; `FileEntityTransactionCoordinator.cs`; `FileMemoryStore.cs`; `FileEntityTransactionTests.cs` | File-entity pair semantics remain local to T6; common recovery fields are additive and ordinary sidecar serialization is out of scope. |
| C07 | T7 | T2 adapters; T3 coordinator; `RecoveryEnvelope.cs`; `AppendJournal.cs`; `RestoreJournal.cs`; `FileEntityTransactionCoordinator.cs`; `FileMemoryStore.cs`; `RecoveryDiscoveryTests.cs` | T7 hardens cross-format conflict/retry behavior without changing the operation-specific owners or broadening discovery scope. |
| C08 | T8 | T7 typed `RecoveryBlock`/`RecoveryOutcome`; MCP/CLI result contracts | Facades consume validated typed data only; public names and successful semantics are compatibility contracts. |
| C09 | T9 | T1/T2 `RecoveryEnvelopeTests.cs`; T3/T7 `RecoveryDiscoveryTests.cs`; T6/T7 `FileEntityTransactionTests.cs`; T8 `RecoveryFacadeMappingTests.cs` and route tests | T9 adds acceptance evidence and may not redesign production; any needed fix must identify a concrete preceding-task inconsistency. |
| C10 | T1 ↔ T2 | `RecoveryOperation`, lifecycle enum, candidate/block/outcome records, `IRecoveryAdapter`, `DurableFormats` | T1 is the producer; T2 must use exact signatures and generated metadata, with no `object`/reflection body serialization. |
| C11 | T1 ↔ T6 | `WholeMemoryTransaction` recovery contracts | T6 may add only typed fields required by the common coordinator; no unrelated whole-memory transaction redesign. |
| C12 | T1 ↔ T9 | `RecoveryEnvelopeTests.cs` contract fixtures | T9 validates the T1 contract and cannot relax its exact enum, identity, or registry assertions. |
| C13 | T2 ↔ T3 | `IRecoveryAdapter`, normalized envelope/candidate | T3 treats adapter output as typed evidence and never reinterprets operation bodies or exception text. |
| C14 | T2 ↔ T4 | Append envelope adapter and `AppendJournal.cs` | T4 preserves T2's wire/legacy normalization boundary and supplies append-specific validation and transitions. |
| C15 | T2 ↔ T5 | Restore envelope adapter and `RestoreJournal.cs` | T5 preserves T2's wire/legacy normalization boundary and supplies restore staging/snapshot rules. |
| C16 | T2 ↔ T6 | File-entity envelope adapter and `FileEntityTransactionCoordinator.cs` | T6 preserves T2's wire/legacy normalization boundary and supplies pair/path/archive rules. |
| C17 | T2 ↔ T7 | All three adapter interfaces and `RecoveryEnvelope.cs` | T7 may add version/conflict/retry handling only through existing typed adapter outcomes; no untyped fallback. |
| C18 | T2 ↔ T9 | Envelope and operation round-trip tests | T9 verifies, rather than changes, T2's generated serialization and legacy byte-preservation guarantees. |
| C19 | T3 ↔ T4 | `RecoveryDiscoveryCoordinator`; `FileMemoryStore.cs` | T4 enters recovery through scope-qualified admission and does not add a second append-specific directory scan. |
| C20 | T3 ↔ T5 | Coordinator; `FileMemoryStore.cs`; restore tests | T5 preserves T3's restore-first ordering and scope-local barrier; no restore-wide scan or barrier is introduced. |
| C21 | T3 ↔ T6 | Coordinator; `FileMemoryStore.cs` | T6 uses qualified entity candidates and cannot promote an unqualified path or identity into a scope. |
| C22 | T3 ↔ T7 | `RecoveryDiscovery.cs`; `FileMemoryStore.cs`; discovery tests | T7 extends candidate classification conservatively; exact locations, ordinal sorting, and B1 contamination isolation remain fixed. |
| C23 | T3 ↔ T8 | `CliRecoveryBlockedRouteTests.cs` | T8 maps the coordinator's typed scope/path/identity result and never reconstructs it by scanning files. |
| C24 | T3 ↔ T9 | `RecoveryDiscoveryTests.cs`; route tests | T9 proves shuffled enumeration, precedence, ambiguity, and scope isolation without changing coordinator behavior. |
| C25 | T4 ↔ T5 | `FileMemoryStore.cs` | Shared store integration is serialized by the existing scope lock/barrier; append and restore retain separate operation bodies and rollback logic. |
| C26 | T4 ↔ T6 | `FileMemoryStore.cs` | Each operation invalidates only its canonical affected caches after verified state; no shared helper may erase pair-specific guarantees. |
| C27 | T4 ↔ T7 | Append adapter and `FileMemoryStore.cs` | T7's cleanup retry must distinguish committed append from pre-commit rollback and retain append evidence on partial cleanup. |
| C28 | T5 ↔ T6 | `FileMemoryStore.cs` | Restore staging and file-entity pair publication remain separate operation flows within one scope-local coordination boundary. |
| C29 | T5 ↔ T7 | Restore adapter and `FileMemoryStore.cs` | T7 may add idempotent retry only after live restore verification; it must not re-enter an active restore barrier or delete newer data. |
| C30 | T6 ↔ T7 | File-entity adapter, `WholeMemoryTransaction.cs`, `FileMemoryStore.cs` | T7 preserves pair verification and operation-owned deletion; verified publication is never rolled back for cleanup failure. |
| C31 | T6 ↔ T9 | `FileEntityTransactionTests.cs` | T9 adds traversal, rollback, cache, and retry acceptance evidence while preserving ordinary sidecar compatibility. |
| C32 | T7 ↔ T8 | Typed blocked/required/warning outcomes | T8 renders T7's typed classifications directly; `RECOVERY_BLOCKED`, `RECOVERY_REQUIRED`, and post-commit warning remain distinct. |
| C33 | T7 ↔ T9 | Mixed-version/recovery discovery tests | T9 verifies mixed legacy/B2, newer-version, retained-byte, and retry behavior; no silent candidate discard is acceptable. |
| C34 | T8 ↔ T9 | Facade mapping and MCP/CLI route tests | T9 compares exact public envelopes and compatibility behavior; production changes require a named failing acceptance test. |

## Execution state

- [ ] T1 — not started
- [ ] T2 — not started
- [ ] T3 — not started
- [ ] T4 — not started
- [ ] T5 — not started
- [ ] T6 — not started
- [ ] T7 — not started
- [ ] T8 — not started
- [ ] T9 — not started

No agents dispatched. No source or test files modified by setup.

## Task 1 completion

- Deferred minor: missing append paths.
- Deferred minor: restore body/sidecar paths.
- Deferred minor: production-faithful committed file-entity fixture.
- Task 1: complete (commits a63a291..9599936, 3 parked minors, review clean)

## Task 2 completion and fix-round review

- Task 1 deferred minors remain parked: missing append paths; restore body/sidecar paths; production-faithful committed file-entity fixture.
- Task 2: complete (commits `6abd775`, `6df78fc`, `9820e71`, `64e6aea`)
- Review round 1: fixes requested.
- Review round 2: fixes requested.
- Review round 3: final `APPROVE`.
- Focused verification: 132 passing.

## Task 3 completion and fix-round review

- Deferred Minor: platform-specific filesystem reparse fixture coverage.
- Task 3 fix rounds: commits `e204839`, `a7bb7a1`, `b81fc9e`, `c0c88f6`, `27fc05e`, `c8eff72`.
- Review rounds: fixes requested across the fix rounds; final review: `APPROVE`.
- Focused final-lane evidence: 133 passing.
- Task 3: complete.

## Task 4 completion and fix-round review

- Task 4 implementation: commit `008d935`.
- Review request: fixes requested.
- Task 4 review fix: commit `5785b1b`.
- Re-review: final `APPROVE`.
- Focused verification: 95 passing (`AppendJournalTests`, `AppendRecoveryTests`, and `RecoveryEnvelopeTests`).
- Non-blocking concern: verification reports the expected `NETSDK1057` warning because the repository uses a preview .NET SDK.

## Task 5 completion and fix-round review

- Task 5 implementation: commit `b948b64`.
- Initial review: fixes requested.
- Task 5 review fix: commit `3736730`.
- Re-review: final `APPROVE`.
- Focused verification: 59 passing (`JournaledRestoreRecoveryTests`); expected `NETSDK1057` preview-SDK warning.

## Task 6 completion and fix-round review

- Task 6 implementation and review/fix rounds: commits `62f4163`, `83f8190`, `129027e`, `8af35b3`.
- Review rounds: fixes requested across the fix rounds; final review: `APPROVE`.
- Focused verification: 77 passing (file-entity, agent, and recovery lane); solution build passed 13 projects with 0 errors.
- Full-suite caveat: the full solution test suite was not treated as a Task 6 acceptance gate; broader/pre-existing failures remain outside this focused verification.

## Task 7 completion and fix-round review

- Task 7 ruling: preserve the existing public `RestoreRecoveryException` contract for committed integrity failures; translate envelope failures only at the existing public restore boundary and do not widen the public exception contract.
- Task 7 implementation and review/fix rounds: commits `1eab994`, `d8672ac`, and `56a4ede`.
- Review rounds: fixes requested across the fix rounds; final review: `APPROVE`.
- Focused verification: 10 passing in the Task 7 focused lane; 51 passing in the mixed-version, discovery, and envelope lane; 195 passing in the recovery regression and append rollback compatibility lane.
- Full solution build: 13 projects, 0 errors; `git diff --check` passed.
- Non-blocking caveat: verification reports the expected `NETSDK1057` warning because the repository uses a preview .NET SDK.

## Task 8 completion and fix-round review

- Task 8 ruling: keep command/tool names, success payloads, exit classes, and recovery distinctions unchanged; never parse exception text or infer from file existence.
- Task 8 implementation and review/fix rounds: commits `1191bdd`, `60827c5`, `ac80c47`, and `c497cc2`.
- Review rounds: fixes requested across the fix rounds; final review: `APPROVE`.
- Focused verification: 37 passing in the Task 8 facade mapping lane.
- Full solution build: 0 errors; `git diff --check` passed.
- Non-blocking caveat: existing unrelated worktree changes were left untouched and were outside Task 8 scope.

## Task 9 final compatibility and acceptance gate

- Exact `.superpowers/sdd/task-9-brief.md` is a historical TransientDetector brief, not the B2 brief;
  the B2 Task 9 plan section and this ledger's T9 row are authoritative for this gate.
- Scoped gate fix: committed append cleanup evidence no longer triggers implicit cleanup from a
  read barrier; explicit recovery retry remains responsible for cleanup.
- B2-focused lane: 1111 passed, 0 failed, 0 skipped, total 1111, exit 0, elapsed 403 seconds.
- Clean serial solution: `dotnet clean Scrinia.sln -m:1` exit 0 (2 seconds); `dotnet build Scrinia.sln --no-restore -m:1` exit 0 (12 seconds), 0 errors and 3024 warnings.
- Ordinary serial full solution test: exit 1, elapsed 635 seconds; 3055 passed, 1 failed, 19 skipped, total 3075. No hang; all projects completed.
- Failure: `FileMemoryStoreWriteSurfaceTests.StoreAsync_RestoresThePreviousContentWhenPersistingTheEntryFails` at line 329; B2 read-barrier safety conflicts with the pre-B2 test expectation after failed whole-memory publication.
- Scope audit and acceptance matrix are recorded in `task-9-report.md`; unrelated range paths and
  the three Task 1 deferred minors remain identified and unabsorbed.
