# Design: Auto-surfacing entity facts (Piece 3)

**Status:** Approved (design)
**Date:** 2026-07-25
**Scope:** Retrieval-side consumer only. When a `search` query names an entity, prepend a compact
aggregate fact summary to the surfaced context. No new extraction, no ranking change, no agent
behavior change. Explicit aggregate tool/CLI is Piece 4; graph unification is Piece 5.

## Motivation

Piece 1 built the `EntityFactStore` (persisted, per-scope, aggregatable). Piece 2 populated it from
prose (`mention:` spans + async typing). Nothing yet *consumes* it: the aggregation machinery —
counts, date spans, co-occurrence — is invisible to the agent. When the agent searches "what do I
know about Jolene", it gets a ranked list of memories but no at-a-glance answer to "how established
is this entity, over what window, and what does it travel with."

Piece 3 closes that: retrieval injects a compact per-entity fact summary into the surfaced context
**when, and only when, the query names an entity.** Transparent — the block is informational content
appended to the response; it never changes which results are selected or how they rank.

## The five pieces (where this sits)

1. EntityFactStore foundation — *done, merged.*
2. Hybrid entity-span extraction — *done, merged.*
3. **Auto-surfacing — this spec.** Retrieval injects a compact per-entity fact summary when a query
   targets an entity.
4. Explicit aggregate tool + CLI — deferred (natural home for set-intersection / timeline).
5. Graph unification — deferred (sequenced last).

## Design decisions (settled in brainstorming)

- **Trigger = query lexically names the entity.** Reuse the *exact* seeding `EntityGraph.Score`
  already does (`SeedEntities`): an entity `type:value` is named when the lowercased query text
  contains its surface value as a substring, OR every token of the surface appears in the query
  tokens. Deterministic, no new heuristic. Vocabulary-mismatch cases (query says "my cat", memory
  says "Seraphim") are intentionally out of scope for v1.
- **Universe = the searched candidates.** The summary is built from the SAME
  `store.ListScoped(scopes, excludeTopics)` candidate set the scorer already loaded — respecting the
  query's scope and `excludeTopics` filters. Counts/span/co-occurrence therefore describe the
  result set's universe and never leak a scope the agent deliberately hid. No separate store read.
- **Content = count + span + co-occurrences.** Per named entity: mention count and date span across
  *all* matching candidates (not just the top-K shown), plus the top-K co-occurring entities — the
  one signal the results list cannot show.
- **Shape = preamble block, up to 3 entities.** An `entity_facts:` block BEFORE `results:`, one line
  per named entity, capped at 3 (ranked by mention count). Absent entirely when no entity is named.
- **Surface = MCP `search` only (v1).** `recall` has no query to key off; the human-facing CLI
  table can adopt the same computed summary later. Both share `ExecuteSearchAsync`, so the summary
  is computed there behind an opt-in flag (default off) and only the MCP surface renders it.
- **`mention:` included uniformly.** A not-yet-promoted `mention:jolene` still surfaces, honestly
  prefixed. Matches the store's "every (key,value) is an entity, uniformly" principle and Piece 2's
  goal that untyped mentions aggregate. Not hidden as provisional.

## Components & boundaries

### 1. `EntityQuerySeeder` (Scrinia.Core.Search) — shared seeding primitive

Lift `EntityGraph.SeedEntities` into a pure, public, tested static helper. One definition, consumed
by both `EntityGraph` (refactored to call it — behavior unchanged, guarded by its existing PPR
tests) and the auto-surface path.

```csharp
public static class EntityQuerySeeder
{
    /// Returns the subset of entityKeys ("type:value") the query names: the lowercased query
    /// contains the surface value as a substring, OR every token of the surface is a query token.
    /// Surface = the value after the first ':' (a key with no ':' seeds on the whole string —
    /// defensive parity with EntityGraph.SurfaceOf). Ordinal, culture-invariant, deterministic.
    public static List<string> Seed(string query, IEnumerable<string> entityKeys);
}
```

- Depends on: `TextAnalysis.Tokenize` (already used by `SeedEntities`).
- `EntityGraph.SeedEntities` becomes `EntityQuerySeeder.Seed(query, _entityToDocs.Keys)`.
- Pure; no I/O. Testable in isolation: substring match, all-tokens match, no match, multi-entity,
  surface with no colon.

### 2. `EntityFactSummary` + summarizer (Scrinia.Core.Search) — pure aggregation projection

```csharp
public readonly record struct CoOccurrence(EntityRef Entity, int Count);

public sealed record EntityFactSummary(
    EntityRef Entity,
    int MentionCount,
    DateTimeOffset? First,          // null only if the entity has zero facts (defensive)
    DateTimeOffset? Last,
    IReadOnlyList<CoOccurrence> TopCoOccurring);
```

A pure function over an already-built `EntityFactStore`:

```csharp
// Given the entity refs the query named, produce up to `maxEntities` summaries, ranked by
// MentionCount desc then Key/Value ordinal (matching EntityFactStore.Entities tie-breaks).
// Each summary's TopCoOccurring is the store's Neighbors(entity) capped at `coOccurrenceLimit`.
static IReadOnlyList<EntityFactSummary> Summarize(
    EntityFactStore store, IEnumerable<EntityRef> namedEntities,
    int maxEntities = 3, int coOccurrenceLimit = 3);
```

- Reuses `EntityFactStore.MentionCount`, `Span`, `Neighbors` — no new store internals.
- Deterministic ordering. No I/O.

