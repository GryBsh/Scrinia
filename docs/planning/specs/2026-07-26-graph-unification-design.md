# Design: Graph unification — PPR over the persisted store (Piece 5)

**Status:** Approved (design)
**Date:** 2026-07-26
**Scope:** Retire the per-query `EntityGraph` rebuild in production: run the entity-graph PPR signal
over the persisted `EntityFactStore`'s adjacency instead. Optimization + dedup, **not a new
capability** — the signal's meaning (seeds, α, iterations, weight, normalization) is unchanged, and
the final search ranking must be preserved. This is the last of the five pieces, sequenced here
because it reaches into Piece 1's data model and touches the scoring hot path.

## Motivation

`EntityGraphScoreContributor` rebuilds a bipartite `EntityGraph` from the query's candidates on
**every search** — re-parsing every candidate's keywords/attributes into `type:value` nodes, then
running PPR. But `EntityFactStore` (Piece 1) already persists exactly that adjacency: it keys on
`EntityRef(key,value)` — identical node identity — and derives entities from the **same**
`ParseEntityKeywords ∪ ParseEntityAttributes` union. The graph and the store are two materializations
of one structure. Piece 5 collapses them: production runs PPR over the store's adjacency, so the
common (unfiltered) query does zero rebuild and there is one adjacency, not two.

## The five pieces (where this sits)

1-4. EntityFactStore / hybrid extraction / auto-surfacing / explicit `facts` tool — **done, merged.**
5. **Graph unification — this spec.**

## Design decisions (settled in brainstorming)

- **PPR over an `EntityFactStore`, selected at the call site; retire the production graph.** The
  contributor is *blind* — it only ever receives a `candidates` array and cannot tell an unfiltered
  query from a scoped/topic-excluded one. So the **caller** (which knows `scopes`/`excludeTopics`)
  selects the store and hands the contributor a ready `EntityFactStore`:
  - fully unfiltered (`scopes==null && excludeTopics==null`) → the cached cross-scope
    `GetEntityFactStore()` (no rebuild — the hot-path win);
  - otherwise → `EntityFactDerivation.BuildStore(candidates)` — a scoped store over the **same
    candidate universe** the caller already built (`ListScoped(scopes[, excludeTopics])`).

  This is deliberate: the narrower universe of a filtered query is load-bearing. A scoped search
  confines associations to that scope; `excludeTopics` keeps excluded memories out of the diffusion
  entirely (a documented invariant — *"excluded topics don't influence scoring"*). Globalizing to the
  full store for those cases would route PPR mass through memories that must not participate — a
  **ranking change**. So the cache fast-path is unfiltered-**only**, and there `candidates` *is* the
  whole store by construction, so reusing the cache is both free correctness and the win.
  `EntityGraph.Build`/`Score` are no longer called in production. Chosen over caching a second graph
  structure or merely de-duplicating the parse.
- **One identity, two derived presentations (root-cause fix for the doc-key mismatch).** The scorer's
  join key is `"{scope}|{name}"` (ephemeral, shared by the entity-graph / path-segment / embeddings
  contributors and `GetSupplemental`). The fact store's `MemoryId` is the recall-able qualified name
  (`FormatRoundTripQualifiedName`) — and is deliberately user-facing (Piece 4 timelines/intersections
  return it to `recall`). Rather than translate between them, `MemoryEntities` stores the raw
  `(scope, subject)` identity and derives **both**: `MemoryId` (qualified name, unchanged value) and
  `ScoringKey` (`"{scope}|{name}"`). The store's adjacency is then natively keyed by `ScoringKey`, so
  PPR-over-store lines up with the scorer with **no translation**, and no other contributor changes.
- **Ranking-preserving, not byte-identical.** PPR is floating-point; the bar is scores equal within a
  small FP tolerance and **identical final ranking** — the same bar Piece 3 used. Guarded by an
  explicit equivalence test (old graph path vs new store path) and an end-to-end ranking test.
- **`EntityGraph` stays.** The benchmark harness and the equivalence test use `EntityGraph.Build`/
  `Score`; production simply stops calling them. The dedup is that production has one adjacency.

## Verified preconditions

- `FormatRoundTripQualifiedName` is a single default interface method on `IMemoryStore` with **no
  overrides** anywhere (even `FileMemoryStore` invokes it via `(IMemoryStore)this`). Its logic is
  pure, so the qualified name derives cleanly from `(scope, subject)` without a store instance.
- `EntityFactSidecar.TryLoad` rejects any `dto.Version != Version`, so bumping `Version` 1→2
  auto-invalidates and rebuilds every existing `facts.json` — no hand migration (Piece 1's design:
  the sidecar is a rebuildable cache, never a competing source of truth).
- The only production consumer of the built graph is `EntityGraphScoreContributor` (one call site).

## Components & boundaries

### 1. Pure qualified-name helper
Extract the pure body of `IMemoryStore.FormatRoundTripQualifiedName` into a static
(e.g. `MemoryNaming.FormatRoundTrip(scope, subject)`); the interface default delegates to it (behavior
unchanged). Lets `MemoryEntities` derive `MemoryId` without a store.

