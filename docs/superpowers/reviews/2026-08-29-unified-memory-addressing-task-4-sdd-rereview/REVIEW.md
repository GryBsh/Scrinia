# Scoped SDD Re-review — Task 4

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `a6cfb61`  
**HEAD:** `63e87e5`  
**Scope:** Task 4 fixes for typed artifact reads and portable artifact-key validation.

## Files Reviewed

- `src/Scrinia.Core/FileMemoryStore.cs`
- `src/Scrinia.Core/Models/ArtifactEntry.cs`
- `tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs`
- Task 4 requirements in the specified plan and unified-addressing design spec
- Complete `a6cfb61..63e87e5` fix delta

## Overall Assessment

**APPROVE**

The fix delta closes both prior Task 4 findings. Typed artifact reads now resolve the
`MemoryAddress` directly, and both the model and physical mapping boundaries reject empty,
traversal, rooted, separator-mixed, control-character, and URI-shaped artifact keys. The new
import test also exercises cross-workspace materialization and verifies that the resulting
artifact remains readable beneath the target root.

## Critical Issues

None identified at the required confidence threshold.

## Major Issues

None identified at the required confidence threshold.

## Minor Issues

None identified at the required confidence threshold.

## Positive Observations

- `ReadArtifactAsync(MemoryAddress)` now guards invalid values and calls `ResolveAddress(address)`
  without converting the typed address back into a raw qualified-name string.
- `ArtifactEntry.ValidateArtifactKey` now rejects empty segments, `.` and `..`, control/NUL
  characters, URI/path punctuation, rooted paths, and absolute URI forms before assignment.
- `MapArtifactKey` retains a second validation gate immediately before physical mapping and uses
  `PathContainment` to keep the resolved path below `MemoriesRoot`.
- Import recomputes `ArtifactKey` from the target topic and entry name before staging, so source
  physical identity is not trusted; the added test covers two workspace roots and a read-back.

## Verification Evidence

- Reviewed the complete `a6cfb61..63e87e5` diff and the Task 4 requirements in the plan/spec.
- `git diff --check a6cfb61..63e87e5`: passed.
- The focused boundary test now includes empty, dot-segment, and dot-dot-segment keys in addition
  to rooted, drive, UNC, separator, and URI-shaped cases.
- Repository search confirms no obsolete `ArtifactUri`, `ResolveArtifactAsync`, or `file://`
  implementation surface remains under `src`; historical/living documentation and legacy test
  fixtures still contain terminology scheduled for the later exhaustive migration task and are
  outside this fix delta.
- This package adds review artifacts only; source and test implementation files were not modified.

## Four-Layer Review

- **Correctness:** **PASS** — the prior typed-read reparse path and model-level traversal gap are
  fixed; import key regeneration occurs before restore payload construction.
- **Security:** **PASS** — key validation is fail-closed at construction and repeated at the
  filesystem mapping boundary, with containment enforced after path composition.
- **Performance:** **PASS** — the delta adds bounded validation and does not alter durable
  transaction sequencing or introduce additional store-wide scans.
- **Style & maintainability:** **PASS** — validation remains close to the boundaries it protects,
  and the test names the cross-workspace behavior directly.

## Philosophy Compliance

- Early exit: **PASS** — invalid addresses and keys are rejected before lookup or mapping.
- Parse don't validate: **PASS** — typed reads consume `MemoryAddress` directly rather than
  reparsing its display form.
- Atomic predictability: **PASS** — artifact-key invariants hold at both the model and physical
  boundary, while imports derive target-local keys before side effects.
- Fail fast: **PASS** — malformed keys throw descriptive `ArgumentException` failures.
- Intentional naming: **PASS** — `ResolveAddress`, `MapArtifactKey`, and
  `ImportedEntryRegeneratesAWorkspaceLocalPhysicalPath` make their responsibilities explicit.
