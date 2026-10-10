# UX Polish Review — Release runtime shim diagnostics

**Date:** 2026-10-10
**Scope:** Four diagnostics in `evidence/task14-ux-copy-shim.txt`, checked against their immediate conditions in `src/Scrinia/Scrinia.csproj`, `publish.sh`, and `publish.ps1`. Release operator/developer audience; report-only addendum.
**Reviewer:** ux-polish-reviewer agent

The diagnostics are direct and consistent; the two publisher errors would be clearer with the checked path, target RID, and non-file condition.

**Counts:** 0 Must fix · 1 Should fix · 0 Consider.

## Must fix

None.

## Should fix

1. **Publisher errors omit the non-file condition and identify only the filename.** `publish.sh:261`, `publish.ps1:234`.
   Both conditions reject a missing file, a path that is not a file, or a link. The current text omits the second case and prints only the basename. Include the checked path and RID so operators can locate the failed build output. Both scripts already have those values; preserve their existing failure behavior.
   - Before, Bash: `Error: required build-resolved Unix runtime shim is missing or linked: $runtime_shim`
   - After, Bash: `Error: required build-resolved Unix runtime shim is missing, is not a file, or is a link: $out_dir/$runtime_shim (RID: $rid)`
   - Before, PowerShell: `Required build-resolved Unix runtime shim is missing or linked: $RuntimeShim`
   - After, PowerShell: `Required build-resolved Unix runtime shim is missing, is not a file, or is a link: $ShimPath (RID: $rid)`

## Consider

None.

## Consistency notes

- Retain `Unix runtime shim`, `build-resolved`, and `RID` across these operator diagnostics. Preserve the Bash `Error:` convention and PowerShell exception convention.
- `src/Scrinia/Scrinia.csproj:73` accurately describes the exactly-one matching runtime asset requirement; `:75` accurately describes the missing/non-file check and includes the resolved path. Both are already suitable.
- The matching asset comes from SDK `RuntimePackAsset` metadata filtered by target RID, native asset type, shim filename, runtime package ID, and a nonempty package version. These diagnostics describe validation predicates; they do not claim successful publication or native execution.

This separate original report preserves the earlier release validation review unchanged. Apply accepted rewrites separately.
