# Design: EntityFactStore — first-class aggregated fact store (Piece 1 of 5)

**Status:** Approved (design)
**Date:** 2026-07-24
**Scope:** Piece 1 (foundation) only. Pieces 2–5 are recorded in the Roadmap and are NOT built here.

## Motivation

Analysis of the cached LoCoMo judge run (`agentic-ingest_20260723_2330`, post temporal-fix)
showed the dominant cause of incorrect "NOT ANSWERABLE FROM CONTEXT" answers is **surfacing**,
not the model: when the gold content is in the answerer's context it converts ~73% (85%+ on
count/duration questions), and craters to ~25% when it isn't. Two structural gaps drive the
misses:

- **No aggregation.** Counts ("how many times X"), durations ("how long between A and B"), and
  set-intersections ("memories touching both A and B") need facts collected/compared across many
  memories. Scrinia stores atomic point-facts and has no structure that aggregates them, so even
  when the pieces are stored, nothing assembles them.
- **Ephemeral, shallow entity structure.** `EntityGraph` is rebuilt per query, stateless, and used
  only for PPR *scoring* — never persisted, never queried for facts. And mechanical extraction
  (`AttributeExtractor.Extract`) only reads *structure* (frontmatter, `key: value` lines,
  `**Bold:**` labels); a freeform sentence like "Jolene bought Seraphim in France" yields **zero**
  attributes. So the graph is only as populated as the agent's explicit tagging.

This is a real-world deficiency, not a benchmark artifact: an agent's memory should be able to
answer "how many times did we hit this bug", "how long did the migration take", "which memories
touch both auth and billing", and disambiguate "Seraphim" (the snake) from "Susie" (the other
snake). We address it regardless of what the pending mechanical `--agent` judged run reports.

## The five pieces (decomposition)

1. **EntityFactStore foundation** — *this spec.* The persisted, per-scope, aggregatable fact store
   + its query API, populated from what's already on entries (`Attributes` + effective date). No
   new extraction, no consumer.
2. **Hybrid deterministic extraction** — rules (quantities, dates, refs, `key:value`) +
   embedder-assisted entity spans; the agent `attributes` map stays authoritative. Enriches the
   facts written per memory. (Deferred.)
3. **Auto-surfacing** — retrieval injects a compact per-entity fact summary into surfaced context
   when a query targets an entity. Transparent; no agent behavior change. (Deferred.)
4. **Explicit aggregate tool + CLI** — a `facts`/`entity` MCP tool + `scri` verb over the store's
   API. (Deferred.)
5. **Graph unification** — re-express `EntityGraph` PPR over the persisted store's adjacency, retire
   the per-query rebuild. Optimization/dedup, not a new capability; sequenced LAST, after 3/4 reveal
   the store's real access patterns. (Deferred.)

Design anchors agreed for the whole set (shape pieces 2–4, not built here): **hybrid extraction**,
**both query surfaces** (auto-surface + explicit tool), **persisted materialized sidecar**.

## Core concept

`EntityFactStore` is a **persisted, per-scope materialized view of the facts already implied by a
memory's entities + the memory's effective date.** A memory's entities are the **same
`ParseEntityKeywords(Keywords) ∪ ParseEntityAttributes(Attributes)` union `EntityGraph` consumes**
(`Attributes` plus any legacy `key:value` `Keywords`) — the store never derives entities differently
from the graph. Every attribute `(key, value)` on a memory is an **entity**, uniformly — so
`person:maria`,
`thing:seraphim`, `project:apollo`, and `topic:auth` are all entities and aggregate identically. For
each entity the store holds the memories that mention it, each stamped with that memory's date and
the other entities co-occurring in it. Counts, timelines/durations, co-occurrence links, and
intersections all derive from that.

