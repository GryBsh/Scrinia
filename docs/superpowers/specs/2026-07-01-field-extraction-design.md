# Mechanical Field Extraction for Memories — Design

**Date:** 2026-07-01
**Status:** Approved

## Problem

Agents already write YAML-ish structure into memory content — frontmatter blocks, `key: value` lines, `**Why:** ...` bold labels — but none of it survives as structure. `TextAnalysis.Tokenize` is deliberately lossy (lowercase, split on non-alphanumerics, drop stopwords and short tokens), which shreds exactly the values that structure carries best: versions (`1.2.3`), dates, hyphenated identifiers (`no-go`), enum-ish statuses. These are high-value points of specific similarity that BM25 and field scoring currently can't use.

nmp/2 is opaque at rest; everything searchable lives in the index entry. So "storing structured data" means extracting field pairs at write time and persisting them on the index entry — the format itself is untouched. No per-type schema is maintained and no graph queries are intended; fields are flat key→value pairs used as retrieval signal.

**Goal (decided):** design storage to support both transparent ranking and an explicit query surface, but ship the ranking signal first.

## 1. Extraction — `FieldExtractor`

New static class in `Scrinia.Core.Search`, sibling of `ReferenceExtractor` (same source-generated-regex, LLM-free, write-time style).

Scans line-by-line with a code-fence state guard — nothing inside ``` fences is extracted. Three patterns (decided scope: frontmatter + labeled lines; tables/headers explicitly out):

1. **Frontmatter**: `key: value` lines inside a leading `---` fenced block.
2. **Line-start pairs**: `^key: value` where key matches `[A-Za-z][A-Za-z0-9_-]{0,29}` and value is ≤ 120 chars. Skipped when the value starts with `//` (URL remainder guard, same philosophy as `ReferenceExtractor`).
3. **Bold labels**: `**Why:** taxes retrieval` → key `why`, value `taxes retrieval`.

Rules:
- Keys lowercased; values kept **verbatim** (no tokenization — that is the point).
- First occurrence wins within a single text.
- Hard cap: 32 fields per entry.

## 2. Storage — `Fields` on the index

- `Dictionary<string, string>? Fields` added to `ArtifactEntry` and `EphemeralEntry` (same shape as the existing `CodeRefs`). Index stays v3-compatible; old entries have null.
- Populated automatically at `store`.
- `append` extracts from the new chunk text and merges **newer-wins** into the entry dictionary (a changed `status:` updates; first-wins would freeze stale statuses).
- **Entry-level only** in v1 — no per-chunk fields (YAGNI until something needs them).
- Backfill: **forward-only**. Fields populate on `store`/`append` going forward; pre-existing entries keep null `Fields` and degrade gracefully, exactly like v2 entries without TF data do for BM25. (Correction from the approved draft: `memory_reconcile` is a merge-conflict resolver, not a metadata regenerator — there is no existing re-index-metadata tool, and adding one is not worth it for v1.)

## 3. Ranking (ships first)

Two transparent hooks; no query-syntax change:

- **TF seeding**: field keys and value tokens seeded into `TermFrequencies` at **+3** boost — between auto-keyword (+2) and agent-keyword (+5). BM25 picks fields up for free.
- **`WeightedFieldScorer` slot**:
  | Match | Score |
  |---|---|
  | Compound `key:value` query term exact-matches a pair | 50 (tag-exact parity) |
  | Term exact-matches a field value | 40 (keyword-exact parity) |
  | Term contained in a field value | 12 |

## 4. Query surface (designed-for, shipped later)

The dictionary storage means a follow-on `fields` filter on `search`/`list` (modeled on `excludeTopics`) requires no storage change — v2 is scoring/API surface only.

## 5. Verification

- Extractor unit tests: frontmatter, line-start pairs, bold labels, code-fence skip, URL guard, key charset/length caps, 32-field cap, first-wins within text, newer-wins across append.
- Scorer tests for the three new slots; store round-trip and append-merge tests; old-index-without-fields graceful-load test.
- Bench sanity: a LoCoMo run to confirm no ranking regression (fields are additive signal, but measure, don't theorize).

## Risks

- **Extraction noise** — prose colons, code snippets. Mitigated by line-start anchoring, tight key charset, value length cap, fence guard, URL guard.
- **Index growth** — index.json loads per scope. Mitigated by the 32-field cap and 120-char value cap.
- **Append key collisions** — newer-wins is a judgment call (decided); the alternative freezes stale values.
