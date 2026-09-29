# Scoped SDD Re-review — Task 8

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `e97fe2c`  
**HEAD:** `9567426`  
**Scope:** Task 8 bundle portability fix delta, plus the Task 8 bundle import/export contract.

## Files Reviewed

- `src/Scrinia.Core/Bundles/BundleFormatService.cs`
- `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`
- `src/Scrinia.Core/Formats/DurableFormats.cs`
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs`
- `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`
- Task 8 bundle tests listed in the specified plan
- Complete `e97fe2c..9567426` fix delta

## Overall Assessment

**REQUEST_CHANGES**

The fix correctly derives the durable leaf used by qualified addresses and adds a cross-workspace
round-trip test. However, the new `Address.IsValid` gate is incompatible with the legacy bundle
migration that the same import path and existing tests promise to retain: legacy indexes carrying
only `name` are deserialized with a default invalid address and are rejected before the existing
`name`/description/tags migration can run.

## Findings

### Major issues

#### T8-R001 — Legacy name-only bundle indexes are rejected before migration

**Confidence:** 95%  
**Location:** `src/Scrinia.Core/Bundles/BundleFormatService.cs:319` (HEAD); legacy fixtures in
`tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs:241-284`

The fix changed the entry guard from `string.IsNullOrEmpty(deserialized.Name)` to
`!deserialized.Address.IsValid || string.IsNullOrEmpty(deserialized.Name)`. A pre-Merge-3 bundle
index has `name`, `originalBytes`, and the legacy metadata keys, but no `address`; source-generated
deserialization therefore leaves `Address` at its default invalid value. The import now refuses
that entry before `BundleLegacyKeys.NormalizeEntry` can preserve its description/tags/fields, even
though the surrounding code explicitly documents legacy bundle migration and the existing legacy
fixtures use the name-only shape.

Keep the compatibility boundary explicit: when `address` is absent, derive and validate a
persistent address from the legacy name (and discard any legacy physical URI rather than exposing
it); then continue through the same portable-key regeneration path. Add a regression test for both
name-only legacy fixtures and assert that the imported address is canonical and its artifact key is
local to the importing store. Do not weaken validation for a present malformed address.

## Positive Observations

- `SubjectFromAddress` fixes the qualified persistent-address mismatch by using the address leaf for
  durable filename validation and the legacy store import seam.
- Import still regenerates `ArtifactKey` in `FileMemoryStore` rather than trusting the bundle's
  physical identity.
- The fix adds a second-workspace content round-trip and verifies that the exported JSON does not
  contain the source workspace root or a `file://` value.
- Malformed index JSON and invalid artifact-key constructor failures remain converted to the
  documented invalid-bundle failure before the write pass.

## Verification Evidence

- Reviewed the complete `e97fe2c..9567426` diff and the Task 8 requirements in the specified plan.
- `git diff --check e97fe2c 9567426`: passed.
- Focused workspace test command:
  `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore --filter "FullyQualifiedName~BundleArtifactKeyPortabilityTests|FullyQualifiedName~BundleRoundTripTotalityTests"`
  passed 21 tests in the available workspace build.
- The package adds review artifacts only; source and test implementations were not modified.

## Four-Layer Review

- **Correctness:** **FAIL** — the new validity guard blocks the documented name-only legacy bundle
  migration path.
- **Security:** **PASS** — source artifact keys are validated and regenerated before physical writes;
  the fix does not restore URI resolution.
- **Performance:** **PASS** — the added address-to-leaf derivation is constant-time relative to the
  address and does not add a store scan.
- **Style & maintainability:** **PARTIAL** — the helper is clear, but compatibility reconstruction
  belongs at the legacy boundary rather than being treated as an invalid modern address.

## Philosophy Compliance

- Early exit: **PARTIAL** — malformed modern entries fail early, but valid legacy input exits with
  the wrong category.
- Parse don't validate: **FAIL** for legacy input — the parser's missing-field result is treated as
  a malformed address instead of being converted at the explicit legacy boundary.
- Atomic predictability: **PASS** for modern bundles; **FAIL** for legacy bundles because the same
  supported historical representation no longer imports.
- Fail fast: **PASS** for malformed modern addresses and keys.
- Intentional naming: **PASS** — `SubjectFromAddress` accurately expresses the new qualified-name
  fix.
