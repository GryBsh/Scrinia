# Scoped SDD Re-review — Task 6

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `3a822e8`  
**HEAD:** `af16bf1`  
**Scope:** Task 6 fixes for MCP recall form detection, resource validation, and address-surface coverage.

## Files Reviewed

- `src/Scrinia.Mcp/MemoryTools.cs`
- `src/Scrinia.Mcp/ScriniaMcpResources.cs`
- `tests/Scrinia.Tests/McpAddressSurfaceTests.cs`
- Task 6 requirements in the specified plan
- Complete `3a822e8..af16bf1` fix delta

## Overall Assessment

**REQUEST_CHANGES**

The fix correctly rejects the previously misclassified non-NMP artifact forms and moves resource
address validation ahead of store lookup. However, it regresses the existing trimmed inline-recall
behavior and the new test still does not provide the exhaustive per-surface policy coverage required
by Task 6.

## Findings

### Major issues

#### T6-R001 — Leading whitespace causes valid inline NMP/2 recall to be rejected

**Confidence:** 95%

**Location:** `src/Scrinia.Mcp/MemoryTools.cs:82-111` (HEAD)

`TryClassifyArtifactInput` examines the untrimmed input. Its NMP/2 check therefore fails when the
artifact is preceded by allowed surrounding whitespace, while the prior `TryParseInlineRecall`
implementation used `TrimStart()` before detecting the NMP/2 header. A normal generated artifact
contains line breaks, so the broad payload check then classifies the whitespace-prefixed artifact as
an unsupported artifact rather than decoding it. This is a behavioral regression in the fix delta.

Trim the input once at the form-detection boundary, preserve the trimmed artifact for decoding, and
retain `UnsupportedInputForm` for genuinely incomplete or unsupported artifact forms. Add a focused
test for a valid NMP/2 artifact with leading whitespace.

### Minor issues

#### T6-R002 — Surface test remains an inventory check, not exhaustive policy coverage

**Confidence:** 100%

**Location:** `tests/Scrinia.Tests/McpAddressSurfaceTests.cs:10-72` (HEAD)

The added test enumerates tool method names and checks one resource method exists, but it does not
exercise every address-bearing tool/resource method as required by Task 6 step 1. It has no table-
driven coverage for remember, append, forget, compact, or link; no persistent-success and ephemeral
policy cases; no `/temp/probe` versus `~probe` equivalence checks; and no verification of both resource
transport registrations. The test therefore cannot prove that all adapter boundaries preserve the
canonicalization, categorized-error, and no-mutation invariant.

Extend the test inventory to enumerate address-bearing parameters and resource registrations, then
run the required input matrix against each applicable operation. Keep the repository audit explicit:
there is no `AgentLoop.cs` or `recall_memory` surface in this head.

## Positive Observations

- `TryClassifyArtifactInput` now catches known non-NMP headers, malformed NMP/2, and artifact-shaped
  payloads before logical-address parsing or store resolution.
- Valid inline NMP/2 recall remains store-independent and is covered by a focused success test.
- Resource parsing now occurs before the missing-store guard, so unsupported URI input receives the
  intended categorized MCP exception without requiring a configured store.
- The surface test explicitly records the current MCP tool inventory and confirms that the reviewed
  branch has no chat-agent recall method to add.

## Verification Evidence

- Reviewed the complete `3a822e8..af16bf1` diff and the Task 6 requirements in the plan.
- `git diff --check 3a822e8 af16bf1`: passed.
- The fix delta changes only the MCP implementation/resource files and adds the Task 6 surface test;
  this package adds review artifacts only and does not modify source or test implementations.
- Repository inspection confirms no `AgentLoop.cs` or `recall_memory` surface is present in the
  reviewed head.

## Four-Layer Review

- **Correctness:** **FAIL** — leading-whitespace inline artifacts regress from successful decoding to
  unsupported-input rejection; exhaustive adapter behavior is not proven.
- **Security:** **PASS** — URI-shaped and malformed artifact inputs are rejected before store access,
  and no unsafe raw-input echo was introduced by the fix.
- **Performance:** **PASS** — classification is local and store-independent; no new repeated store
  lookup was introduced for rejected forms.
- **Style & maintainability:** **PARTIAL** — the detector is centralized and named clearly, but the
  broad heuristic and incomplete surface matrix leave important behavior implicit.

## Philosophy Compliance

- Early exit: **PASS** for rejected artifact forms; **FAIL** for valid whitespace-prefixed inline
  artifacts because the detector exits with the wrong classification.
- Parse don't validate: **PARTIAL** — artifact-shaped input is classified at the boundary, but the
  boundary does not normalize the same surrounding whitespace accepted by the prior parser.
- Atomic predictability: **PARTIAL** — ordinary inputs are deterministic, while equivalent whitespace-
  decorated inline artifacts produce a different result.
- Fail fast: **PASS** for unsupported URI/artifact forms; **FAIL** for the regression in valid inline
  artifact handling.
- Intentional naming: **PASS** — `TryClassifyArtifactInput`, `TryParseLogicalAddress`, and the
  categorized error code communicate the intended flow.