**Key property — the sidecar is a materialized *cache of derived data*, never a competing source of
truth.** `entry.Attributes` + the memory's date remain the sole authority. If the sidecar is
missing, an older format version, or fails a cheap validity check against the index, it is **rebuilt
from the entries and rewritten** — there is no hand-migration path, ever. "Persisted" buys restart
speed and first-class on-disk presence; it never buys a second authority to keep in sync.

## Data model

```csharp
// Key lowercased; Value lowercased and kept whole (NOT tokenized) — identical to EntityGraph node
// identity, so the store and the graph never disagree on entity identity.
readonly record struct EntityRef(string Key, string Value);

sealed record EntityFact(
    EntityRef Entity,
    string MemoryId,                         // round-trip-safe qualified name (scope + subject)
    DateTimeOffset Date,                     // entry.UpdatedAt ?? entry.CreatedAt — see below
    IReadOnlyList<EntityRef> CoOccurring);   // the other entities in the same memory
```

Internal indices in the merged in-memory view:
- `entity → List<EntityFact>` — the spine (drives Facts / Timeline / MentionCount / MemoriesFor).
- `memoryId → EntityRef[]` — reverse index (cheap intersection & co-occurrence).

### Date semantics: `Date = entry.UpdatedAt ?? entry.CreatedAt`

A fact is derived from the memory's **current** content, so its honest timestamp is when that
content was last authored, not creation. Dating a possibly-rewritten fact (`status: in-progress`
edited to `status: done`) back to `CreatedAt` would stamp it before it was true.

Two accepted limitations for the foundation:
- **A memory-wide edit re-dates all of that memory's facts**, even an attribute unchanged by the
  edit — we do not diff versions for per-attribute provenance. Acceptable proxy; true per-attribute
  change dates would require version-diffing (out of scope, not now).
- **This is an "as-of/currency" date, not an event date.** An event edited months later should not
  move on a timeline. That distinction only matters once extraction (Piece 2) can attach a real
  event date to a fact; at that point `EntityFact` gains an optional `EventDate` separate from this
  authored `Date`, and `Timeline`/`Span` prefer `EventDate` when present. The API below does not
  change.

## Persistence

- **One sidecar per scope**, next to that scope's `index.json` (e.g. `facts.json`), mirroring the
  existing per-scope storage layout. A mutation to a scope rewrites only that scope's sidecar —
  bounded work, the same write cadence `index.json` already has.
- **Read path:** on first aggregate query, load each in-scope sidecar and build the **merged
  in-memory view** (entities span scopes: `person:maria` in `/findings` and `/patterns` aggregate to
  one entity). Cache the merged view; **invalidate on any mutation** to a contributing scope
  (cache-on-first-read + mutation-invalidation).
- **Rebuild-on-mismatch, never migrate:** each sidecar carries a `version` and a cheap **validity
  token** — the scope's entry count plus a cumulative hash of `(id, UpdatedAt ?? CreatedAt)` over its
  entries. If the sidecar is missing, an old version, or the token doesn't match the scope's current
  `index.json`, the sidecar is rebuilt from the entries and rewritten. A matching token loads
  without rebuild.
- **Format:** JSON via a source-generated serialization context (same style as the index). Stores the
  per-scope facts (entity → its facts with `MemoryId` / `Date` / co-occurring refs) so a restart
  skips the rebuild.

## Aggregate-query API

An entity *is* an `(key, value)` pair; predicate = attribute key. Seven methods:

```csharp
IReadOnlyList<EntityFact> Facts(EntityRef entity);                         // everything about an entity
IReadOnlyList<EntityFact> Timeline(EntityRef entity);                      // Facts ordered by Date (durations)
int MentionCount(EntityRef entity);                                        // "how many times X appears"
IReadOnlyList<string> MemoriesFor(params EntityRef[] entities);            // 1 → mentions; N → touching ALL (intersection); 0 → empty
IReadOnlyList<(EntityRef Entity, int Count)> Neighbors(EntityRef entity);  // co-occurring entities, ranked by count
(DateTimeOffset First, DateTimeOffset Last)? Span(EntityRef entity);       // for "how long between A and B"
IReadOnlyList<EntityRef> Entities(string? key = null);                     // discovery / disambiguation
```

