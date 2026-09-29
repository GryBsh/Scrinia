# Task 3 Scoped SDD Re-review

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `a38d822`  
**HEAD:** `2842acc7c9900aa0caa7ad0a5d4c77bb6af4b104`  
**Scope:** Task 3 contract/model migration and the fix delta between FIX_BASE and HEAD.

## Files Reviewed

- `src/Scrinia.Core/Models/ArtifactEntry.cs`
- `src/Scrinia.Core/Models/ScopedArtifact.cs`
- `src/Scrinia.Core/Models/MemorySearchRequest.cs`
- `src/Scrinia.Core/Search/SearchQuery.cs`
- `src/Scrinia.Core/IMemoryStore.cs`
- `src/Scrinia.Core/IPluginStore.cs`
- `src/Scrinia.Core/IMemoryEventSink.cs`
- `tests/Scrinia.Tests/ContractTypingTests.cs`
- the complete `a38d822..2842acc7c9900aa0caa7ad0a5d4c77bb6af4b104` fix delta

## Overall Assessment

**APPROVE**

The reviewed fix delta closes the outstanding Task 3 contract gaps. Typed CRUD and callback
members are now required implementations, executable raw callback/reflection bridges are removed,
typed search/list filters propagate into store operations, and `ArtifactEntry` exposes the logical
address plus opaque artifact key without a public URI/name compatibility surface.

## Re-review of Prior Findings

### M1 — Raw-address compatibility bridges bypassed the typed contract

**Status: ADDRESSED**

`IMemoryEventSink` now declares only abstract callbacks receiving `MemoryAddress`, and the
reflection/string callback bridge plus string extension overloads are gone. `IPluginStore` now
requires `ReadArtifactAsync(MemoryAddress, ...)`; the legacy reflection resolver is removed.

### M2 — Typed CRUD defaults silently discarded or misrouted addresses

**Status: ADDRESSED**

`IMemoryStore.ReadAsync`, `WriteAsync`, and `DeleteAsync` are abstract contract members. The
`FileMemoryStore` implementation verifies that the write payload address matches the requested
address and routes read/write/delete through the canonical address. Focused tests assert both
abstractness and persistent CRUD round-tripping.

### m1 — Contract test did not exercise typed filter propagation

**Status: ADDRESSED**

The typed request adapter copies `MemorySearchRequest.Address` into `SearchQuery`, while listing
applies `MemoryListRequest.Prefix` with kind-aware exact/descendant matching. Tests cover matching
descendants, prefix collisions, and ephemeral exact identity.

## Findings

No Critical, Major, Minor, or Nitpick findings met the review confidence threshold in this scoped
re-review.

## Positive Observations

- `ArtifactEntry` uses `MemoryAddress` for logical identity and validates `ArtifactKey` as opaque,
  store-relative data.
- Event sink and plugin-store public contracts now require typed addresses rather than converting
  back to strings at the boundary.
- Persistent prefix matching is boundary-aware, while ephemeral addresses retain exact identity
  semantics.
- The fix removes compatibility behavior rather than leaving silent defaults that appear to work.

## Verification Evidence

- Reviewed the complete `a38d822..2842acc7c9900aa0caa7ad0a5d4c77bb6af4b104` diff against Task 3 of
  the specified plan.
- `git diff --check a38d822 2842acc7c9900aa0caa7ad0a5d4c77bb6af4b104`: passed.
- The Task 3 fix delta is limited to the core contract declarations and focused contract tests;
  this package adds review artifacts only and does not modify source or test files.

## Philosophy Check

- Early exit: PASS for the typed store address validation and address-prefix filtering paths.
- Parse don't validate: PASS; public callback and request boundaries use `MemoryAddress`.
- Atomic predictability: PASS; typed request filters are forwarded and applied rather than
  accepted and discarded.
- Fail fast: PASS; typed contract members are required and mismatched write addresses throw.
- Intentional naming: PASS; `Address` and `ArtifactKey` clearly distinguish logical identity from
  physical artifact identity.
