# Scoped SDD Re-review — Task 9

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `f641a54`  
**HEAD:** `bfbc985`  
**Scope:** Task 9 source-generated JSON registration and serialization compatibility fix delta, plus the Task 9 contract.

## Files Reviewed

- `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs`
- Task 9 requirements in the specified plan
- Complete `f641a54..bfbc985` fix delta
- Production source-generated context registrations at `f641a54`
- `ResponseBuilder` error construction and YAML rendering at `f641a54`

## Overall Assessment

**REQUEST_CHANGES**

The fix correctly narrows the registration assertion to context-owned contracts and adds coverage through
the private durable-store serialization path. However, its new categorized-error test does not test MCP
category preservation: it constructs an error with one category and deliberately emits an unrelated,
hard-coded MCP error code. The test can therefore pass while the adapter maps the category incorrectly.

## Findings

### Major issues

#### T9-R001 — Categorized-error test hard-codes the wrong MCP category

**Confidence:** 100%  
**Location:** `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs:145-159` (HEAD)

The test creates a `MemoryError` with category `ForbiddenAddressKind`, verifies that category only after a
Bundle JSON round trip, and then calls `ResponseBuilder.Error(error.Message, ErrorCodes.UnsupportedInputForm)`.
The MCP assertion consequently proves only that `ResponseBuilder` renders the explicitly supplied
`UNSUPPORTED_INPUT_FORM` string. It does not exercise or assert any production mapping from
`MemoryErrorCategory.ForbiddenAddressKind` to the MCP surface. A regression that maps forbidden ephemeral
addresses to `UNSUPPORTED_INPUT_FORM`, `INTERNAL`, or any other category would still pass this test.

Route the `MemoryError` through the actual MCP translation helper used by the tools, or assert the expected
category/code mapping directly for the production adapter. Cover at least `ForbiddenAddressKind`,
`MalformedAddress`, `UnsupportedInputForm`, and `InvalidWriteContent`, and retain the serialized core/CLI
assertions separately so each boundary is tested rather than manually reconstructed in the test.

### Minor issues

#### T9-R002 — Context coverage remains incomplete for typed contracts

**Confidence:** 90%  
**Location:** `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs:27-33, 90-113, 162-169` (HEAD)

The ownership assertion checks only `ArtifactEntry` and `MemoryAddress` for the MCP context, only
`MemoryAddress` for the CLI context, and only the pre-existing dictionary payload for the Config and plugin
contexts. The typed request/error/write round trip then runs only against `BundleJsonContext` and
`CliJsonContext`. Thus the suite does not prove that the other production contexts' registered
`MemoryWrite`, `MemoryError`, `MemorySearchRequest`, `MemoryListRequest`, and `ArtifactEntry` metadata can
actually round-trip, despite those types being registered in the fix-base contexts and named in Task 9's
trim-safe contract.

Define the expected set per context based on its real callers and round-trip every type in each applicable
set. If Config and Plugin contexts intentionally need only their dictionary payloads, state that explicitly;
otherwise the test should not imply that registration alone establishes serialization compatibility.

## Positive Observations

- The prior blanket assertion over all Core contracts was replaced with context-specific ownership checks,
  avoiding the earlier false failure caused by purpose-specific contexts.
- The new FileMemoryStore round trip reaches the private durable `FileStoreJsonContext` through the real
  write/read path and checks the typed address, opaque artifact key, and preview.
- The address tests continue to cover persistent and both ephemeral spellings, canonicalization, and
  rejection of legacy `file://` input through generated metadata.
- Artifact and bundle assertions explicitly reject legacy `uri` and `artifactUri` JSON properties.

## Verification Evidence

- Reviewed the complete `f641a54..bfbc985` diff and Task 9 requirements in the specified plan.
- Inspected the production context registrations and `ResponseBuilder` implementation at the reviewed
  fix base to distinguish generated metadata coverage from adapter error translation.
- `git diff --check f641a54 bfbc985`: passed.
- This package adds review artifacts only; source and test implementations were not modified.

## Four-Layer Review

- **Correctness:** **FAIL** — T9-R001 leaves the MCP categorized-error acceptance claim untested; T9-R002
  leaves several registered typed contracts without round-trip coverage.
- **Security:** **PASS** — the fix retains generated metadata and does not add reflection fallback,
  filesystem URI resolution, or unsafe raw-input handling.
- **Performance:** **PASS** — the added assertions and store round trip introduce no production scans,
  caching changes, or new serialization fallback.
- **Style & maintainability:** **PARTIAL** — context ownership is clearer and the durable-path test is
  valuable, but the categorized-error test manually rebuilds an adapter result and can mislead future
  maintainers about MCP error coverage.

## Philosophy Compliance

- Early exit: **PASS** — address converter failures remain rejected at the generated JSON boundary.
- Parse don't validate: **PASS** — the fix continues to use the canonical address converter rather than
  duplicating address parsing in the tests.
- Atomic predictability: **PARTIAL** — generated metadata and durable round trips are deterministic, but
  the MCP assertion does not represent the production category-mapping operation.
- Fail fast: **FAIL** for T9-R001 — the test fails to fail when MCP category translation regresses.
- Intentional naming: **PARTIAL** — `Categorized_error_serialization_preserves_category_in_core_and_cli_surfaces`
  overstates its MCP coverage because the MCP portion hard-codes an unrelated code.
