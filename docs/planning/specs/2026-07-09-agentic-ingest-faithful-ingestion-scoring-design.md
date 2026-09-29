# Agentic-Ingest Faithful Ingestion + Honest Scoring — Design

**Date:** 2026-07-09
**Scope:** LoCoMo, dialog granularity only.
**Status:** approved for planning.

## Problem

A real `locomo / bm25__agentic-ingest` run reports `answerable@10 = 0.0%` and
`R@5/@10/@50 = 0.0%` across every category, with `memories written: 112`,
`memories without refs: 112`, `unresolvable refs: 0`, `tool-call errors: 0`.
Investigation against the code and the persisted episode cache
(`cache/episodes-openai-qwen3.ndjson`, 272 episodes / 373 `memory_remember`
calls) found three stacked causes. Two are scoring bugs; one is a deeper
ingestion-fidelity bug.

### Cause 1 — Lossy ingestion (deepest; caps everything downstream)
The harness exposes only two tools — `memory_remember` and `memory_search`
(`AgenticIngestPrompts.Tools()`; `AgenticIngestHarness.ExecuteCallAsync`
dispatches only those two). There is **no `memory_append` and no
`memory_recall`.** `memory_remember` upserts by path, so when the agent reuses
a natural path (`/sessions/2026-04-08`, `/people/melanie`) across turns and
across sessions in the shared store, each write **clobbers** the prior content.
The 373 → 112 collapse is ~70% of recorded content destroyed before scoring
runs. This is not the agent misbehaving; the harness handed it an amputated
toolset, so it *cannot* accumulate — the opposite of the arm's premise
("ingest like it would during actual usage"). No amount of scoring repair
recovers content that was never retained.

### Cause 2 — Ref citations unparseable (R@k structurally 0)
The model *does* cite sources, but in forms nothing parses: comma lists
(`ref: D2:1, D2:3, D2:5`), ranges (`ref: D9:1-D9:17`), and inline refs. Only
71/373 writes put `ref:` at line-start. Provenance relied on production
`FieldExtractor` → `Fields["ref"]`, whose `LineStartPairPattern` captures the
whole right-hand side as **one opaque composite string** (`"D2:1, D2:3, …"`)
that matches no individual doc-id. Even a perfect citation scores 0.

### Cause 3 — Judge starved by provenance coupling (answerable@10 = 0)
`AgenticScriniaRetriever.RetrieveAsync` drops any memory whose refs don't
resolve to a buffered doc-id (`if (docIds.Count == 0) continue;`). The judge's
answerer is fed `hits.Select(h => h.Text)`, so with an empty/broken provenance
map it receives **no context** and cannot answer — even though the memory
matched by BM25 and its full text is available. The judge — the metric built
specifically to sidestep provenance's flaw — is gated by provenance anyway.
From the cache, only 103/373 writes contain `ref:` at all, so even a perfect
parser leaves ~72% of memories with no resolvable ref; those must still feed
the judge.

### Why dialog granularity
Dialog granularity is the chosen target. It removes a fourth issue for free:
at dialog granularity the buffered doc-ids **are** the `D{n}:{i}` dialog ids
(`LoCoMoLoader` line 120) and the transcript labels every turn `[D2:1]`, so the
model's citations map directly to doc-ids — no `D{n}:{i} → session_N` namespace
mapping is needed (that was purely a session-granularity artifact). LoCoMo gold
at dialog granularity is the evidence `dia_id`s, a direct match.

## Goals

1. The agentic-ingest arm ingests with the real tool loop and stops clobbering
   its own memories.
2. `answerable@10` reflects what the store actually surfaces for a query,
   independent of provenance resolution.
3. Provenance-R@k becomes an *honest* labeled lower bound (parses the model's
   real citation forms) rather than a structural zero.

## Non-goals

- Session granularity, LongMemEval, and any other dataset/mode.
- Prompt engineering to raise citation compliance. R@k stays a labeled lower
  bound; the judge does not depend on it.
- Modifying production `Scrinia.Core` / `Scrinia.Mcp` code. All bench needs are
  met in the benchmark project. Production `FieldExtractor` / `ReferenceExtractor`
  are **not** touched.
- Recovering the ~260 clobbered memories from the old run; Part 1 prevents the
  loss going forward, it does not reconstruct past runs.

## Design

Five units, each independently testable.

### Part 1 — Faithful ingestion toolset

**Files:** `Agent/AgenticIngestPrompts.cs`, `Agent/AgenticIngestHarness.cs`.

