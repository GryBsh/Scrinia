# B2 Gap-Closure — CONTEXT.md

**Date:** 2026-09-17
**Scope:** B2 journal convergence gap-closure only
**Authoritative spec:** `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`
**Authoritative plan:** `docs/superpowers/plans/2026-09-11-journal-convergence.md`
**Status source:** `task-9-report.md` (B2 Tasks 1–9 complete, PASS / APPROVE)

## Why this exists

B2 Tasks 1–9 are done and gated. The remaining work is gap-closure: fixture quality,
dead-code removal, documentation/ledger parity, and scope-audit cleanup. No behavior
change is authorized in this phase.

## Locked decisions

These four user decisions are final for this phase. Do not relitigate them in
implementation or review.

1. **Gap-closure only, zero behavior change.** This phase closes gaps without
   changing production behavior, public MCP/CLI contracts, wire bytes, file
   locations, precedence, blocking semantics, or cleanup/retry semantics. Any
   failing acceptance test that implies a behavior change is out of scope and
   must be parked with an explicit finding, not fixed by changing behavior.

2. **B5 owns `ParseVerifiedEnvelope` / `ConsumeParsed`. Reconcile stale plan
   interfaces to HEAD.** The six-argument `ParseVerifiedEnvelope` in
   `src/Scrinia.Core/RecoveryVerification.cs` is the sole operation that parses
   bytes, parses JSON, and validates scope, location, kind, metadata, and
   envelope, returning `RecoveryVerifiedEnvelope`. Typed consumers
   (`AppendJournalCodec.ConsumeParsed`, `RestoreJournalCodec.ConsumeParsed`,
   `FileEntityRecoveryAdapter.ConsumeParsed`,
   `MultiMemoryTransactionRecordCodec.ConsumeParsed`,
   `FileMemoryStore.GetOperationId`,
   `RecoveryDiscoveryCoordinator.GetOperationId`) borrow the parsed envelope and
   perform no I/O, JSON parsing, or validation. The `IRecoveryAdapter`
   `ParseAndNormalize` / `ParseAndValidate` shapes and `RecoveryEnvelopeDocument`
   `JsonElement Body` boundary in the 2026-09-11 plan's "Exact Shared
   Interfaces" section are stale. Reconcile all gap-closure work to HEAD; do
   not reintroduce superseded parser names, reduced-argument overloads, second
   parsers, or path readers. `RecoveryEnvelopeCodec.Parse` and
   `ParseTypedBody` are removed/replaced legacy entry points, not retained
   parser operations.

3. **B3 coordinator + B4 participant are regression-only.** B3
   (`MultiMemoryTransactionCoordinator` and participant contracts) and B4
   (plugin quarantine / admission state) are consumed, not modified. Gap-closure
   may add regression coverage proving B2 does not alter their behavior, but
   must not change coordinator lifecycle, participant handling, quarantine
   admission, observer semantics, or plugin contracts.

4. **Clean parked scope-contamination + bare-`complete` finding in B2.** Both
   items land in this phase, not a later branch:
   - The B1 committed scope-contamination disclosure from `task-9-report.md`
     Scope audit (`4dd315e..HEAD` paths outside the B2 Task 1–9 file
     responsibility map): justify each path or remove it; do not silently
     reclassify unrelated paths as B2 ownership.
   - The Task 6 parked bare-`complete` recovery-journal finding carried in
     `AGENTS.md`: a journal with `complete` but without `prepared` must not
     claim half-replayed state. Resolve per the converged envelope lifecycle,
     with an explicit test and ledger entry.

## Non-goals

B2 gap-closure does not:

- change NMP/2, ordinary artifacts, indexes, metadata, or ordinary sidecars;
- introduce cross-scope transactions, workspace-wide barriers, background GC,
  fleet reporting, daemons, or HTTP/server surfaces;
- move or rename `.append-journal.json`, `.restore-journal.json`, or
  `.file-entity-*.recovery` evidence;
- redesign append, restore, or file-entity business behavior;
- make legacy evidence writable in place merely to normalize it;
- change public MCP tool names, CLI command names, or successful-response
  semantics;
- add a migration command or writer;
- modify `src/` or `tests/` production and test code (this phase is
  docs, fixtures planning, ledger, and scope-audit only — see File
  discipline).

## File discipline

- **Do not modify `src/` or `tests/`.** Return planning, documentation, and
  ledger artifacts only.
- **No raw parsers or unbounded reads.** Do not add `ReadAllBytes`,
  `ReadAllText`, whole-file `SHA256.HashData`, unbounded JSON parses,
  contextless `JsonSerializer` overloads, exception-message parsing, or
  directory scans to any proposal. All recovery byte/hash decisions route
  through the B5-owned bounded verifier.
