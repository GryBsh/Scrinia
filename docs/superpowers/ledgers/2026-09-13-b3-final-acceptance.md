# B3 final acceptance record

**Disposition: ACCEPTED — final gate evidenced; B4 dependency unblocked.**

This record is the dependency gate for B4. The authoritative transcript, named skipped tests, commit
boundary reconciliation, and independent review are checked in at the links below. This acceptance
does not authorize changes outside the B4 plan inventory.

**Authoritative evidence:** [`../evidence/2026-09-13-b3-final-gate-transcript.md`](../evidence/2026-09-13-b3-final-gate-transcript.md)

**Independent review:** [`../reviews/2026-09-13-b3-final-gate-review.md`](../reviews/2026-09-13-b3-final-gate-review.md)

## Evidence register

| Required evidence | Checked-in reference | Status and limitation |
|---|---|---|
| B3-focused test command, raw output, exit code, elapsed time, no-hang result | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#b3-focused-tests) | Accepted; complete capture hash and exact result are recorded. |
| B2/regression command and raw output | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#b2regression-lane) | Accepted; complete capture hash and exact result are recorded. |
| Full solution command and raw output | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#full-solution-tests) | Accepted; 19 skips are named/classified and complete capture hash is recorded. |
| Release build output | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#release-build) | Accepted; exact result and complete capture hash are recorded. |
| Trimmed publish output | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#trimmed-publishes) | Accepted with explicit expected framework-dependent `NETSDK1102` caveat. |
| Diff, status, and staged inventory | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#diff-status-and-staged-inventory) | Accepted; no staged paths, raw result lines, and hashes are recorded. |
| B3 implementation/test path inventory | [`evidence transcript`](../evidence/2026-09-13-b3-final-gate-transcript.md#scope-inventory) | Accepted; the 19 paths are confirmed by `git diff --name-status 986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`. |
| Independent final-gate review | [`independent review`](../reviews/2026-09-13-b3-final-gate-review.md) | Accepted; review is independently identified and approves the exact B3 range. |

The three checked-in Task 5 verification files are narrower evidence only:
`.superpowers/sdd/2026-09-12-multi-memory-transactions/task-5-review/verification/focused-visibility.txt`,
`solution-build.txt`, and `diff-check.txt`. They record 4 focused tests, a 13-project build, and a
diff check, respectively; they do not contain the B3 final suite, publish, status, staged inventory,
or independent final review. Their contents have no recorded hashes or elapsed/no-hang data.

## Commit reconciliation

Two incompatible boundaries are present in the available records:

- `.superpowers/sdd/2026-09-12-multi-memory-transactions/progress.md:147-176` identifies the B3
  implementation/test gate through `1a10c0f` and describes the range as `986fce0^..1a10c0f`.
- An earlier version of this acceptance file and the B4 plan named `63befd2` as the accepted
  boundary, without a checked-in commit inventory or raw diff/status evidence. That stale reference
  is superseded by this reconciled record.

Repository history resolves the discrepancy. The exact B3 implementation/test range is
`986fce0a^..1a10c0f77316fa0717c9ad4307a162a8176c1041`; `63befd20` is later documentation-only
path-inventory correction and is not part of that range. `4a15e329` is evidence documentation and
`5f17ae42` is B4 planning/specification documentation; neither is a B3 implementation/test commit.
Current `HEAD` is later at
`5f17ae428212186e0cb8bb9ad8362b5d37caf3a1`.

## Reported B3 implementation/test inventory

The untracked execution ledger reports these 19 paths for the B3 implementation/test gate. This is
an inventory claim, not a checked-in diff proof:

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
tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs
tests/Scrinia.Tests/Formats/FormatVersionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs
tests/Scrinia.Tests/MultiMemoryTransactionVisibilityTests.cs
```

The current worktree also contains unrelated modified and untracked paths reported by that same
ledger. Because the raw `git status --short` and `git diff --cached --name-status` outputs are not
checked in, this record cannot prove that the inventory is complete or that no unrelated path was
staged.

## Acceptance basis

The linked transcript contains the authoritative command lines/results, complete-capture hashes,
exit codes, elapsed times, explicit no-timeout markers, skip classification, and worktree inventory.
The linked independent review records `APPROVE` against the exact range. No B4 source or test
implementation is included in this documentation-only repair.
