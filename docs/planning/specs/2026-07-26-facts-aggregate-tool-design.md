# Design: `facts` explicit aggregate tool + CLI (Piece 4)

**Status:** Approved (design)
**Date:** 2026-07-26
**Scope:** A deliberate query surface (MCP tool + `scri` verb) for aggregate/relational questions over
the entity fact store — the counterpart to Piece 3's automatic surfacing. Reuses the existing
`EntityFactStore` query API; adds no new store logic. Graph unification is Piece 5.

## Motivation

Piece 1 built `EntityFactStore` (persisted, aggregatable). Piece 2 populated it from prose. Piece 3
*auto-surfaces* a compact summary when a `search` query happens to name an entity. What's still
missing is a way for the agent to **deliberately ask** the aggregate/relational questions keyword
search cannot express:

- "What memories mention **both** Jolene and France?" (set intersection — explicitly deferred in Piece 3)
- "Give me the full timeline of everything about `person:maria`."
- "What `thing` entities do I know?" (disambiguation / vocabulary discovery)

`search` finds memories by text; `facts` answers questions about the *entities* across them.

## The five pieces (where this sits)

1. EntityFactStore foundation — done, merged.
2. Hybrid entity-span extraction — done, merged.
3. Auto-surfacing — done, merged.
4. **Explicit aggregate tool + CLI — this spec.**
5. Graph unification — deferred (last).

## Design decisions (settled in brainstorming)

- **One `facts` tool with an operation selector**, not multiple tools and not folded into `list`/`search`.
  Ops ∈ {`entities`, `profile`, `intersect`}. Every op takes an entity-ish argument, so a single tool
  keeps the surface at +1 and ambiguity low. Rejected: overloading `list`/`search` (muddies two
  keyword-retrieval schemas with a relational model) and N tiny tools (against the codebase's
  deliberate minimalism — cf. the `/skill`-routing note in `MemoryTools.cs`).
- **`profile` is a full deep-dive + timeline**: count + span + top co-occurrences + the chronological
  per-memory timeline (id + date). The timeline is the pointer set the agent then recalls from — this
  is what makes the explicit tool worth more than Piece 3's compact auto-surface.
- **Cross-scope by default.** `facts` means "ask everything I know," so it defaults to the cross-scope
  merged store via Piece 1's cached `GetEntityFactStore()` (fast; reuses the generation-invalidated
  cache). This is also consistent with `search`, whose default is already all-scopes (`excludeTopics`
  is opt-in per call). Optional `scopes` + `excludeTopics` narrow it — the **same knobs, same
  semantics as `search`** — so an agent can do a knowledge-only profile
  (`excludeTopics: 'plan,task,project,learn'`) with no new mental model.
- **One uniform entity-addressing rule.** An entity argument is either `key:value` (exact, e.g.
  `person:jolene`) or a bare `value`. A bare value resolves across all keys: exactly one match → use
  it; multiple → the response is a **disambiguation list** (`did you mean person:jolene or
  mention:jolene?`) and the op is not performed; zero → a not-found response. Same rule for `profile`
  and each `intersect` member. Keys and values are lowercased (matching `EntityRef.Of`), so
  `person:Jolene` and `jolene` both resolve.
- **One pipeline, two surfaces.** A single `ScriniaMcpTools.Facts(...)` handler returns the YAML
  `McpResponse`; the CLI `scri facts <op> ...` calls the same handler and prints it (with `--json`),
  exactly like every other MCP-passthrough command.

## Operations

### `entities` — vocabulary discovery / disambiguation
- **Input:** optional `key` (filter to one type, e.g. `person`); optional `limit` (default 50).
- **Output:** each entity as `key:value` with its mention count, sorted by count desc then Key/Value
  ordinal, capped at `limit`. Reports the total count when truncated.
- **Store API:** `Entities(key)` + `MentionCount(e)`.

