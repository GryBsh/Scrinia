# IMP-01 Entity Adjacency Memoization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reuse the two derived scoring adjacency maps while preserving exact entity-graph results for repeated calls and independent materialized stores.

**Architecture:** `EntityFactStore` owns a private memo containing both read-only maps. `ScoringAdjacency()` computes one complete pair on the first request and reuses that pair for equivalent repeated calls; separately constructed stores retain independent materialized facts. The existing `FileMemoryStore` invalidation path remains responsible for replacement/mutation freshness by constructing a fresh fact store. No public API, generation, replacement, invalidation, or test-control API is introduced; the only controlled derivation seam is internal and test-only.

**Refs:** `docs/superpowers/specs/2026-08-27-imp-01-entity-adjacency-memoization-design.md` (approved cache boundary); `src/Scrinia.Core/Search/EntityFactStore.cs` (existing constructor and `ScoringAdjacency()` contract).
ref: `docs/superpowers/specs/2026-08-27-imp-01-entity-adjacency-memoization-design.md`

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, `EntityFactStore`, `PersonalizedPageRank`.

## Global Constraints

- “No change to `IMemoryStore`, `EntityFactStore` public behavior, entity extraction, scoring weights, search ranking, persistence, sidecar format, or storage layout.”
- “The memo must not expose mutable dictionaries to callers.”
- “A failed adjacency derivation must not publish a partial pair of maps.”
- “Only behavior reachable through the existing constructor and `ScoringAdjacency()` contract is tested; FileMemoryStore freshness is tested through its existing mutation methods.”

---

## Dependencies / Order

- Depends on IMP-04 completing the entity-fact materializer/store integration first; use only the existing `EntityFactStore(IEnumerable<MemoryEntities>)` constructor and `ScoringAdjacency()` contract.
- Add no public failure-injection, generation, setter, replacement, invalidation, or cache-control API. Before any failure/concurrency test is written or run, define exactly one internal test seam: `internal delegate (IReadOnlyDictionary<string, IReadOnlyCollection<string>> EntityToDocs, IReadOnlyDictionary<string, IReadOnlyCollection<string>> DocToEntities) EntityAdjacencySource()`; the production constructor uses an internal production-default source that derives from the store's index, and an internal constructor accepts the delegate only for failure/concurrency tests. The red tests must compile against this boundary; no test may reference a type or overload introduced by a later task.
- Run Task 1 before Task 2 and before the release plans consume the focused entity tests. Task 1 establishes the seam and records red tests; it must not claim that memoization, concurrency, or failure-recovery behavior passes before Task 2 exists.
- “Independent store” means constructing a second store from a separately materialized fact sequence; it does not mean mutating or replacing facts through an unsupported API.
- **Ref:** `docs/superpowers/specs/2026-08-27-imp-01-entity-adjacency-memoization-design.md` (observable behavior and rejected public API expansion).

### Task 1: Add cache behavior tests

**Files:**
- Create: `tests/Scrinia.Tests/EntityFactAdjacencyMemoizationTests.cs`
- Modify: `src/Scrinia.Core/Search/EntityFactStore.cs` (the production file defining `EntityAdjacencySource` and its internal constructor seam)

**Interfaces:**
- Consumes: existing public constructor `EntityFactStore(IEnumerable<MemoryEntities>)` and `ScoringAdjacency()`.
- Produces: tests for repeated-call result preservation and independent state between separately materialized stores.

- [ ] **Step 1: Define the exact internal `EntityAdjacencySource` seam and minimal internal constructor setup** described in Dependencies / Order, before any test below is written or run. Do not alter the public constructor or `ScoringAdjacency()` signature.
- [ ] **Step 2: Add `ScoringAdjacency_RepeatedCalls_ReturnEquivalentReadOnlyMaps`** with two `MemoryEntities` sharing one entity and distinct scoring keys. Call the method twice; assert equal entity/doc key sets, equal collection contents, reciprocal entity↔document edges, and that casting returned maps/collections to mutable dictionary/set types is impossible (or mutation attempts through any exposed interface are unavailable).
- [ ] **Step 3: Add `FileMemoryStore_ReplacementAndMutation_InvalidateFactStoreThroughExistingPath`** using a temporary `FileMemoryStore`: write/upsert an entry with `topic:old`, warm `GetEntityFactStore()`, replace the same named entry through the existing `Upsert` path with `topic:new`, assert old is absent and new is present, then remove it through the existing `Remove` path and assert the new entity is absent. Do not call an unprovided replacement or invalidation API.
- [ ] **Step 4: Add `ScoringAdjacency_ConcurrentFirstAccess_PublishesOneCompletePair`** using the exact internal `EntityAdjacencySource` seam defined in Step 1: create a store with a source that increments an `Interlocked` counter, blocks the first derivation on a `ManualResetEventSlim`, and then releases it after multiple `Task.Run` callers have started. Await all callers, assert the source ran once, every result is equivalent, and every result contains the complete reciprocal pair.
- [ ] **Step 5: Add `ScoringAdjacency_FailedFirstDerivation_DoesNotPoisonRetry`** using the same seam: the source throws a specific `InvalidOperationException` on its first invocation and returns a complete pair on the second. Assert the first call rethrows the same exception instance, the second succeeds with both maps complete, and the source count is two, proving no partial pair was published and recovery is possible.
- [ ] **Step 6: Add `EntityGraphScoreContributor_CachedAdjacency_IsScorerEquivalent`** using the existing `EntityGraphTestFixture.StoreOf` fixture and `EntityGraphScoreContributor` defaults (`DefaultWeight = 50.0`, `DefaultAlpha = 0.15`, `DefaultIterations = 20`). Use exactly the existing candidate sequence from `EntityGraphScoreContributorTests.ComputeScores_LiftsAssociatedMemory_KeyedByScopeAndName`: `a = person:alice, org:acme`, `b = org:acme, person:bob`, and `c = person:carol`, with query `what did alice do`. The exact pre-memoization expected map is `{ ["s|a"] = 50.0, ["s|b"] = 29.3103431085 }`; assert exact key-set equality and exact value equality for every key with no tolerance, and assert the two post-memoization maps are exactly equivalent as well. Do not replace these literals with `BeApproximately`, a conditional tolerance, or a newly computed expected value.
- [ ] **Step 7: Run the new tests before implementation.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactAdjacencyMemoizationTests"`

