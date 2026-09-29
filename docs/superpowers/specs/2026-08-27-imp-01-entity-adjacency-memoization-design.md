# IMP-01 — Entity adjacency memoization design

**Date:** 2026-08-27  
**Status:** Approved design  
**Release relationship:** 0.6.0 improvement; public APIs and storage behavior remain unchanged

## Goal

Avoid rebuilding entity-scoring adjacency maps on every search while preserving the exact
results, ordering inputs, scope behavior, and invalidation semantics of the current entity graph.

## Scope

- Memoize the two derived adjacency maps returned by `EntityFactStore.ScoringAdjacency()`.
- Make cache ownership and invalidation explicit at the existing fact-store boundary.
- Cover cache reuse, replacement, mutation, and query-equivalence behavior.

## Non-scope

- No change to `IMemoryStore`, `EntityFactStore` public behavior, entity extraction, scoring
  weights, search ranking, persistence, sidecar format, or storage layout.
- No broad `FileMemoryStore` rewrite and no new process-wide cache.
- No change to the authoritative entity data: the materialized facts remain the source from which
  adjacency is derived.

## Architecture and data flow

`EntityGraphScoreContributor` continues to request adjacency from `EntityFactStore`. The fact store
holds a private memoized value containing both derived maps. The first request derives the maps from
the current fact index and stores them. Later requests return the same immutable/read-only result
until the fact index is replaced or otherwise invalidated. Replacement clears the memo before the
new index becomes observable, so a query cannot combine old adjacency with new facts.

The flow is:

1. `EntityFactMaterializer` or the existing derivation boundary produces the current fact store.
2. The fact store installs the new fact index and clears adjacency memoization.
3. `EntityGraphScoreContributor` calls `ScoringAdjacency()`.
4. The first call derives both maps; subsequent calls reuse them.
5. A later fact replacement repeats the invalidation and derivation cycle.

The memo must not expose mutable dictionaries to callers. If the current scorer requires dictionary
interfaces, the returned dictionaries are read-only views or are treated as immutable by contract.

## Error and risk handling

- A failed adjacency derivation must not publish a partial pair of maps; the memo remains empty and
  the next call may retry from the current facts.
- Concurrent first callers must converge on one valid current-generation result. An older derivation
  must not overwrite a memo that was installed after a fact replacement.
- Cache invalidation is required for every fact-index replacement, including filtered or rebuilt
  stores where the method is invoked on a newly created instance.
- If an existing diagnostic path reports derivation failure, preserve its exception and logging
  behavior; memoization must not turn a failure into an empty graph.

## Tests and acceptance criteria

- Repeated `ScoringAdjacency()` calls reuse the memoized pair without changing either map.
- Replacing the fact index causes the next call to derive from the replacement, not the old index.
- A mutation followed by scoring never returns an edge that only existed in the prior index.
- Concurrent first access returns equivalent maps and does not publish a partial result.
- A forced derivation failure leaves no poisoned memo and succeeds on a later valid attempt.
- `EntityGraphScoreContributor` scores a fixed corpus byte-for-byte/effect-for-effect equivalently
  before and after memoization, including empty, single-node, shared-entity, and multi-scope cases.
- Tests prove no public API, serialized sidecar, artifact, or index output changes.

## Affected files

- `src/Scrinia.Core/Search/EntityFactStore.cs` — memo ownership and invalidation.
- `src/Scrinia.Core/Search/EntityGraphScoreContributor.cs` — consumer characterization only if
  required to make the ownership contract explicit.
- `src/Scrinia.Core/Search/EntityFactMaterializer.cs` — integration point if introduced by IMP-04.
- `tests/Scrinia.Tests/` — focused fact-store and scorer equivalence tests.

## Dependencies

- IMP-04 defines the bounded `EntityFactMaterializer` extraction seam. IMP-01 may consume that seam,
  but adjacency memoization must remain independently testable.
- Existing entity derivation, attribute extraction, and search scoring contracts are authoritative.

## Explicit no-placeholder content

This design contains no deferred decisions, placeholder paths, or unspecified acceptance cases.
Implementation must preserve the current public APIs and storage behavior exactly.