### `profile <entity>` — the deep-dive
- **Input:** `entity` (required; `key:value` or bare `value`).
- **Output** for the resolved `EntityRef`:
  - mention count,
  - date span (`first → last`),
  - top co-occurring entities with counts (default top 10 — richer than Piece 3's 3),
  - the **timeline**: every mentioning memory as (memoryId, date), chronological (date asc, then id
    ordinal), capped at a generous `limit` (default 50) with a truncation note.
- **Store API:** `MentionCount`, `Span`, `Neighbors`, `Timeline`.
- Bare value matching multiple entities → disambiguation list instead of a profile.

### `intersect <entity...>` — the relational query
- **Input:** `entities` (array, **≥2**).
- **Resolution:** each member resolves by the uniform rule; if any member is ambiguous or not found,
  the response is that member's disambiguation / not-found (the op is not performed).
- **Output:** the memories mentioning **all** members, as (memoryId, date), sorted by date asc then id
  ordinal, plus the count. Empty set is a valid, explicit "0 memories mention all of: …".
- **Store API:** `MemoriesFor(refs)` for the id set; dates joined from `Facts(refs[0])` (every
  intersecting memory appears in each member's facts, so the first member's facts carry the dates —
  no new store method needed).

## Components & boundaries

### 1. `FactsQuery` (Scrinia.Core.Search) — pure query service
Given an already-built `EntityFactStore` and a parsed request, returns a discriminated result. Pure,
I/O-free, reuses only the public `EntityFactStore` API. Owns entity resolution.

```csharp
// Entity resolution outcome for one argument.
//   Resolved(ref)         exactly one match
//   Ambiguous(candidates) bare value matched >1 entity
//   NotFound(arg)         no match
// Op results:
//   EntitiesList(items, total, truncated)
//   Profile(entity, count, span, neighbors, timeline, timelineTruncated)
//   Intersection(members, memories, count)
//   plus Ambiguous / NotFound surfaced from resolution
```
- `ResolveEntityArg(EntityFactStore, string) → Resolution` — `key:value` → exact `EntityRef.Of`;
  bare value → all `Entities()` whose `Value` equals the lowercased arg (1 → Resolved, 0 → NotFound,
  >1 → Ambiguous).
- One method per op producing the corresponding result record.
- Deterministic ordering throughout (matching `EntityFactStore`'s own tie-breaks).

### 2. Store selection (in the MCP handler)
- `scopes` and `excludeTopics` both null → the cached cross-scope `store.GetEntityFactStore()`.
- Either set → build a scoped `EntityFactStore` from `store.ListScoped(scopes, excludeTopics)` via
  `EntityFactDerivation.Derive(sa, store.FormatRoundTripQualifiedName(sa.Scope, sa.Entry.Name))` —
  the same pattern Piece 3's `BuildEntityFacts` uses. No second authority, no new store internals.

### 3. `ScriniaMcpTools.Facts(...)` — the MCP tool
```csharp
[McpServerTool(Name = "facts"), Description(...)]
Task<string> Facts(
    string op,                     // "entities" | "profile" | "intersect"
    string? entity = null,         // profile: the target
    string[]? entities = null,     // intersect: the members (>=2)
    string? key = null,            // entities: filter to one type
    string? scopes = null,         // narrow (same semantics as search)
    string? excludeTopics = null,  // narrow (same semantics as search)
    int limit = 50,                // entities list cap / profile timeline cap
    CancellationToken ct = default);
```
- Validates op + required args per op (missing/invalid → `ErrorCodes.InvalidParameter` with a
  corrective example, matching the other tools' error style).
- Selects the store, runs `FactsQuery`, formats the result to YAML.

### 4. `FactsResponseFormatter` — result → YAML content
Renders each result variant into the `McpResponse` content (dates as `yyyy-MM-dd`, InvariantCulture,
matching the search result blocks). Disambiguation and not-found render as actionable guidance
(`status: warning` with the candidate `key:value`s), not errors.

### 5. `scri facts` — CLI mirror
A `ScriniaCommands.Facts(op, entity, entities, key, scopes, excludeTopics, limit, json, workspaceRoot)`
method that configures the workspace, calls `ScriniaMcpTools.Facts`, and prints via the existing
`EmitMcpResult` (raw YAML, or `--json` envelope) — identical to `search`/`list`.

## Data flow

```
facts(op, entity/entities, key, scopes, excludeTopics, limit)      [MCP tool / scri facts]
  ├─ store = (scopes|excludeTopics set) ? scopedStore(ListScoped(...)) : GetEntityFactStore()  [cached]
  ├─ result = FactsQuery.<op>(store, parsed args)   // resolution + EntityFactStore API only
  └─ FactsResponseFormatter → McpResponse YAML
```

## Error handling & edge cases

- **Unknown op / missing required arg** → `status: error`, `INVALID_PARAMETER`, with a corrective example.
- **`intersect` with < 2 entities** → invalid-parameter (intersection of one is just `profile`).
- **Ambiguous bare value** → `status: warning` + the candidate `key:value`s; op not performed.
- **Not-found entity** → `status: warning` ("no entity named …"); for `entities`/`intersect` this is a
  legitimate empty answer, not an error.
- **Empty intersection** → `status: success`, explicit "0 memories mention all of: …".
- **Truncation** (entities list or profile timeline beyond `limit`) → the content states the shown/total
  counts so the agent knows to narrow.
- **Determinism** → stable ordering everywhere; same store + args → same bytes.

## Non-goals (out of scope for Piece 4)

- Any change to ranking, `search`, `EntityGraph`, or scoring (that is Piece 5 / not this piece).
- Graph unification — re-expressing PPR over the persisted store is Piece 5, sequenced last.
- New extraction or write-path changes.
- Free-text / fuzzy entity matching beyond exact `key:value` and exact bare-value resolution.
- Cross-entity analytics beyond intersection (e.g. "entities most correlated with X") — YAGNI now.

## Testing strategy

- **`FactsQuery` (unit):** entities list (sort, `key` filter, `limit` truncation); profile (count,
  span, neighbors, timeline order + cap); intersect (all-members id set, dates, empty set, <2 guard);
  resolution (`key:value` exact, bare unique, bare ambiguous → candidates, not-found).
- **Store selection (integration):** unfiltered uses the cross-scope cached store; `scopes`/
  `excludeTopics` narrow the aggregates (an entity present only in an excluded topic drops out) — the
  searched-universe guard, mirroring Piece 3's excludeTopics test.
- **MCP tool (integration):** each op end-to-end over a temp workspace returns the expected YAML;
  invalid op / missing arg / <2 intersect → the right error; ambiguous → disambiguation warning.
- **CLI mirror:** `scri facts <op>` produces the same YAML the tool does (and `--json` wraps it).
