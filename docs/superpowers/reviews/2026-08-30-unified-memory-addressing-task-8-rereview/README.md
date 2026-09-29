# Task 8 SDD Re-review Package

## Scope

- Plan: `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`
- Task: 8 — Make bundles portable and regenerate artifact keys on import
- Fix base: `ceeea45`
- Fix head: `755de18`
- Review range: `ceeea45..755de18`

This is a scoped re-review of the fix range, not a review of the complete
unified-addressing branch. Review only the changed behavior and its direct
consequences at the Task 8 bundle boundary.

## Changed files in the fix range

- `src/Scrinia.Core/Bundles/BundleFormatService.cs`
- `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`
- `tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs`

## Exclusions

- Do not modify source, tests, plans, specs, or historical review artifacts.
- Do not widen the review to Tasks 1–7 or 9–11 except where a Task 8 change
  directly relies on their contracts.
- Do not treat comments or the prior review verdict as evidence; verify claims
  against executable code and the fix-range diff.

## Review command set

```text
git diff --check ceeea45..755de18
git diff --unified=80 ceeea45..755de18 -- \
  src/Scrinia.Core/Bundles/BundleFormatService.cs \
  src/Scrinia.Core/Bundles/BundleLegacyKeys.cs \
  tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs
git diff --name-status ceeea45..755de18
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj \
  --filter "FullyQualifiedName~BundleArtifactKeyPortabilityTests|FullyQualifiedName~BundleRoundTripTotalityTests"
```

## Required review output

Report only findings at 80% confidence or higher, with file and line
references. Classify each as Critical, Major, Minor, or Nitpick. Include:

1. Files reviewed
2. Overall assessment
3. Summary
4. Findings by severity
5. At least one positive observation
6. Verification evidence and any limitations
