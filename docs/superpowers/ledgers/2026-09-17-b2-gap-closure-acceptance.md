# B2 Gap-Closure Acceptance Ledger

**Date:** 2026-09-17  
**Scope:** B2 journal convergence documentation, ledger reconciliation, and scope audit only  
**Status:** Documentation reconciliation complete; implementation acceptance deferred

This ledger reconciles B2 documentation with the docs-only scope. It does not authorize or record
a behavior change, source/test edit, public-surface change, implementation remediation, or new
test execution. The B2 lifecycle and compatibility decisions remain planning inputs for a later
implementation phase.

## Boundary and parity decisions

| Decision | Evidence and disposition |
| --- | --- |
| B5 owns the sole parser boundary | `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` is the sole six-argument operation that parses bytes/JSON and validates scope, location, kind, metadata, and envelope, returning `RecoveryVerifiedEnvelope`. This reconciles the stale B2 plan interface listing to B5 design §§0.4, 0.6, 0.7, and 2.1–2.2. |
| B2 consumes the verified result | `AppendJournalCodec.ConsumeParsed`, `RestoreJournalCodec.ConsumeParsed`, `FileEntityRecoveryAdapter.ConsumeParsed`, `AppendRecoveryAdapter.ConsumeParsed`, `RestoreRecoveryAdapter.ConsumeParsed`, and `RecoveryVerifiedEnvelope.TypedBody` consume the parsed result or typed metadata, borrowing the parsed envelope from the sole parser `RecoveryEnvelopeCodec.ParseVerifiedEnvelope`. They do no I/O, JSON parsing, or duplicate validation. The former `RecoveryEnvelopeDocument`/`JsonElement Body`, `ParseAndNormalize`, `ParseAndValidate`, `RecoveryEnvelopeCodec.Parse`, and `ParseTypedBody` descriptions are historical, not interfaces to restore. |
| B3 coordinator and B4 participant boundary | `MultiMemoryTransactionCoordinator`, participant contracts, plugin quarantine/admission, and observer semantics are regression-only consumers for this closure. No coordinator lifecycle, participant handling, quarantine, or plugin contract change is claimed or accepted here. |

## Deferred findings and scope audit

| Item | Planning basis | Disposition |
| --- | --- | --- |
| Task 1 baseline-fixture minors | Missing append paths, restore body/sidecar paths, and a production-faithful committed file-entity fixture remain identified in the plan. | Deferred to a later implementation phase; no fixture work or fixture-result claim is made here. |
| Task 3 reparse fixture | Platform-specific reparse coverage requires a deterministic seam and implementation-backed tests. | Deferred to a later implementation phase; this ledger makes no portability or regression-result claim. |
| Task 6 bare-`complete` recovery journal | Independently existing completed-journal coverage may remain as contextual evidence, but it is not new B2 acceptance evidence. | Deferred for explicit B2 fixture and acceptance coverage in a later implementation phase. |
| B1 scope-contamination disclosure | The scope audit preserves the ruling that unrelated or unqualified evidence cannot widen another scope's recovery barrier. | Documentation reconciliation only; no B2 remediation or acceptance is claimed. |
| Envelope hygiene and non-removed audit residue | Duplicate validation, `CanonicalPathEquals`, and sync-over-async paths remain subject to their existing contracts. | Any envelope-hygiene or source/test remediation is deferred. This ledger neither removes paths nor accepts them as remediated. |

## Evidence boundary

No commands were run for this docs-only reconciliation. Previously reported command results are
not reasserted as current B2 acceptance evidence, particularly where underlying source or test work
was reverted.

| Evidence | Status | Permitted use |
| --- | --- | --- |
| Existing named completed-journal coverage | Exists independently of this reconciliation. | Context for future implementation planning only; not a new run or an acceptance result. |
| Prior B2 reports, command tables, and scope notes | Historical provenance only. | Do not use as evidence that deferred fixture, reparse, envelope, source, or test work is complete. |

## Final status

B2 is reconciled as a documentation, ledger, and scope-audit activity—not completed code
remediation. The B5 parse-once boundary remains the documented authoritative boundary, and B2
remains its planned typed consumer. The B3 coordinator and B4 participant surfaces remain
regression-only. Fixture completion, deterministic reparse coverage, envelope hygiene, and all
source/test remediation are deferred to a later implementation phase.

## Gap-closure execution (2026-09-17)

This section records the four B2 task-breakdown items as documentation-only investigation and
reconciliation. No source or test code was written, changed, or removed in this execution. Each
item documents what was found and how it reconciles to HEAD; implementation and acceptance remain
deferred to a later phase.

### Task 1 — Fixtures (planned, not implemented)

Closure of the four fixture minors was planned but not implemented this phase. Each minor was
recorded with an owner assigned to a later implementation phase, a fixture location, and an
acceptance check. No fixture artifact was created.

