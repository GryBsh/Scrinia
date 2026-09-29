# Scoped SDD Re-review — Task 5

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `55bf547`  
**HEAD:** `aebb37d`  
**Scope:** Task 5 fixes for typed plugin callbacks, maintenance alias handling, and propagation tests.

## Files Reviewed

- `src/Scrinia.Core/IPluginStore.cs`
- `src/Scrinia.Core/MaintenanceEventSink.cs`
- `tests/Scrinia.Tests/MaintenanceEventSinkTests.cs`
- Task 5 requirements in the specified plan
- Complete `55bf547..aebb37d` fix delta

## Overall Assessment

**APPROVE**

The fix delta closes both findings from the prior Task 5 review. Maintenance now parses extracted
references into `MemoryAddress` values before comparison and dispatch, so `/temp/name` and `~name`
are treated as the same self-reference. The added theory covers both alias directions, and the
existing typed callback migration remains intact.

## Critical Issues

None identified at the required confidence threshold.

## Major Issues

None identified at the required confidence threshold.

## Minor Issues

None identified at the required confidence threshold.

## Positive Observations

- `TryParseReferenceAddress` gives maintenance one canonical parsing boundary while retaining the
  existing bare-name shorthand by retrying with a leading `/`.
- Self-reference comparison is now value-based over `MemoryAddress`, rather than a comparison of
  raw extracted text with a rendered canonical address.
- Ephemeral targets use `TryUpdateEphemeral`, while persistent targets continue through the scoped
  update path; the fix does not broaden physical storage responsibilities.
- `IPluginStore.ResolveAddress` now delegates persistent address decomposition to the store's
  existing parser instead of retaining a duplicate split-and-scope implementation.
- The new test explicitly exercises `/temp/probe` → `~probe` and `~probe` → `/temp/probe`, and
  verifies that neither alias creates a self-link.

## Verification Evidence

- Reviewed the complete `55bf547..aebb37d` diff and the Task 5 requirements in the plan.
- `git diff --check 55bf547 aebb37d`: passed.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MaintenanceEventSinkTests --no-restore`: passed (20/20).
- The fix delta is limited to the Task 5 core adapter and its focused test; this package adds
  review artifacts only, with no source or test implementation changes.

## Four-Layer Review

- **Correctness:** **PASS** — both ephemeral aliases parse to equal addresses before self-link
  checks, and target dispatch selects the appropriate persistent or ephemeral mutation path.
- **Security:** **PASS** — malformed extracted references are ignored by the fail-closed parser;
  no raw reference is passed to address resolution after parsing fails.
- **Performance:** **PASS** — each extracted reference is parsed once for dispatch and the change
  preserves the existing one-update-per-target behavior.
- **Style & maintainability:** **PASS** — address parsing and canonical identity are explicit,
  localized, and covered by a table-driven test.

## Philosophy Compliance

- Early exit: **PASS** — invalid references and self-references are skipped before resolution or
  mutation.
- Parse don't validate: **PASS** — extracted reference text is converted to `MemoryAddress` at the
  maintenance boundary and typed identity is used thereafter.
- Atomic predictability: **PASS** — persistent and ephemeral targets are updated through their
  respective store primitives without changing unrelated event behavior.
- Fail fast: **PASS** — malformed references do not reach store resolution or mutation.
- Intentional naming: **PASS** — `TryParseReferenceAddress`, `referencedAddress`, and
  `ResolveAddress` communicate the address flow directly.