### 2. `MemoryEntities` — raw identity + derived presentations
```csharp
public sealed record MemoryEntities(
    string Scope, string Subject, DateTimeOffset Date, IReadOnlyList<EntityRef> Entities)
{
    public string MemoryId   => MemoryNaming.FormatRoundTrip(Scope, Subject);  // recall-able, == today's value
    public string ScoringKey => $"{Scope}|{Subject}";                          // the scorer's join key
}
```
`EntityFact` keeps its `MemoryId` field (user-facing, from `MemoryEntities.MemoryId`) — Piece 3/4
output is unchanged. Scoring never uses `EntityFact`; it uses `ScoringKey` off the adjacency.

### 3. `EntityFactDerivation.Derive(ScopedArtifact)` — drop the `memoryId` parameter
Compute identity from `item.Scope` / `item.Entry.Name`; return `new MemoryEntities(scope, subject, …)`.
Simplifies its callers (`IMemoryStore` default, `FileMemoryStore`, `MemoryTools.Core`,
`MemoryTools.Facts`) — they stop threading `FormatRoundTripQualifiedName(...)` in.

### 4. `EntityFactSidecar` — format bump to v2
Serialize/deserialize `(Scope, Subject)` instead of the pre-formatted `MemoryId`. `Version = 2`. Old
v1 sidecars fail the version check and rebuild automatically. `ComputeToken`'s per-entry id stays a
stable identifier (use `ScoringKey`) — validity semantics unchanged.

### 5. `PersonalizedPageRank` — extract the walk as a pure function
```csharp
// entityToDocs: entity node -> doc keys ; docToEntities: doc key -> entity nodes ; seeds: entity nodes.
public static class PersonalizedPageRank
{
    public static IReadOnlyDictionary<string, double> Score(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> entityToDocs,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> docToEntities,
        IReadOnlyCollection<string> seeds, double alpha, int iterations);   // -> docKey -> mass
}
```
`EntityGraph.Score` refactors to: seed via `EntityQuerySeeder`, then delegate to
`PersonalizedPageRank.Score(_entityToDocs, _docToEntities, seeds, α, iterations)`. Behavior-preserving;
existing `EntityGraphTests` are the parity guard (the Piece 1 `EntityQuerySeeder` extraction pattern).