- Add `memory_append` and `memory_recall` to `AgenticIngestPrompts.Tools()`
  with JSON schemas mirroring the real tools:
  - `memory_append`: `{ path: string (required), content: string (required),
    description?, tags?[], keywords?[], anchors?[] }`. Note `content` is a
    **single string**, not an array (matches `MemoryTools.MemoryAppend`).
  - `memory_recall`: `{ path: string (required), chunk?: integer }`.
- Add dispatch cases in `AgenticIngestHarness.ExecuteCallAsync` (used by both
  live episodes and `ReplayAsync`):
  - `memory_append` → `tools.MemoryAppend(path, content, description, tags,
    keywords, anchors, ct)`. Required-field validation (`path`, `content`)
    returns a production-style error tool-result on miss, same pattern as
    `DispatchRememberAsync`.
  - `memory_recall` → `tools.MemoryRecall(path, chunk, withBuiltin: false, ct)`.
- Both new tools are recorded in `EpisodeToolCall`s like the existing ones, so
  the cache replays them. Replaying a `memory_recall` (a read) is harmless; the
  reconstruction correctness comes from replaying the mutations
  (`memory_remember`, `memory_append`) in recorded order.
- Update the bench addendum in `AgenticIngestPrompts` to name the real loop:
  search for an existing memory, recall it, and **append** rather than
  re-`remember` the same path. Keep it minimal — the embedded guide already
  documents these tools; this only makes the bench framing consistent. No
  benchmark-tuning language.
- Bump `AgenticIngestPrompts.PromptVersion` `"v1" → "v2"`. The episode cache key
  is `Key(modelSlug, PromptVersion, transcript)`, so the bump invalidates
  stale 2-tool cached episodes (old entries become dead keys, ignored). The
  existing `episodes-openai-qwen3.ndjson` is left in place; new keys simply
  don't hit it.
- Exception-safety already in place (prior fix): an append to a nonexistent
  path returns a tool error the agent can self-correct on, and never aborts
  the run.

### Part 2 — Bench dialog-ref parser

**Files:** new `Core/DialogRefParser.cs` (static, pure); tests
`DialogRefParserTests.cs`.

A pure function `IReadOnlyList<string> ExtractDialogIds(string content)` that
scans arbitrary memory content and returns every LoCoMo dialog id it finds,
**regardless of any `ref:` prefix** (the `D{n}:{i}` shape is distinctive enough
to read directly, which sidesteps the line-start-vs-inline problem):

- bare / inline ids: `D2:1`
- comma lists: `D2:1, D2:3, D2:5` → `["D2:1","D2:3","D2:5"]`
- ranges `D{n}:{a}-D{n}:{b}` and short form `D{n}:{a}-{b}` → expanded to
  `D{n}:{a} … D{n}:{b}` inclusive. Only same-session ranges expand (the `n`
  is shared). A descending or malformed range falls back to the two endpoints
  as individual ids.
- Output is de-duplicated, first-seen order preserved. The parser does **not**
  filter against existing docs — that is the caller's job (unknown ids count
  as unresolvable), keeping the parser pure and dataset-shape-only.

Regex sketch (final form decided in implementation):
`D(\d+):(\d+)(?:\s*-\s*(?:D\1:)?(\d+))?` with capture of `n`, `a`, optional `b`.

### Part 3 — Resolve-once full-content cache

**Files:** `Core/AgenticScriniaRetriever.cs`.

After episodes finish (in `EnsureEpisodesRunAsync`, before/within
`BuildProvenanceMap`), resolve every stored memory's full plaintext **once**
into a field `Dictionary<string,string> _fullContentByEntryName`, using the
entry `Uri` + `Nmp2Strategy.Decode` path that `RetrieveAsync` already uses. The
store is immutable during the query phase, so a single resolve pass suffices;
this **replaces** the per-`RetrieveAsync` lazy `resolvedFullContent`
re-resolution (respects the cache-on-first-read / SSD-wear preference). Parts 2
and 4 read from this cache, and `RetrieveAsync` itself reads `hit.Text` from it
instead of lazy-resolving — one full-content source for the whole retriever. On
a resolve failure, fall back to the entry's `ContentPreview` and log once per
entry (existing `_loggedResolveFailures` dedupe). The `surfaceFullContent`
constructor flag stays as the gate for *whether* to resolve full content at all
(a no-judge run needs only doc-id hits, so it skips the resolve pass and
`hit.Text` stays preview).

### Part 4 — Judge decoupling