### 3. `ExecuteSearchAsync` wiring — compute once, behind a flag

`ExecuteSearchAsync` already loads the scope+excludeTopics-filtered `candidates` (when a scorer is
present). Extend it to optionally build the summary from those candidates and return it alongside
the matches:

```csharp
internal readonly record struct SearchOutcome(
    IReadOnlyList<SearchResult> Matches,
    IReadOnlyList<EntityFactSummary> EntityFacts);

internal static async Task<SearchOutcome> ExecuteSearchAsync(
    ..., bool surfaceEntityFacts = false, ...);
```

- When `surfaceEntityFacts` is false (default — the CLI `scri search` path), `EntityFacts` is empty
  and no fact-store build happens. The CLI reads `.Matches` and is otherwise unaffected.
- When true (MCP `search`): after candidates are in hand, build a scoped `EntityFactStore` from them
  (via `EntityFactDerivation.Derive` per candidate — same O(candidates) order as the graph build),
  detect named entities with `EntityQuerySeeder.Seed(effectiveQuery, factStore.Entities().Select(formatKey))`,
  map the matched `type:value` keys back to `EntityRef`, and `Summarize`.
- If `surfaceEntityFacts` is true but no scorer context loaded candidates, load them once here (the
  summary needs them regardless). Guard: still zero output when the query names nothing.

**Cost:** one extra O(candidates) derive pass, gated to the agent-facing surface only, and short-
circuited to nothing when the query names no entity. Same cost class as the entity-graph contributor
already on that path. The graph and the fact store stay separate structures here — unifying them is
explicitly Piece 5, not smuggled in early.

### 4. Rendering — `entity_facts:` preamble (Scrinia.Mcp)

The MCP `Search` method passes `surfaceEntityFacts: true`, then renders the block before the results:

```
entity_facts:
  person:jolene — 7 memories, 2026-01-04→2026-07-20; with place:france (4), thing:seraphim (3)
  place:france — 5 memories, 2026-02-11→2026-07-20; with person:jolene (4)
results: 12
  ...
```

- One line per summary: `key:value — N memories, FIRST→LAST; with co1 (c1), co2 (c2), ...`.
- Dates via the same formatting the result blocks use (`AppendDatesLine`'s ISO rendering) for
  visual consistency.
- Singular/plural on "memory"/"memories"; omit the "; with ..." clause when there are no
  co-occurrences; omit the span when First/Last is null (defensive).
- Block omitted entirely when `EntityFacts` is empty — the common case pays nothing.

## Data flow

```
search(query, scopes, excludeTopics, ...)                       [MCP tool]
  └─ ExecuteSearchAsync(..., surfaceEntityFacts: true)
       ├─ candidates = ListScoped(scopes, excludeTopics)         [already loaded for scoring]
       ├─ matches    = SearchAll(...)                            [unchanged — ranking untouched]
       └─ entityFacts:
            factStore = EntityFactStore(candidates.Derive())
            keys      = factStore.Entities().Select(e => $"{e.Key}:{e.Value}")
            named     = EntityQuerySeeder.Seed(effectiveQuery, keys)   // -> matched "type:value" keys
            summaries = Summarize(factStore, named.Select(parseRef), max=3, coOcc=3)
  └─ FormatRichSearchResults renders entity_facts: block, then results:
```

## Error handling & edge cases

- **No entity named** → `EntityFacts` empty → no block. Zero surprise, near-zero cost.
- **Entity named but single mention** → still surfaced (`1 memory`, possibly no co-occurrences).
  Honest and informative.
- **`effectiveQuery` includes `context`** → seeding runs on the effective query, consistent with how
  the graph contributor already seeds. (The `context` param is scoring-only elsewhere; here it can
  legitimately name an entity the agent knows a synonym for.)
- **Same value under multiple keys** (`thing:jolene` and `mention:jolene`) → distinct `EntityRef`s,
  both surfaced if both are named and within the cap. No dedup — they are genuinely different facts.
- **Determinism** → stable ordering throughout (count desc, then key/value ordinal); same inputs →
  same block.

## Non-goals (explicitly out of scope for Piece 3)

- Set-intersection ("how many mention BOTH A and B" via `MemoriesFor`) — Piece 4 explicit tool.
- Timeline rendering / duration phrasing — Piece 4.
- CLI table surface and `recall` integration — later; the computed summary is reusable when they come.
- Vocabulary-mismatch / semantic entity detection — v1 is lexical naming only.
- Any change to ranking, selection, scoring weights, or `EntityGraph` (that is Piece 5).
- Any new extraction or write-path change.

## Testing strategy

- **`EntityQuerySeeder`** (unit): substring match, all-tokens match, no match, multi-entity query,
  colon-less key; parity assertion that `EntityGraph` seeding is unchanged post-refactor.
- **`Summarize`** (unit): correct count/span/top-co-occurrence from a hand-built store; ranking by
  count; cap at `maxEntities`; co-occurrence cap at `coOccurrenceLimit`; deterministic order.
- **Rendering** (unit): block shape, singular/plural, omitted co-occ clause, omitted span; block
  absent when no entity named.
- **Integration** (MCP `search` over a temp workspace): query naming an entity yields the preamble
  with correct aggregates; query naming nothing yields no block; `excludeTopics` genuinely removes a
  scope's contribution from the counts (searched-universe guard).
- **No-behavior-change** (regression): the `results:` section — order, scores, membership — is
  byte-identical with and without `surfaceEntityFacts`; the CLI path (`surfaceEntityFacts: false`)
  builds no fact store.