### 6. `EntityFactStore` — expose its adjacency for scoring
Add a method that projects the store into the two maps `PersonalizedPageRank.Score` consumes, with
entity nodes as `"{key}:{value}"` (matching the graph's `type:value` node identity) and doc keys as
each memory's `ScoringKey`. Reuses the store's existing indices; adds no new derivation.

### 7. `EntityFactDerivation.BuildStore(items)` — one shared derive→store primitive
Extract the `items.Select(Derive).Where(non-null).Select → new EntityFactStore` build (currently
duplicated in `IMemoryStore.GetEntityFactStore` default, `MemoryTools.Core.BuildEntityFacts`, and
`MemoryTools.Facts.SelectFactStore` — Piece 4's deferred T4) into one static:
```csharp
public static EntityFactStore BuildStore(IEnumerable<ScopedArtifact> items) =>
    new(items.Select(Derive).Where(m => m is not null).Select(m => m!));
```
Post-component-3 `Derive(ScopedArtifact)` takes no `memoryId`, so `BuildStore` needs no `store`. It
folds the three existing duplicates and is the filtered entity-graph path's builder (5th caller).
`FileMemoryStore`'s per-scope sidecar loop keeps its own `Select(Derive)` (it needs the
`MemoryEntities` list to serialize, not a store) but is simplified by the same `Derive` signature drop.

### 8. `EntityGraphScoreContributor` — store-backed PPR + call-site store selection
The contributor is handed a ready `EntityFactStore` and runs PPR over it — no `EntityGraph.Build`, no
`candidates` parse:
```csharp
public IReadOnlyDictionary<string, double>? Score(EntityFactStore factStore, string query)
// (entityToDocs, docToEntities) = factStore.ScoringAdjacency()   // entities "key:value", docs ScoringKey
// seeds = EntityQuerySeeder.Seed(query, entityToDocs.Keys)
// ppr   = PersonalizedPageRank.Score(entityToDocs, docToEntities, seeds, α, iters)  // keyed by ScoringKey
// -> None short-circuits (no seeds / empty / max<=0) -> max-normalize × weight -> "{scope}|{name}" map
```
The normalization/scaling (`× DefaultWeight`, max-normalize) and every `None` short-circuit are copied
verbatim; α/iterations/weight constants unchanged.

- **`EntityGraphScoreContext`** is retyped from `ISearchScoreContributor?` to the concrete
  `EntityGraphScoreContributor?` (it is a dedicated channel — only ever set to
  `new EntityGraphScoreContributor()`), so callers can invoke `Score(store, query)`.
- **The two production call sites** (`PluginPipeline.SearchAsync`, `MemoryTools.Core` search) select
  the store: `unfiltered ? store.GetEntityFactStore() : EntityFactDerivation.BuildStore(candidates)`,
  then call `Score(...)`. `candidates` is still materialized (embeddings + auto-surface need it); the
  entity-graph signal just skips re-deriving it when the cache applies. Unfiltered predicate:
  `scopes==null` on the REST path (no `excludeTopics` there), `scopes==null && excludeTopics==null` on
  the MCP path.
- **`ComputeScoresAsync(query, candidates, store, ct)`** (the `ISearchScoreContributor` method) stays,
  now delegating to `Score(EntityFactDerivation.BuildStore(candidates), query)` — this is the exact
  behavior-preserving path the **benchmarks and unit tests** use (they pass curated candidate pools and
  a `null` store; `Score` never touches `store`).

## Behavior preservation

For a given memory set, the store's adjacency (`ScoringKey` docs, `type:value` entities) is **identical**
to what `EntityGraph.Build` produces from the same docs — same node identity, same parse union, same
per-doc dedup, same empty-doc drop. Seeds come from the same `EntityQuerySeeder` over the same node
set. The PPR is the same extracted function. Therefore scores match within FP tolerance. The candidate
universe is provably identical at every call site: filtered queries build the store from the very same
`candidates` the old contributor graphed, and the unfiltered query's cached store is `ListScoped(null)`
— exactly the unfiltered `candidates`.

## Data flow

```
search(query, scopes, excludeTopics)              [PluginPipeline.SearchAsync / MemoryTools.Core]
  ├─ candidates = store.ListScoped(scopes[, excludeTopics])   // also feeds embeddings + auto-surface
  ├─ factStore  = unfiltered ? store.GetEntityFactStore()[cached]
  │                          : EntityFactDerivation.BuildStore(candidates)
  ├─ entityScores = entityContributor.Score(factStore, query)
  │      ├─ (E→D, D→E) = factStore.ScoringAdjacency()         // docs keyed by ScoringKey
  │      ├─ seeds = EntityQuerySeeder.Seed(query, E.Keys)
  │      ├─ ppr   = PersonalizedPageRank.Score(E→D, D→E, seeds, α, iters)
  │      └─ normalize × weight -> IReadOnlyDictionary<"{scope}|{name}", double>
  └─ store.SearchAll(query, scopes, limit, [excludeTopics,] supplemental, entityScores)
       └─ WeightedAttributeScorer.GetSupplemental(scope, name, …) looks up "{scope}|{name}"  // native match
```

## Error handling & edge cases

- **No seeds / empty adjacency** → empty score map (unchanged `None` short-circuits).
- **Unfiltered vs filtered** → cached store vs scoped build; both key by `ScoringKey`, no translation either way.
- **Sidecar v1 on disk** → version mismatch → transparent rebuild as v2 on first read.
- **`ScoringKey` uniqueness** → `(scope, subject)` always distinguishes (unlike the lossy qualified
  name), so scoring never merges two distinct memories.
- **Determinism** → PPR iteration order is fixed by the adjacency map enumeration; the equivalence
  test pins that old and new agree.

## Non-goals

- No change to the signal's meaning: seeds, α=0.15, 20 iterations, `DefaultWeight`, max-normalization
  are all identical.
- No change to the `"{scope}|{name}"` scoring convention or to the embeddings / path-segment
  contributors or `GetSupplemental`.
- No deletion of `EntityGraph` (bench + equivalence test keep it); production just stops building it.
- No new retrieval capability, no ranking *change* (ranking *preservation* is the whole point).

## Testing strategy

- **`PersonalizedPageRank` (unit):** the extracted walk over hand-built adjacencies (seed diffusion,
  restart, convergence); `EntityGraphTests` stay green (parity guard for `EntityGraph.Score`'s delegation).
- **`MemoryEntities` / sidecar (unit):** `MemoryId` derives the same qualified name as before;
  `ScoringKey` = `"{scope}|{name}"`; v2 round-trips `(scope, subject)`; a v1 sidecar is rejected and rebuilt.
- **`EntityFactStore.ScoringAdjacency` (unit):** entity nodes are `type:value`, doc keys are
  `ScoringKey`, edges match the memories.
- **Equivalence (unit — the safety net):** for representative doc sets + queries,
  `EntityGraph.Build(docs).Score(q,α,i)` and `contributor.Score(EntityFactDerivation.BuildStore(docs), q)`
  produce equal score maps within FP tolerance. Doc keys match by construction — both sides key docs by
  `"{scope}|{name}"` (the graph's contributor key == the store's `ScoringKey`), so no format
  reconciliation is needed; that alignment is itself part of what the test confirms.
- **Store selection (unit/integration):** the fully-unfiltered call reuses the cached
  `GetEntityFactStore()` (assert the same instance is consulted / not rebuilt); a `scopes` or
  `excludeTopics` call builds from `candidates` and does **not** consult the cross-scope cache — so an
  excluded-topic memory never becomes a PPR node (the documented "excluded topics don't influence
  scoring" invariant, now guarding the graph signal too).
- **End-to-end ranking (integration):** full `search` result order + scores are unchanged with the new
  contributor vs a baseline — including a `scopes`/`excludeTopics` query (the filtered path) and an
  entity-association query (the signal actually firing).
- **Full suite** green (baseline 1140/0/4).
