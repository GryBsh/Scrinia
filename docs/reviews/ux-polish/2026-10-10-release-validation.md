# UX Polish Review — Release validation

**Date:** 2026-10-10
**Scope:** Focused Task 14 operator copy packet; parameters, errors, diagnostics, completion messages, and workflow labels in `scripts/release/Validate-Release.ps1`, `tools/Scrinia.ReleaseTools/{Program,ReleaseArtifactManifest,ReleaseLifecycle,NativePayloadProbe}.cs`, and `.github/workflows/release-validation.yml`. Immediate claims checked against the current implementation and `global.json`; no broad code review or execution of validation lanes.
**Reviewer:** ux-polish-reviewer agent

The copy is calm and mostly precise; one baseline/candidate error needs correction, with four clarity improvements and one optional label refinement.

**Counts:** 1 Must fix · 4 Should fix · 1 Consider.

## Must fix

1. **Baseline handshake failures are mislabeled as candidate failures.** `tools/Scrinia.ReleaseTools/ReleaseLifecycle.cs:201`.
   `ExerciseHost` also invokes the handshake for `0.6.0-validation-baseline.1`; its expected `version` and `name` are parameters. The current error incorrectly identifies that fixture as the candidate.
   - Before: `Stdio ServerInfo differs from candidate.`
   - After: `Stdio server identity does not match expected name '{name}' and version '{version}'.`

## Should fix

1. **Argument errors give no usable command or option guidance.** `tools/Scrinia.ReleaseTools/Program.cs:6`, `:16`, `:19`.
   The parser already defines the supported commands and their exact option sets. Show those names so operators can correct a failed invocation without reading source. Preserve the existing requirement that all named options have values.
   - Before: `Expected command and paired named options.`
   - After: `Supply a command followed by option/value pairs. Commands: stage-embeddings, verify-artifacts, lifecycle, native-probe.`
   - Before: `Unknown release-tool command.`
   - After: `Unknown release-tool command '{args[0]}'. Commands: stage-embeddings, verify-artifacts, lifecycle, native-probe.`
   - Before: `Missing or unknown release-tool option.`
   - After: `Invalid options for '{args[0]}'. Required options: {string.Join(", ", names)}.`

2. **Package and installed-version errors omit the identity the operator needs.** `tools/Scrinia.ReleaseTools/ReleaseLifecycle.cs:69`, `:125`.
   Both checks have an expected version parameter. Include the package path or installed command output to distinguish the test-only baseline from stable `0.6.0` and locate the failed input.
   - Before: `Lifecycle package identity differs from exact candidate or test baseline.`
   - After: `Package '{package}' must have ID 'scrinia' and version '{version}'.`
   - Before: `Installed command version differs.`
   - After: `Command version mismatch: expected '{version}', received '{actual.Trim()}'.`

3. **Artifact success should name the checks it actually performs.** `tools/Scrinia.ReleaseTools/ReleaseArtifactManifest.cs:58`.
   This command checks manifest identity, the expected artifact set, file sizes, and SHA-256 consistency. It does not independently authenticate the publisher or establish native lifecycle success. Naming those checks makes the positive result easier to interpret without broadening its claim.
   - Before: `Verified seven release artifacts for {sha} / {version}.`
   - After: `Manifest identity, artifact set, sizes and SHA-256 hashes match for seven artifacts: commit {sha}, package version {version}.`

4. **Missing completion evidence is folded into an identity mismatch.** `.github/workflows/release-validation.yml:177`.
   The compound predicate includes a missing `complete.txt` alongside mismatched commit/version/SDK/RID/run identity. A missing record prevents verification; it does not establish that an identity differs. Separate the diagnostics while preserving the existing failure gate. This requires choosing the message for the failed predicate, not adding a new validation state or relaxing the gate.
   - Before: `Lane identity or completion evidence differs`
   - After, missing completion marker: `Required completion evidence is missing for lane '{laneDir.Name}'; this lane cannot be verified.`
   - After, identity mismatch: `Lane '{laneDir.Name}' identity does not match the requested commit, package version, SDK, run or RID.`

## Consider

1. **The workflow input label omits its lowercase requirement.** `.github/workflows/release-validation.yml:11`.
   The validation pattern accepts exactly 40 lowercase hexadecimal characters. Stating that requirement in the input label avoids an unnecessary first error.
   - Before: `Exact 40-character candidate commit SHA`
   - After: `Candidate commit SHA (40 lowercase hexadecimal characters)`

## Consistency notes

- Keep sentence case and calm, direct errors. Use `commit SHA`, `package version`, `SDK`, and `RID` for distinct identities; preserve exact parameter names and identifiers.
- Use `SHA-256` in prose; retain `SHA256`, `sha256`, and `CandidatePackageSha256` where they name APIs, fields, or parameters.
- The fixture is test-only `0.6.0-validation-baseline.1`; the stable candidate is `0.6.0`. Messages shared by both should use the supplied expected identity.
- Keep checksum consistency separate from authenticity. Keep missing evidence separate from confirmed failures and from success.
- Native probe output accurately says individual libraries loaded by absolute path. Plugin initialization alone does not establish native loading or inference. The completion messages occur after the required stage sequence; this copy review does not establish that any native lane passed.
- The SDK requirement matches `global.json` (`10.0.203`, prereleases disabled, roll-forward disabled). The XML documentation label explicitly discloses unconfigured coverage; neither label is release execution evidence.

This is the original review report. Apply accepted rewrites separately and retain this record unchanged.