Expected: the characterization tests that use only the existing public constructor and mutation paths pass; the new cache-reuse, concurrent-first-access, failed-derivation-retry, and scorer-equivalence tests fail because the memoization implementation is not present yet. The failure is expected and must be recorded as red. The internal source counter is permitted only because the approved failure/concurrency seam was defined before these tests; no public API or process-wide cache is required.

- [ ] **Step 8: Commit the tests.**

```bash
git add src/Scrinia.Core/Search/EntityFactStore.cs tests/Scrinia.Tests/EntityFactAdjacencyMemoizationTests.cs
git commit -m "test: specify entity adjacency memoization"
```

### Task 2: Implement atomic memoization

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityFactStore.cs:8-104`
- Test: `tests/Scrinia.Tests/EntityFactAdjacencyMemoizationTests.cs`

**Interfaces:**
- Consumes: the existing `_byEntity` fact index and `ScoringAdjacency()` return type.
- Produces: unchanged `ScoringAdjacency() : (IReadOnlyDictionary<string,IReadOnlyCollection<string>> EntityToDocs, IReadOnlyDictionary<string,IReadOnlyCollection<string>> DocToEntities)` with cached immutable/read-only maps.

**Ref:** `src/Scrinia.Core/Search/EntityFactStore.cs`; `docs/superpowers/specs/2026-08-27-imp-01-entity-adjacency-memoization-design.md` (atomic publication and read-only exposure).

- [ ] **Step 1: Add a private cached pair field** initialized empty; do not expose it or alter constructor/public signatures.
- [ ] **Step 2: Move current map construction into one local derivation method** that builds both dictionaries completely before returning, then wraps each value collection and dictionary in read-only views.
- [ ] **Step 3: Publish the pair in one assignment** only after both maps are complete; on an exception leave the cached field empty and rethrow the original exception.
- [ ] **Step 4: Use the exact internal `EntityAdjacencySource` seam and production default** defined in Task 1; keep the public constructor and `ScoringAdjacency()` signatures unchanged.
- [ ] **Step 5: Guard first publication** with the smallest synchronization primitive that prevents duplicate publication; do not introduce a process-wide cache or public cache-control surface.
- [ ] **Step 6: Run focused tests after the implementation is complete.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactAdjacencyMemoizationTests"`

Expected: PASS; repeated calls reuse complete maps, concurrent first access publishes one complete pair, failed derivation leaves the memo retryable, the exact scorer map is `{ ["s|a"] = 50.0, ["s|b"] = 29.3103431085 }`, and callers cannot mutate the returned maps or collections.

- [ ] **Step 7: Commit implementation.**

```bash
git add src/Scrinia.Core/Search/EntityFactStore.cs tests/Scrinia.Tests/EntityFactAdjacencyMemoizationTests.cs
git commit -m "perf: memoize entity scoring adjacency"
```

## Self-review

- Spec coverage: Tasks 1–2 cover repeated equivalent adjacency results, FileMemoryStore replacement/mutation invalidation, concurrent first access, failure recovery through the exact internal source seam, read-only exposure, reciprocal-edge preservation, and scorer equivalence without public API expansion.
- Placeholder scan: no vague error-handling or deferred implementation language appears.
- Type consistency: public tests target the unchanged `EntityFactStore(IEnumerable<MemoryEntities>)` constructor and `ScoringAdjacency()` tuple; only the explicitly internal `EntityAdjacencySource()` constructor overload is used for controlled failure/concurrency cases.
