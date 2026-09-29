# Scoped SDD Re-review — Task 6

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `41ca7a4`  
**HEAD:** `87fe2d3`  
**Scope:** Task 6 MCP tools, recall form detection, resources, and typed address propagation.

## Files Reviewed

- `src/Scrinia.Core/Models/MemoryAddress.cs`
- `src/Scrinia.Mcp/MemoryTools.cs`
- `src/Scrinia.Mcp/MemoryTools.Core.cs`
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`
- `src/Scrinia.Mcp/ScriniaMcpResources.cs`
- `tests/Scrinia.Tests/McpAddressSurfaceTests.cs`
- Task 6 requirements in the specified plan
- Complete `41ca7a4..87fe2d3` fix delta

## Overall Assessment

**REQUEST_CHANGES**

The commit completes several internal typed-call migrations and preserves trimmed inline NMP/2
recall, but ordinary logical-address whitespace handling is broken and the public contract still
depends on an implicit raw-string conversion. The Task 6 surface test remains an inventory plus
recall-focused check rather than the required per-operation policy matrix.

## Findings

### Major issues

#### T6-R001 — Whitespace-padded logical addresses are misclassified as malformed

**Confidence:** 100%  
**Location:** `src/Scrinia.Mcp/MemoryTools.cs:39-66` (HEAD); `tests/Scrinia.Tests/McpAddressSurfaceTests.cs:54-61`

`TryParseLogicalAddress` passes `raw` directly to `CanonicalizeEphemeralAlias` and prepends `/`
before parsing. Unlike the inline-artifact path, it does not trim first. For the existing test input
`" /ordinary-name "`, this produces an invalid address instead of `/ordinary-name`, so the method
returns `MALFORMED_ADDRESS` rather than reaching the store and returning the expected `NOT_FOUND`.
Trim once at this adapter boundary before URI detection and alias canonicalization, then pass the
canonical typed address to the internal operation.

#### T6-R002 — Implicit string conversion keeps a second, non-canonical construction path

**Confidence:** 95%  
**Location:** `src/Scrinia.Core/Models/MemoryAddress.cs:42-49` (HEAD); typed calls in
`src/Scrinia.Mcp/MemoryTools.Core.cs`

The public `implicit operator MemoryAddress(string raw)` constructs addresses by applying its own
trim/alias/prefix logic before calling `Parse`. MCP facades parse into `MemoryAddress`, convert back
to a canonical string, and then rely on this operator when calling typed internal methods. This
violates Task 6's parse-once boundary and the design contract that `MemoryAddress` is the only
supported address-construction path through its explicit factories; it also permits production
code to silently reintroduce raw-string address flow. Remove the implicit conversion and pass the
already parsed value through each internal call.

### Minor issues

#### T6-R003 — Surface coverage does not prove the required policy matrix

**Confidence:** 100%  
**Location:** `tests/Scrinia.Tests/McpAddressSurfaceTests.cs:10-72` (HEAD)

The test enumerates tool names and exercises recall plus one resource rejection, but does not run
the required matrix against remember, append, forget, compact, link, or both resource transport
registrations. It also does not assert `/temp/probe` and `~probe` equivalence, persistent success,
forbidden-operation no-mutation behavior, or categorized malformed-address results for every
address-bearing boundary. Expand the test by reflecting address-bearing methods/registrations and
driving the applicable cases through each operation. Repository inspection confirms there is no
`AgentLoop.cs` or `recall_memory` surface in this head.

## Positive Observations

- Recall now classifies URI-shaped and artifact-shaped inputs before store lookup and retains
  store-independent valid inline NMP/2 decoding.
- The trimmed inline-artifact regression identified in the previous Task 6 review is covered by
  `Recall_TrimsOnlyInlineArtifactForm` and handled by the new raw-input overload.
- Resource parsing occurs before the missing-store guard, allowing unsupported URI input to retain
  its categorized MCP error.
- Internal remember, append, forget, compact, and link helpers now expose typed address parameters.

## Verification Evidence

- Reviewed the complete `41ca7a4..87fe2d3` diff and Task 6 requirements in the plan.
- `git diff --check 41ca7a4 87fe2d3`: passed.
- HEAD contains the existing whitespace logical-address test whose expected `NOT_FOUND` result is
  inconsistent with the current untrimmed helper path.
- The package adds review artifacts only; source and test implementations were not modified.

## Four-Layer Review

- **Correctness:** **FAIL** — whitespace-padded logical input returns the wrong category; required
  adapter behavior is not fully tested.
- **Security:** **PASS** — URI-shaped and malformed artifact forms are rejected before store lookup;
  no physical URI resolution was reintroduced in the reviewed MCP files.
- **Performance:** **PASS** — form classification is local and rejected inputs avoid store access.
- **Style & maintainability:** **PARTIAL** — naming and central classification are clear, but the
  implicit conversion and duplicated parse/canonicalize route obscure the boundary contract.

## Philosophy Compliance

- Early exit: **PARTIAL** — rejected forms exit early, but valid whitespace-padded addresses exit
  with the wrong result.
- Parse don't validate: **FAIL** — raw strings are converted implicitly after facade parsing rather
  than preserving the parsed value through the operation path.
- Atomic predictability: **FAIL** — equivalent whitespace-decorated logical inputs do not behave as
  equivalent addresses.
- Fail fast: **PASS** for URI/artifact rejection; **FAIL** for the incorrect malformed classification.
- Intentional naming: **PASS** — the form detector and typed helper names communicate intent.