- `MemoriesFor()` collapses the single-entity lookup and the intersection into one variadic method:
  one entity → memories mentioning it; many → memories touching all of them. **Zero args returns
  empty** (meaningless input must not silently mean "everything").
- Ordering is deterministic and stable across builds (e.g. `Neighbors` by count desc then
  `EntityRef` ordinal; `Entities` by key then value).

**Foundation scope line (honest):** these operate over entity *mentions*, *co-occurrences*, and
*dates*. That already answers intersection ("memories touching both A and B"), span ("when did X
first/last appear / how long between A and B"), and disambiguation (`Entities("thing")` → seraphim
vs susie). It does **not** yet do predicate-level counting like "how many times Jolene *visited*
France" — "visited" isn't a predicate until extraction (Piece 2) mines it. The API is already
predicate-aware (predicate = key), so Piece 2 enriches the data without changing this contract.

## Relationship to `EntityGraph`

**Additive, not a replacement.** `EntityFactStore` reuses the exact same entity definition —
`EntityGraph.ParseEntityKeywords ∪ ParseEntityAttributes` — so the store and the graph never
disagree on what an entity is (a test guards this). `EntityGraph` stays as the per-query PPR
*scoring* contributor; the store is the persisted *aggregation* structure. They coexist. Unifying
them (the store's `Neighbors` co-occurrence *is* the graph's adjacency) is Piece 5, deferred to last.

## Components & boundaries

- `EntityRef`, `EntityFact` — data model (Scrinia.Core.Search or a new Scrinia.Core.Facts namespace;
  decide at plan time following existing conventions).
- `EntityFactStore` — the merged in-memory view + the seven query methods. Pure logic; built from an
  `IEnumerable<ScopedArtifact>`, deriving each memory's entities via the shared
  `ParseEntityKeywords ∪ ParseEntityAttributes` union. No I/O.
- Persistence layer — load/save per-scope sidecar, validity-token check, rebuild-on-mismatch, merged
  view caching + invalidation. Hooks into `FileMemoryStore`'s existing mutation points (Upsert /
  Remove / the write paths) and its scope-load path. Kept separate from the pure store so the store
  is testable without disk.

## Testing

- **Build correctness** — from hand-built `ScopedArtifact`s (attributes, dates, multiple scopes),
  assert every method: `Facts`, `Timeline`, `MentionCount`, `MemoriesFor` (single + intersection +
  zero-arg empty), `Neighbors` ranking, `Span`, `Entities(key)`.
- **Date semantics** — a fact from an updated memory dates to `UpdatedAt`; from a never-updated one,
  to `CreatedAt`.
- **Cross-scope merge** — same entity in two scopes aggregates to one, facts from both.
- **Shared entity definition** — anything `ParseEntityKeywords`/`ParseEntityAttributes` treats as an
  entity shows up in the store (guards store/graph drift).
- **Persistence round-trip** — build → save → reload → identical merged view.
- **Rebuild-on-mismatch** — missing / old-version / token-mismatch sidecar is rebuilt and rewritten;
  a matching token loads without rebuild.
- **Invalidation** — mutating a scope invalidates the cached merged view; next query reflects it.
- **Determinism** — stable ordering, same inputs → same outputs.

## Non-goals (explicitly out of scope for Piece 1)

- Any new extraction from prose (Piece 2).
- Any consumer — no auto-surfacing in retrieval (Piece 3), no MCP tool / CLI verb (Piece 4).
- Any change to `EntityGraph` or the scoring path (Piece 5).
- Per-attribute provenance dates / version-diffing.
- In-content event-date parsing (arrives with Piece 2 as `EntityFact.EventDate`).
