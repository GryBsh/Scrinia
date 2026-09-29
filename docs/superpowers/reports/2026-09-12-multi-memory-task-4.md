# B3 Task 4 report — commit lifecycle and recovery

## Status

Implemented the coordinator prepare/decision/commit boundary and authoritative scope recovery for
one ordered persistent scope.
The coordinator publishes version-1 prepared and committed records through the existing durable
record codec, treats committed publication as the linearization point, rolls back only before that
point, retains evidence for incomplete recovery, and supports committed roll-forward and prepared
abort through typed adapter handles.

## Changes

- `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs`: added lifecycle orchestration, decision
  publication fault seam, typed aggregate outcomes, pre-decision compensation, post-decision
  recovery, cleanup classification, idempotent commit proof, and conservative record retention.
- `src/Scrinia.Core/FileMemoryStore.cs`: made coordinator evidence participate in the normal
  exclusive/read recovery barriers, wired production B2 evidence resolution, validated participant
  identity/proof, and fail closed for unresolved coordinator state. Persisted completion proof now
  permits cleanup-only replay after participant evidence has already been removed; resolver
  preflighting validates every persisted participant before constructing any adapter.
- `src/Scrinia.Core/RecoveryDiscovery.cs`: added a coordinator-excluded discovery mode used only
  while resolving the persisted participant evidence under the coordinator boundary.
- `src/Scrinia.Core/MultiMemoryTransaction.cs`: added coordinator transition identifiers used by
  the fault-injection seam.
- `tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs`: aligned the prior admission
  placeholder with the now-supported lifecycle entry point.
- `tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs`: added focused happy-path,
  pre-decision rollback, post-decision failure, decision-publication fault, proof, and
  recovery-required coverage.
- `tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs`: added malformed, mismatched,
  scope-isolation, retained-evidence, fresh-store completion-proof, and identity-mismatch recovery
  coverage.

## Verification

- Multi-memory focused lane: **51 passed, 0 failed, 0 skipped**.
- Core build: **0 errors, 0 warnings**.
- `git diff --check`: passed for the scoped files.

## Scope

No B2 journal locations or formats, participant payloads, cross-scope behavior, plugin/event
surfaces, archive policy, NMP/2, or public MCP/CLI surface was changed. Commit creation is left to
the orchestrator.
