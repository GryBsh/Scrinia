# Whole-Memory Transactions — Whole-Branch Review Package

**Plan:** `docs/superpowers/plans/2026-09-10-whole-memory-transactions.md`  
**MERGE_BASE:** `e44b1a684331e40a87d7993f4ef40d8db4101999`  
**HEAD:** `cba3980`  
**SDD ledger:** `.superpowers/sdd/2026-09-10-whole-memory-transactions/progress.md`

## Scope

This review covers the complete B1 range `e44b1a6..cba3980`, all seven plan tasks, the
implementation/test diff, the task reports, and the SDD ledger. Source and test files were not
modified while producing this package.

## Overall Assessment

**REQUEST_CHANGES**

The transaction implementation and focused verification are substantial, and the focused B1 lane
passes. The branch nevertheless fails the plan's explicit final scope gate because it carries
unrelated documentation changes and a restore-journal test change, and two ledger-parked minor
coverage gaps remain open.

## Findings

### Major issues

#### B1-001 — Final branch contains changes explicitly excluded by the plan

**Confidence:** 100%  
**Locations:** `docs/architecture/cli.md`, `docs/architecture/overview.md`, `docs/cli-reference.md`,
`docs/multi-user-setup.md`, `docs/security.md`, `README.md`, `AGENTS.md`, and
`tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs` in `e44b1a6..cba3980`.

The plan's file map says not to modify unrelated living/historical documentation or restore-journal
code, and its final scope gate limits the implementation commits to the listed source/test/context
changes. The branch removes merge/reconcile documentation, changes general security/CLI guidance,
and weakens an existing restore assertion from `ContainSingle()` to a predicate `Contain(...)`.
These changes must be removed from the B1 branch or explicitly authorized as a separate scope before
merge; otherwise the branch is not reviewable as the requested plan slice.

### Minor issues

#### B1-002 — Malformed-sidecar overwrite/body-preservation regression remains parked

**Confidence:** 100%  
**Location:** SDD ledger `.superpowers/sdd/2026-09-10-whole-memory-transactions/progress.md:85-89`.

The ledger records this as a deferred Task 5 minor. The coordinator has malformed-sidecar behavior,
but the requested regression proving that an attempted overwrite refuses before changing the body is
not present in the final task evidence. Add the test before treating the sidecar boundary as fully
covered.

#### B1-003 — Sidecar warning/recovery propagation coverage remains parked

**Confidence:** 100%  
**Location:** SDD ledger `.superpowers/sdd/2026-09-10-whole-memory-transactions/progress.md:85-89`.

The ledger also records deferred coverage for `WriteSidecarMeta` `SuccessWithWarning` propagation and
retained evidence. The public MCP/CLI mapping tests cover the broader result surface, but the
sidecar-helper-specific propagation path is not closed by the recorded B1 evidence.

## Positive Observations

- The branch includes all seven task slices and preserves the internal Core/coordinator ownership
  boundary described by the plan.
- Focused whole-memory, file-entity, MCP, and CLI tests passed: **78 passed, 0 failed, 0 skipped**.
- The transaction code adds paired artifact/sidecar publication, rollback, recovery evidence,
  operation correlation, archive pairing, and explicit ephemeral exclusion coverage.
- `git diff --check e44b1a6 cba3980` passed.
- The large `RegisteredFormatsTests.cs` diff is primarily line-ending churn; ignoring whitespace
  leaves the intended paired-rollback assertion updates visible rather than an unrelated test rewrite.

## Verification Evidence

Commands run against the specified range:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj \
  --filter "FullyQualifiedName~WholeMemoryTransactionTests|FullyQualifiedName~WholeMemoryStoreTransactionTests|FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~McpWholeMemoryTransactionTests|FullyQualifiedName~CliWholeMemoryTransactionTests" \
  --no-restore
PASS: 78 passed, 0 failed, 0 skipped

git diff --check e44b1a6 cba3980
PASS
```

The concurrent solution build was invalidated by a compiler output-file lock while the focused test
build was still active (`CS2012`, `Scrinia.Core.dll` held by `VBCSCompiler`); it is not treated as a
source failure. A clean serial solution-build rerun is still required for merge approval.

## Four-Layer Review

- **Correctness:** **PARTIAL** — focused transaction behavior passes; restore-test scope drift and
  the two parked sidecar coverage gaps remain.
- **Security:** **PASS** — no new secret, process, or unsafe path behavior was identified in the
  reviewed B1 delta; transaction path validation is covered by the new coordinator tests.
- **Performance:** **PASS** — the implementation uses existing scope/entity locking and does not add
  unbounded scans to the reviewed write paths.
- **Style & maintainability:** **PARTIAL** — transaction responsibilities are clearly separated,
  but unrelated documentation and restore-test edits violate the plan's scope boundary.

## B1 Commits Included

```text
c833982  feat: define whole-memory transaction contract
7fbd1ea  fix: preserve whole-memory contract compatibility
1970e45  feat: transact persistent whole-memory writes
5ebac3c  feat: harden whole-memory recovery
cba3497  fix: complete whole-memory recovery guarantees
922c2d6  fix: retain cleanup recovery evidence
0c0e06b  feat: make compact transactional
6c31c96  fix: surface compact transaction outcomes
6155ed0  fix: expose compact recovery evidence
0ec4f03  fix: complete file entity coordinator
b86c89d  fix: complete file transaction coordinator
8775cbc  fix: harden file entity recovery
129aca8  feat: transact arbitrary sidecar mutations
23c959c  fix: harden sidecar transaction recovery
ed81d00  feat: transact skill and agent mutations
3b29a03  fix: refuse malformed skill agent rewrites
ea1d51e  fix: map skill agent lock timeouts
6d353c3  fix: map skill delete lock timeouts
916d3dd  feat: stabilize transaction result mapping
97e1484  fix: harden public recovery mapping
fb69fb8  fix: complete transaction mapping verification
cba3980  fix: complete cli warning verification
```
