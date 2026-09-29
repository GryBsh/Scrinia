# Scoped SDD Re-review — Task 8

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `9567426`  
**HEAD:** `ceeea45`  
**Scope:** Task 8 bundle portability fix delta, plus the Task 8 bundle import/export contract.

## Files Reviewed

- `src/Scrinia.Core/Bundles/BundleFormatService.cs`
- `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`
- `src/Scrinia.Core/Formats/DurableFormats.cs`
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs`
- `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`
- Task 8 bundle tests listed in the specified plan
- Complete `9567426..ceeea45` fix delta

## Overall Assessment

**REQUEST_CHANGES**

The fix correctly removes known physical-location metadata before extension-data capture and adds a
cross-workspace regression test proving that selected legacy physical fields do not survive an
import/export cycle. The prior Task 8 blocker remains unresolved: name-only legacy bundle entries
are still rejected by the import build pass before the documented legacy migration can complete.

## Findings

### Major issues

#### T8-R001 — Legacy name-only bundle indexes are still rejected before migration

**Confidence:** 98%  
**Location:** `src/Scrinia.Core/Bundles/BundleFormatService.cs:319` (HEAD); legacy fixtures in
`tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs:241-284`

The reviewed head changes only `BundleLegacyKeys.NormalizeEntry` and its regression test. It does
not change the existing guard in `BundleFormatService`: after normalization/deserialization, an
entry with no `address` has the default invalid address and no usable `Name` in the typed shape, so
the import still throws before the legacy name-only representation can be reconstructed. The
existing Task 8 migration path and fixtures require those historical entries to remain importable.

At the explicit legacy boundary, when `address` is absent, derive and validate a persistent address
from the legacy name, discard any physical metadata, and continue through local artifact-key
regeneration. Do not weaken validation for a present malformed address. Add a regression test for a
name-only legacy entry that asserts canonical address, preserved metadata, and an artifact key
local to the importing store.

### Minor issues

#### T8-R002 — The new regression does not exercise every newly discarded physical-key spelling

**Confidence:** 90%  
**Location:** `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs:26-36`; test addition in
`tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs`

The implementation discards eight case-insensitive keys (`uri`, `artifactUri`, `artifactPath`,
`filePath`, `physicalPath`, `physicalUri`, `path`, and `location`), but the regression only supplies
and asserts three of them. A future edit could remove one of the other five while the new test stays
green. Extend the table-driven fixture/assertion to cover each key, including mixed casing, while
retaining an unrelated unknown field to prove the removal is intentional rather than a blanket
extension-data drop.

## Positive Observations

- Physical metadata is removed before source-generated deserialization can preserve it in
  `ArtifactEntry.Unknown`.
- Unknown non-physical metadata remains available through the import/export cycle.
- Import continues to regenerate the importing store's artifact identity rather than trusting
  source physical paths or URIs.
- The focused bundle suite passed after the fix delta.

## Verification Evidence

- Reviewed the complete `9567426..ceeea45` diff and the Task 8 requirements in the specified plan.
- `git diff --check 9567426 ceeea45`: passed.
- Focused workspace test command:
  `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~BundleArtifactKeyPortabilityTests|FullyQualifiedName~BundleRoundTripTotalityTests"`
  passed 21 tests.
- The package adds review artifacts only; source and test implementations were not modified.

## Four-Layer Review

- **Correctness:** **FAIL** — the documented name-only legacy import remains blocked; the new
  physical-metadata behavior is otherwise correct for the covered cases.
- **Security:** **PASS** for the reviewed delta — known physical metadata is discarded before
  extension-data capture and artifact keys remain subject to the existing boundary validation.
- **Performance:** **PASS** — the bounded key-set scan is linear in the number of properties in one
  index entry and introduces no store scan.
- **Style & maintainability:** **PARTIAL** — the helper is clear, but its class documentation still
  says it knows exactly two keys and the test does not enumerate the complete new key set.

## Philosophy Compliance

- Early exit: **PASS** for physical-key stripping; **FAIL** for the unresolved legacy import path,
  which exits before compatibility reconstruction.
- Parse don't validate: **PARTIAL** — physical fields are removed at the JSON boundary, but a
  missing legacy address is still treated as an invalid modern entry instead of being parsed at the
  explicit legacy boundary.
- Atomic predictability: **PASS** for the new normalization behavior; **FAIL** for the supported
  name-only legacy representation, which still cannot complete a round trip.
- Fail fast: **PASS** — malformed physical keys and malformed bundle data remain refused before
  writes.
- Intentional naming: **PARTIAL** — `PhysicalLocationKeys` is clear, while the stale “exactly TWO
  keys” class documentation contradicts the expanded set.