**Files:** new `Core/ISurfacedContentProvider.cs`; `Core/AgenticScriniaRetriever.cs`;
`Core/BenchmarkRunner.cs`.

- New interface:
  ```csharp
  public interface ISurfacedContentProvider
  {
      Task<IReadOnlyList<string>> GetSurfacedContentsAsync(
          string query, int k, CancellationToken ct = default);
  }
  ```
- `AgenticScriniaRetriever` implements it: `SearchAll(query, limit:
  Math.Max(k * 4, k))` (over-fetch so chunk-results collapsing to the same
  parent memory still yield `k` distinct memories) → dedup by entry name,
  take the top-`k` → return their full content from `_fullContentByEntryName`
  (Part 3). **No provenance gate.** This is "what the store surfaces for the
  query."
- `BenchmarkRunner` judge block (both `RunAsync` and `RunGroupedAsync`): source
  the judge's contents from the provider when the retriever implements it, else
  keep `hits.Select(h => h.Text)`:
  ```csharp
  var judgeContents = retriever is ISurfacedContentProvider scp
      ? await scp.GetSurfacedContentsAsync(queryText, _judge.JudgeK, ct)
      : hits.Select(h => h.Text).ToList();
  ```
  Every mechanical arm (no interface impl) is byte-for-byte unchanged. R@k
  continues to read `RetrieveAsync` (provenance-gated) as the labeled lower
  bound.

### Provenance build (ties Parts 2–3 together)

`BuildProvenanceMap` stops reading `entry.Fields["ref"]` and instead runs
`DialogRefParser.ExtractDialogIds(_fullContentByEntryName[entry.Name])`,
resolving each id against `_docsById` (existing → resolved provenance; missing
→ `_unresolvableRefs++`). `_memoriesWithoutRefs` counts memories whose parsed id
set is empty. Stats semantics are otherwise unchanged.

## Data flow

```
Ingest (buffer docs by session)
  → per-session episode: LLM loop over {search, recall, remember, append}
      → real ScriniaMcpTools writes/accumulates into shared store
  → resolve-once full content per stored memory        (Part 3)
  → BuildProvenanceMap: DialogRefParser over full content → doc-ids (Parts 2,3)

Query time:
  R@k    : RetrieveAsync → BM25 → provenance-expand (gated) → doc-id hits
  Judge  : GetSurfacedContentsAsync → BM25 → top-k memory full content (ungated)
```

## Testing

- **DialogRefParser** (pure unit): bare id; comma list; range `D9:1-D9:17`;
  short range `D9:1-17`; mixed prose + ids; descending/malformed range →
  endpoints; dedup + order; no-id content → empty.
- **Harness**: append/recall dispatch produces correct tool-results; a
  remember-then-append replay reconstructs an **accumulated** memory (both
  chunks present, not clobbered); append-to-missing-path surfaces as a tool
  error (counted), not an abort.
- **Retriever**: a remember+append episode yields a memory whose provenance
  covers **both** cited dialog ids; `GetSurfacedContentsAsync` returns content
  for a memory with **no** resolvable refs (proves the decoupling);
  `_memoriesWithoutRefs` / `_unresolvableRefs` count correctly against parsed
  ids.
- **Runner**: judge is sourced from `ISurfacedContentProvider` when the
  retriever implements it, and from `hits.Text` when it does not.
- **Regression**: full `Scrinia.Retrieval.Benchmarks.Tests` suite green.

## Risks / notes

- **Cross-arm judge comparability:** mechanical arms feed the judge doc-chunk
  text; the agentic arm feeds memory text. Both are "what that store surfaces
  for the query," which is the intended, faithful comparison — not a
  discrepancy to paper over.
- **Range endpoint gaps:** an expanded range may include ids with no
  corresponding doc (session shorter than the cited upper bound). Those are
  counted unresolvable, which is correct for a lower bound.
- **Path-overwrite is addressed, not merely flagged:** Part 1 gives the agent
  append, so path reuse accumulates instead of clobbering. Residual intentional
  overwrites (agent updating a memory) are faithful behavior and left as-is.
- **Uncommitted WIP:** the user has in-flight edits to
  `AgenticIngestPrompts.cs` and other bench files. Implementation must
  integrate with the on-disk state and must not revert or restage unrelated WIP.

## Out of scope (explicit)

Session granularity · LongMemEval · citation-compliance prompt tuning ·
production `Scrinia.Core`/`Scrinia.Mcp` changes · reconstructing past-run
memories.