- **Preserve verifier, accounting, and cursor.** Proposals must preserve
  `MaintenanceLimits` as the sole limits owner (16 MiB total, 2-second
  monotonic deadline, 2 opens, 8 stats, 257 reads including final zero-byte
  EOF, 64 KiB buffer), caller-owned `RecoveryVerificationContent` disposed in
  the outer `finally`, exact page-accounting equations
  (`Scanned = JournalInspected + NonJournalInspected`,
  `SafeInspected = Scanned - UnsafeScanned`, shared 128-admission and
  50-journal budgets, 32-entry unsafe bound), and the fixed `MaintenanceCursor`
  (`b5c2.` + unpadded base64url of 160 bytes: 128 payload + 32 HMAC).
- **Preserve B1 ruling.** Unrelated or unqualified recovery evidence must never
  promote an ordinary failure into a recovery-blocked result for another scope.
- **Source-generated JSON only.** Any proposed DTO must name its
  `JsonSerializerContext` registration; reflection fallback is forbidden.

## Test lanes (evidence only, no code changes)

Gap-closure records evidence from these lanes; it does not add or edit tests.

| Lane | Command | Gate |
|---|---|---|
| B2 focused (1111) | `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Recovery\|FullyQualifiedName~Append\|FullyQualifiedName~JournaledRestoreRecoveryTests\|FullyQualifiedName~FileEntityTransactionTests\|FullyQualifiedName~McpRecoveryBlockedRouteTests\|FullyQualifiedName~CliRecoveryBlockedRouteTests\|FullyQualifiedName~Bundle\|FullyQualifiedName~Nmp2\|FullyQualifiedName~Sidecar\|FullyQualifiedName~Archive\|FullyQualifiedName~Lock\|FullyQualifiedName~Cache\|FullyQualifiedName~Fact\|FullyQualifiedName~Legacy\|FullyQualifiedName~Trim\|FullyQualifiedName~SourceGeneration\|FullyQualifiedName~RegisteredFormats'` | 1111 passed, 0 failed, 0 skipped |
| Full solution | `dotnet test Scrinia.sln --no-restore -m:1` | 0 failed (reference: 3370 passed, 19 skipped, total 3389) |
| Build | `dotnet build Scrinia.sln --no-restore -m:1` | 0 errors; warnings are pre-existing analyzer/preview-SDK noise |
| Diff check | `git diff --check -- <scoped paths>` | No whitespace errors |

Run lanes serially with `-m:1` where applicable. Cite exact counts and elapsed
times in the ledger; do not claim a lane passed without pasting its result row.

## Task breakdown

1. **Fixtures.** Plan (do not implement) closure of the three Task 1 fixture
   minors and the Task 3 reparse-point fixture from `task-9-report.md` Parked
   findings: missing append paths in baseline fixtures, restore body/sidecar
   paths in baseline fixtures, a production-faithful committed file-entity
   fixture, and the platform-dependent reparse fixture. Each item gets an
   owner, a fixture location, and an acceptance check that later append,
   restore, file-entity, and recovery tests already cover the intent.

2. **Envelope dead code, gated by B5 contract tests.** Inventory every
   `RecoveryEnvelopeDocument` `JsonElement Body` boundary use, legacy
   `ParseAndNormalize` / `ParseAndValidate` / `TryReadOperationId` reference,
   and any second parser or path reader that duplicates `ParseVerifiedEnvelope`.
   Mark each row removable only when the B5 Task 4 source guard and
   `RecoveryEvidenceVerifierTests` prove the typed `ConsumeParsed` /
   `GetOperationId` handoff covers it. No removal proposal may precede its
   guard row.

3. **Doc / ledger parity.** Reconcile the stale "Exact Shared Interfaces" and
   any adapter signature prose in the 2026-09-11 plan against HEAD
   (`RecoveryVerification.cs`, `RecoveryContracts.cs`, `RecoveryEnvelope.cs`,
   `RecoveryDiscovery.cs`, coordinator/journal helpers). Record each
   reconciliation as a ledger row with design section citation
   (B5 design §§0.4, 0.6, 0.7, 2.1–2.2). Update the B2 design/plan only where
   the interface description contradicts HEAD; behavior sections stay frozen.

4. **Scope-audit cleanup.** Resolve every path in the `task-9-report.md`
   Scope audit outside the B2 Task 1–9 file responsibility map
   (`task-3-report.md`, `AppendContracts.cs`,
   `WholeMemoryTransactionCoordinator.cs`, `FormatVersionTests.cs`,
   `RegisteredFormatsTests.cs`, `RecoveryBlockedFailureClassCoverageTests.cs`,
   `TestHelpers.cs`, plus the plan and B2 design specification). For each path:
   either justify it as an expected B2 planning artifact or file a removal /
   separate-scope justification. Close the B1 committed scope-contamination
   finding and the bare-`complete` finding with named evidence and ledger
   entries before the branch is merge-eligible.

## Acceptance

Gap-closure is complete when all of the following hold with no `src/` or
`tests/` modifications:

- all four locked decisions are traceable to a ledger row;
- the dead-code inventory is fully gated by B5 contract-test citations;
- doc/ledger parity rows cite HEAD signatures, not the stale plan shapes;
- the scope audit has zero unjustified paths;
- the parked scope-contamination and bare-`complete` findings are closed with
  named evidence;
- the four test lanes above are recorded green.