| Fixture minor | Owner | Location | Acceptance check |
| --- | --- | --- | --- |
| Missing append member/archive paths in baseline fixtures | Later implementation phase | tests/Scrinia.Tests/RecoveryTestFixtures.cs | Defined, not verified this phase |
| Missing restore body/sidecar paths | Later implementation phase | tests/Scrinia.Tests/RecoveryTestFixtures.cs | Defined, not verified this phase |
| Missing production-faithful committed file-entity fixture | Later implementation phase | tests/Scrinia.Tests/RecoveryTestFixtures.cs | Defined, not verified this phase |
| Platform-dependent reparse fixture | Later implementation phase | tests/Scrinia.Tests/RecoveryTestFixtures.cs | Defined, not verified this phase |

### Task 2 — Envelope dead-code inventory

The superseded B2 boundaries are already dead in `src/` at HEAD. The original bare `Parse` and
`ParseTypedBody` were removed, and `ParseAndNormalize`, `ParseAndValidate`, `TryReadOperationId`,
and `ConsumeLegacy` have zero production references. The rename mapping diverges by destination:
`ParseTypedBody` was renamed to `ParseTypedBodyDocument`, a **production** method (`RecoveryEnvelope.cs:182`
call, `RecoveryEnvelope.cs:204` definition), whereas `ConsumeLegacy` and `ParseLegacyJournal` were
renamed into the test facade only and have no production path reference.

The sole parser is `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` (`RecoveryVerification.cs`, ~line
299, six-argument: bytes, JSON, scope, location, kind, metadata), returning `RecoveryVerifiedEnvelope`.
Every typed consumer is free of I/O, parsing, and duplicate validation:

- `AppendJournalCodec.ConsumeParsed`
- `RestoreJournalCodec.ConsumeParsed`
- `FileEntityRecoveryAdapter.ConsumeParsed`
- `AppendRecoveryAdapter.ConsumeParsed`
- `RestoreRecoveryAdapter.ConsumeParsed`
- `RecoveryVerifiedEnvelope.TypedBody`

The only live `JsonElement` in production recovery is the intentional raw wire `Body` member on
`RecoveryEnvelopeDocument` (`RecoveryContracts.cs:39`), populated from verified bytes. No removal is
required; the envelope hygiene gate is already satisfied.

Gate evidence (no code change required):
- `RecoveryVerificationContractTests.cs` — `Recovery_source_uses_only_the_typed_parser_boundary`,
  `Recovery_envelope_exposes_only_the_exact_verified_parser_boundary`,
  `Recovery_production_surface_has_no_raw_legacy_parser_entry_points`
- `MaintenanceSerializationAndCompatibilityTests.cs` — `ParseVerifiedEnvelope_IsTheSoleSixArgumentByteJsonParser`

### Task 3 — Doc/ledger parity

The 2026-09-11 plan's "Exact Shared Interfaces" was reconciled to HEAD. The interfaces in force:

- `IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)` — single shared consume boundary
- `ValidateForMutation` — five-argument
- `RecoverOrCleanup`
- `Admit`
- No `GetOperationId` on the adapter contract
- Sole six-argument parser: `ParseVerifiedEnvelope`
- Live intentional `JsonElement Body` on `RecoveryEnvelopeDocument`

This reconciles the stale B2 plan listing to B5 design §§0.4, 0.6, 0.7, and 2.1–2.2. Ledger
descriptions were corrected to match; no production interface changed.

### Task 4 — Scope audit

Each out-of-map path was classified as either a B2 planning artifact, a different epoch's
production/test code, or shared infrastructure. None were reclassified as B2 ownership. See the
scope-audit classification table below.

| Path | B-train owner | Planning vs code |
| --- | --- | --- |
| task-3-report.md | B2 Task 3 | planning/report artifact |
| docs/superpowers/plans/2026-09-11-journal-convergence.md | B2 | planning artifact |
| docs/superpowers/specs/2026-09-15-b5-retention-maintenance-design.md | B5 (not B2) | design artifact |
| AppendContracts.cs | whole-memory-transactions / append-hardening epoch | production code |
| WholeMemoryTransactionCoordinator.cs | whole-memory-transactions phase (explicitly excluded from B2) | production code |
| FormatVersionTests.cs | durable-format-versioning phase | test code |
| RegisteredFormatsTests.cs | durable-format-versioning phase | test code |
| RecoveryBlockedFailureClassCoverageTests.cs | B1 recovery-worktree | test code |
| TestHelpers.cs | shared test infrastructure (all phases) | test code |

### Parked finding closures

- **bare-complete:** The AGENTS.md Task 6 parked bare-complete finding is satisfied at HEAD by
  existing production code. `LegacyMigrationState.cs` lines 424–425 reject a "complete" marker
  without a preceding "prepared"; the closest existing test is
  `LegacyMigrationServiceTests.CompletedJournalWithTrailingContentIsRejectedByInspectAndMigrate`
  (InlineData "complete\nprepared" rejected). No new code or test was written this phase.
- **scope-contamination:** The seven scope-audit paths above are unrelated to B2 production
  behavior. None were reclassified as B2 ownership.

### Verification boundary

The four test lanes (B2 focused, full solution, build, diff-check) were **not** re-run this phase,
because no source or test code changed. The last documented green run remains from the prior
reconciliation:

- B2 focused lane 1197 passed
- Full serial `Scrinia.Tests` 3588 pass / 19 skip
- Build 0 errors
- Diff clean

Gap-closure is documentation-only and requires no new verification.
