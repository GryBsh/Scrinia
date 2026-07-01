# Unified Fields — Design

**Date:** 2026-07-01
**Status:** Approved (v2 — supersedes the v1 "mechanical field extraction" draft in this file's history; v1 layered fields alongside typed keywords, v2 unifies them)

## Problem

The index already stores `key: value`-shaped facts four different ways:

1. **Typed entity keywords** — `person:maria` strings in `Keywords`, parsed per query by `EntityGraph.ParseTypedEntities` (`src/Scrinia.Core/Search/EntityGraph.cs:88`) to build the (never-persisted) entity graph.
2. **Reference keywords** — `file:path` / `ref:name` strings in `Keywords`, emitted by `ExtractRefKeywords`, consumed by the `References`/`Link` tools, `MaintenanceEventSink` orphan tracking, and the CodeRefs builder.
3. **`CodeRefs`** — a path→hash dictionary for drift detection.
4. **(planned v1)** extracted content fields — a fifth variant.

Same shape, four codepaths, three divergent copies of the typed-prefix list (`EntityGraph.RealTypes`, benchmark `AgentPrompts.TypedKeywordPrefixes`, `guide.md` prose). Meanwhile the agent-written content itself carries extractable structure (frontmatter, `key: value` lines, `**Why:** ...` labels) that `TextAnalysis.Tokenize` shreds — versions, dates, hyphenated identifiers, statuses are lost as exact-match signal.

**Decision: it's all just fields.** One multi-valued `Fields` store on the index entry, populated equally from agent self-report and mechanical extraction, consumed by ranking, the entity graph, and reference tracking. nmp/2 is untouched (everything searchable lives in the index). No per-type schema, no deep graph queries — flat key→values as points of specific similarity.

## 1. Storage

- `Dictionary<string, string[]>? Fields` on `ArtifactEntry` and `EphemeralEntry` (last positional param, default null). Multi-valued: a memory can reference two people.
- Keys lowercased + trimmed; values verbatim (trimmed, never tokenized). Caps: 32 keys per entry, values ≤ 120 chars.
- Index stays v3-compatible; pre-fields entries have null and degrade gracefully (like v2 entries without TF).
- **CodeRefs stays** as the drift-hash sidecar (path→hash), now fed from the `file` field's values ∪ the explicit `codeRefs` param. The field states the fact ("references path X"); CodeRefs holds metadata about it (the hash). Drift detection is unchanged.

## 2. Known-fields table — the single point of schema

New `KnownFields` in `Scrinia.Core.Search` — one table replacing the three divergent lists:

| Class | Keys | Merge semantics | Extra role |
|---|---|---|---|
| Entity | `person, place, org, thing, topic` | union-dedupe (accumulate across appends) | feed the entity graph |
| Reference | `file, ref` | union-dedupe | feed References/Link/orphan + CodeRefs hashing |
| Everything else | any | scalar: newer-wins replace across appends; first-wins within one text | — |

That table is the entire schema: merge semantics + graph membership, one place. Legacy `entity:` prefix remains recognized read-side (parity with `ParseTypedEntities`).

## 3. Sources — all land in Fields with equal standing

1. **Agent-supplied**: `key:value`-shaped entries in the existing `keywords` param are routed into Fields at write time. Routing rule: the part before the first `:` matches the extractor's key charset (`[A-Za-z][A-Za-z0-9_-]{0,29}`) and the remainder is non-empty and doesn't start with `//` (URL guard) — so `person:nick` routes, `http://x` and plain `nick` don't. Non-routed keywords stay in `Keywords`. Zero API change — the natural `keywords: ["person:nick", "status:active"]` expression agents already use keeps working, it just lands in the right store.
2. **Mechanical extraction** — `FieldExtractor` (new, sibling of `ReferenceExtractor`): line-scan with code-fence guard; three patterns — frontmatter `key: value` lines inside a leading `---` block; line-start `key: value` (key `[A-Za-z][A-Za-z0-9_-]{0,29}`, value ≤ 120 chars, URL-remainder `//...` guard); bold labels `**Why:** value` / `**Why**: value`.
3. **Reference extraction** — `ReferenceExtractor` output becomes `file`/`ref` field values instead of prefixed keywords.

New writes **stop emitting prefixed keywords entirely** (no dual-write). Store/append rebuild all metadata, so old entries migrate organically on next touch.

## 4. Consumers — redirected at the confirmed choke points

- **Entity graph**: `ParseTypedEntities` (or its caller `EntityGraph.Build`) reads Fields entity keys ∪ legacy typed keywords (dual-read). The graph is rebuilt per query and never persisted, so no stored format migrates.
- **References tool** (`MemoryTools.Core.cs:1263`): matches `file`/`ref` field values ∪ legacy `file:`/`ref:` keywords.
- **Link tool** (`MemoryTools.Core.cs:1298`): writes `ref` field values instead of `ref:` keywords.
- **MaintenanceEventSink**: auto-links and orphan detection read/write the `ref` field ∪ legacy keywords.
- **CodeRefs builder** (`MemoryTools.Core.cs:310`): reads the `file` field instead of scanning `file:` keywords.
- **TF seeding**: field keys + value tokens seeded at +3 (between auto-keyword +2 and agent-keyword +5). `IndexTokensForKeyword` keeps working for legacy entries.
- **CLI `list --summary`**: top-keywords display gains a fields-aware rendering (no more raw `person:x` strings).
- **Benchmark harness**: `AgentPrompts.TypedKeywordPrefixes` deleted, pointed at `KnownFields`.
- **`guide.md` / tool descriptions**: updated so agents learn fields are first-class (still expressed via `keywords`).

## 5. Ranking (ships with this change)

- `WeightedFieldScorer.ScoreEntryTerm` slots, iterating multi-values:
  | Match | Score |
  |---|---|
  | Compound `key:value` query term exact-matches key + one value | 50 (tag-exact parity) |
  | Term exact-matches a field value | 40 (keyword-exact parity) |
  | Term contained in a field value | 12 |
- **Scorer dual-read**: the field slots also parse `key:value`-shaped legacy keywords on the fly, so pre-migration entries rank identically to new ones — the cutover is invisible, no re-index needed.
- All comparisons explicit `OrdinalIgnoreCase` (dictionary comparers don't survive JSON round-trips).
- Query surface beyond ranking (a `fields` filter on `search`/`list`) remains v2 follow-on; storage already supports it.

## 6. Verification

- `FieldExtractor` unit tests: frontmatter, line-start, bold labels, fence-skip, URL guard, caps, first-wins within text.
- `KnownFields` merge tests: entity/ref union vs scalar newer-wins.
- Keyword-routing tests: `person:nick` in the keywords param → Fields, plain keyword stays; store + append paths (MCP and server orchestrator both).
- Consumer parity tests: entity graph scores identical for a legacy-keywords entry vs a fields entry; References/Link/orphan work across both; drift detection unchanged; scorer dual-read parity.
- Round-trip + old-index graceful-load tests.
- Bench: LoCoMo control run (no regression expected; entity-graph benchmark path must produce identical scores through the shared table).

## Risks

- **Extraction noise** — mitigated by anchoring, tight key charset, value cap, fence + URL guards.
- **Index growth** — 32-key / 120-char caps; multi-values bounded by the same 32-key budget.
- **Semantics leak** — union-vs-replace is per-key-class; deliberately confined to the one `KnownFields` table.
- **Stale inbound `ref:` links on forget** — pre-existing gap (`OnForgotten` is a no-op); unchanged by this design, noted for a future pass.
- **Bigger blast radius than v1** — ~6 consumer redirects; mitigated by dual-read everywhere and organic migration (no big-bang re-index).
