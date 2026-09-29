# Faithful Temporal Grounding + Judge Replay & Ranking — Design

**Date:** 2026-07-19
**Status:** Approved (design), pending implementation plan
**Scope:** One cohesive change spanning the production MCP search surface and the LoCoMo retrieval benchmark. The bench change is only legitimate *because* it mirrors the production change, so the two are designed and reviewed together.

## Background & motivation

Investigation of the `scrinia_locomo` result corpus surfaced several intertwined facts:

1. **The scorer was fixed in commit `87dc093`** (2026-05-28, "align loaders, scorer, and parallel infra with upstream evals"), 236 commits back from HEAD `155a208` on all-ancestry (83 on first-parent). It changed LoCoMo recall from any-hit to fractional (`found / len(evidence_ids)`). Every pre-fix number (~0.86–0.90) is inflated and not comparable to post-fix numbers.

2. **The answerability judge has run on only 2 of ~70 LoCoMo result files.** Every other run has `Aggregate.Answerable = 0.0` because `--judge` was not passed. Ranking by the judge metric today would rank a corpus of two.

3. **Result files already store each question's `Retrieved` hits and gold texts.** Retrieval is the expensive, non-deterministic step, and it is already persisted. So the judge pass can be replayed offline over saved contents without re-running retrieval.

4. **Microsoft's Memora LoCoMo runner does not score retrieval recall at all** — it scores generated-answer quality (BLEU / F1 / LLM-judge). Its answer prompt injects memory **timestamps** and instructs the model to convert relative references ("yesterday", "last year") to absolute dates. That temporal grounding lives in the *runner harness*, not the memory tool.

5. **Our harness is temporally blind.** `JudgePrompts.AnswererUser` emits bare numbered passages with no dates and no conversion instruction. The judge cache shows 72 answers that are raw relative references ("Yesterday", "Last week", "Ten years ago") — each graded `incorrect` against absolute-date gold. We are not failing to *retrieve*; we are surfacing "I went yesterday" with no date to anchor it.

6. **The real `search` surface is also date-blind.** `CreatedAt` reaches agent-facing text in exactly one place — `BuildFullList` (the `list` action table, `MemoryTools.Core.cs:748`). The `search` result block (`AppendResultBlock`) renders label/score/tokens/attributes/description/anchors — **no date**. So the current bench faithfully mirrors a genuine product gap; it is not "unfaithful" today.

7. **Dates are not attribute-driven yet.** `KnownFields` recognizes no `created`/`date`/`when` key (only person/place/org/thing/topic/file/ref/review/tag/anchor). `CreatedAt`/`UpdatedAt` are structural entry fields (`ArtifactEntry`, `EphemeralEntry`) never folded into the `Attributes` dictionary, so `AppendAttributesLine` cannot show them.

### The core principle

The fix is **not** to prompt-engineer the answerer (Memora's approach). Coaching a token predictor to override its default verbatim-recall behavior measures the coaching, not the memory system — the same "optimize the run conditions" trap we are avoiding. Instead we make the memory surface carry the metadata a real deployment would carry, through the same channel production uses, and let the answer fall where it may. A residual failure then reflects a real end-to-end weakness, not a missing prompt.

## Goals

- Production `search` results surface record dates the same way `list` does — as pseudo-attributes derived from structural `CreatedAt`/`UpdatedAt`.
- The LoCoMo bench mirrors that surface, seeding each memory's effective creation date from the dataset session timestamp under an explicit, recorded "real-time turn-granularity ingest" scenario.
- A judge score can be produced for the historical result corpus by replaying only the LLM-judge pass over already-saved retrieval, without re-running retrieval.
- Runs are rankable by the judge metric, and the ambiguity between granularities/scenarios is eliminated by recording them in `RunMetadata`.
- Every temporal failure is attributable to the layer that caused it (retrieval surfaced the date vs. answerer resolved it).

## Non-goals

- No LoCoMo-specific answer-prompt coaching. The answerer prompt stays general.
- The date does **not** enter the retrieval scoring index (BM25/embeddings). It is display/answerer metadata only.
- No agent-written `created:`/`updated:` attributes. Dates are entry-intrinsic facts derived from structural fields, not agent assertions.
- No change to the fixed LoCoMo scorer semantics from `87dc093`.
- No dataset mutation; the LoCoMo dataset stays as-is.

## Components

### A. Production — date pseudo-attributes on search results

`AppendResultBlock` (`MemoryTools.Core.cs`) renders `created` and (when non-null) `updated` as pseudo-attributes for `EntryResult` and `ChunkEntryResult`, derived from `Entry.CreatedAt` / `Entry.UpdatedAt`. These are **display-derived**, not stored in `Attributes` — the agent cannot write them, and they never round-trip into storage or scoring.

- Format: `created: 2023-05-03` — ISO date, date-only, no time component. `updated: <date>` shown only when `UpdatedAt` is non-null.
- Placement: alongside the existing attributes block, so `list` and `search` become consistent.
- The pseudo-attributes are presentation only; the JSON/structural model is unchanged.

### B. Bench — seed `created` from dataset session dates

The `LoCoMoLoader` stamps each ingested memory with an effective creation date taken from `session_N_date_time`, under the stated scenario: Scrinia ingests turn-by-turn in real time, so a memory's creation time ≈ the turn's time ≈ its session date. This reconstructs the exact reference frame LoCoMo's authors used to build the gold answers.

- **Session granularity:** the session doc carries its own `session_N_date_time`.
- **Dialog granularity:** each turn doc (`D{n}:{i}`) carries its parent session `n`'s date.
- The date is surfaced to the answerer through the same pseudo-attribute mechanism as production (Component A), **not** indexed for retrieval scoring.
- `updated` is not applicable in the bench (LoCoMo memories are ingested once, never revised) — only `created` is surfaced.

### C. `RunMetadata` — record granularity and temporal scenario

Extend the shared `RunMetadata` record with nullable fields, null for benchmarks/runs where the concept does not apply:

- `Granularity` (e.g. `"session"` / `"dialog"` for LoCoMo; null elsewhere) — eliminates the current invisible ambiguity where the same commit produced 0.55 and 0.84 with no recorded difference.
- `TemporalScenario` (e.g. `"session-date-ingest"` when Component B seeded dates; null when dates were not seeded) — makes the scenario auditable and prevents anyone reading a temporally-grounded number as un-augmented.

Both `RunAsync` and `RunGroupedAsync` construction sites in `BenchmarkRunner` populate these from values threaded down from the command layer.

### D. Forward-looking judge replay

Replay is **forward-looking**, not a reconstruction of old files. Every run — regardless of whether a judge runs live — assembles and **persists** the exact answerer input per question, so replay is a pure judge re-run with no dataset and no retriever, giving exact live/replay parity. This replaces an earlier reconstruct-from-`locomo10.json` design whose gold-answer rejoin (fragile `_qa{N}` alignment), date re-hydration, and agentic content-source gap were all compromises; persisting the context at run time removes every one of them.

Two additions make it work:

1. **Run-time persistence (always on, every benchmark).** The runner builds the date-prefixed answerer context (top-JudgeK, `JudgeK` default 10) via the same path the judge uses, and writes it plus the gold answer onto each `QuestionResult` — new fields `JudgeContext` (the exact passages) and `GoldAnswer`. This is free for id-mapped retrievers and one extra local (no-LLM) search per question for the agentic arm. It doubles as an honest record of what the answerer saw.

2. **The replay command** reads a result file and, for each question that has a persisted `JudgeContext` + `GoldAnswer`, runs **only** the judge (answerer + grader, reusing `AnswerabilityJudge` and its cache), writing `SurfacedFromContext` / `AnsweredCorrectly` / `JudgeAnswer` / `JudgeGraderVerdict` / `DateSurfaced` and refreshing the aggregate three-state rates / `PerCategory` (Component F) to a sibling `*.judged.json`. Cost is the LLM judge calls alone.

Runs produced before this change lack `JudgeContext`; those questions are **skipped and the count logged** — pre-existing files are intentionally not replay-targeted (only two exist, both agentic-ingest, and reconstructing them would reintroduce the compromises above).

### E. Ranking — thin reader

A thin reader over `Aggregate.CorrectRate`/`SurfacedRate` (and per-category) that ranks result files, working uniformly for freshly-`--judge`'d runs and Component-D-replayed runs, and tolerantly falling back to the legacy `Answerable` field for un-rejudged historical files. Surfaced via the same script entry point. Output includes granularity and temporal-scenario columns from `RunMetadata` so incomparable runs are visibly flagged rather than silently mixed.

### F. Per-category surfaced/correct attribution (all categories)

Every question is attributed to one of three states, reported per-category and overall, so a single correctness number never conflates a retrieval miss with an answering miss:

- **not-surfaced** — the answerer emitted the `"NOT ANSWERABLE FROM CONTEXT"` sentinel; the context did not contain enough to answer (retrieval/surface layer — Scrinia's job).
- **surfaced-but-wrong** — the answerer produced an answer but the grader marked it incorrect (answerer/grounding layer — the harness LLM, a small model, so a floor since production uses a frontier agent).
- **correct** — answered and graded correct.

This generalizes what was originally a temporal-only idea. Evidence from the one judged `agentic-ingest` run (1,538 graded Q): overall 37.5% correct / 22.6% surfaced-but-wrong / 40.0% not-surfaced — and the split is category-diagnostic (temporal 39.3% surfaced-but-wrong = the grounding loss Components A/B recover; single_hop 46.1% surfaced-but-wrong = an answerer issue invisible to recall; temporal_inference 71.7% not-surfaced = a retrieval issue).

Mechanics and boundaries:

- **The signal already exists but is collapsed.** `AnswerabilityJudge.JudgeAsync` sets `answerable = ParseGrade(graderReply)` — i.e. today's `Answerable` field is really *graded-correct*, and the answerer's NOT-ANSWERABLE sentinel is only recoverable from `JudgeAnswer` text. Fix: capture surfacing as a structured field.
- **Schema change:** split the misnamed `Answerable` into `SurfacedFromContext` (bool — answerer did not emit the sentinel) and `AnsweredCorrectly` (bool — grader verdict, the rename of `Answerable`). Aggregates gain the three-state rates per category. The ranking reader (E) accepts the legacy `Answerable` name for un-rejudged historical files.
- **"Surfaced" is the answerer's opinion, not ground truth** — a small answerer can hallucinate on insufficient context (false surfaced) or over-emit NOT-ANSWERABLE (false not-surfaced). It is the only faithful surfacing signal for rewritten/agentic memory (where id-recall and substring checks fail — `GroundTruthTexts` is empty in result files and agentic ids don't match gold), but it complements rather than replaces id-recall.
- **Deterministic date-presence sub-diagnostic (temporal only):** additionally compute, without an LLM, whether the surfaced context literally contained the resolvable `created` date. This isolates "Scrinia put the date in context" (deterministic, model-independent) from "the answerer thought it could answer" (the LLM sentinel above). Kept as a temporal sub-signal, not the general mechanism.

Net: three complementary lenses — **id-recall** (deterministic retrieval), **SurfacedFromContext** (answerer's view of sufficiency), **AnsweredCorrectly** (grader) — each answering a distinct question; plus a deterministic date-presence check for temporal.

## Data flow (bench, live run)

```
locomo10.json
  → LoCoMoLoader (granularity + session-date stamping, Component B)
     → haystack docs, each carrying effective created date (metadata, not indexed)
  → retriever.Ingest / Retrieve  (date NOT in scoring path)
     → hits
  → answerer context = surfaced text + `created:` pseudo-attribute (Component A mechanism)
     → persist as QuestionResult.JudgeContext + GoldAnswer (always — enables Component D)
     → AnswerabilityJudge (uncoached, when --judge) → SurfacedFromContext + AnsweredCorrectly (Component F)
     → temporal date-presence sub-diagnostic (deterministic)
  → RunMetadata records Granularity + TemporalScenario (Component C)
```

## Data flow (forward replay, Component D)

```
run time (always): assemble date-prefixed answerer context (top-JudgeK) + gold answer
  → persist as QuestionResult.JudgeContext + GoldAnswer  (whether or not a judge runs live)

replay: *.json (JudgeContext + GoldAnswer persisted)
  → judge answerer + grader over the persisted context   (no dataset, no retriever)
  → SurfacedFromContext + AnsweredCorrectly + DateSurfaced
  → write *.judged.json ; refresh aggregates
  → ranking reader (Component E)
  (questions lacking JudgeContext — pre-plan runs — are skipped + logged)
```

## Faithfulness guardrails (explicit)

- Date is **display/answerer metadata only** — never a retrieval-scoring term, in production or bench. Verified by keeping it out of `TextAnalysis`/embedding input.
- Answerer prompt is **general** — no LoCoMo phrasing, gold format, or "convert last year to 2022" coaching.
- The temporal scenario is **recorded** in `RunMetadata`, never silent.
- Pseudo-attributes are **derived from structural fields**, not agent-writable — dates cannot be gamed by ingestion.
- The answerer model is **fixed and documented**; temporal answerability is interpreted as a floor given the small judge-answerer.

## Testing

- **A (production):** unit test that `search` result block renders `created:` (and `updated:` when set) from entry fields; test that these are absent from stored `Attributes` and from the retrieval index.
- **B (bench loader):** test that session/dialog docs receive the correct `session_N_date_time`-derived date; test that the date is not tokenized into BM25/embedding input (retrieval scores unchanged with vs. without date seeding).
- **C (metadata):** round-trip test that `Granularity`/`TemporalScenario` serialize and are null for non-applicable benchmarks.
- **D (replay):** test gold-answer rejoin by `QuestionId`; test date re-hydration for both id shapes; test that replay over a fixture file with a stubbed judge produces the expected `Answerable` and a `*.judged.json` that preserves the original.
- **F (attribution):** test that `SurfacedFromContext` is false iff the answerer emitted the NOT-ANSWERABLE sentinel, and `AnsweredCorrectly` tracks the grader; test the three-state aggregation; test the ranking reader accepts the legacy `Answerable` field name. Deterministic test that the temporal date-presence sub-diagnostic is true iff the `created` line is present in surfaced context.
- Regression: existing LoCoMo scorer semantics (`87dc093`) unchanged; recall numbers for a fixture run identical before/after this change.

## Decisions locked during design

- Pseudo-attributes derived from structural `CreatedAt`/`UpdatedAt`, not agent-written. Agree.
- Bench seeds `created` from session dates under a real-time turn-granularity ingest scenario; `updated` not surfaced in bench. Agree.
- Judge fork = **C (both)**: build offline replay (D), ranking is a thin reader (E) over whatever judge data is present.
- Reject Memora-style answer-prompt coaching; prefer scenario-date grounding.
- Format: `created: 2023-05-03`, ISO date-only; `updated:` only when non-null.
- One spec covering production surface + bench seeding + replay/ranking + metadata, reviewed together.
- Component F generalized from a temporal-only date check to a universal per-category three-state attribution (not-surfaced / surfaced-but-wrong / correct); `Answerable` split into `SurfacedFromContext` + `AnsweredCorrectly`; id-recall retained as the deterministic retrieval lens; deterministic date-presence kept as a temporal sub-diagnostic.
- Replay is **forward-looking**: every run persists `JudgeContext` + `GoldAnswer` per question (always on, all benchmarks), so replay is a dataset-free judge re-run with exact live/replay parity. This drops the `locomo10.json` rejoin, `_qa{N}` alignment, date re-hydration, and the agentic content-source gap. Pre-plan files (2, both agentic) are not replay-targeted — skipped + logged. Both answerer paths (id-mapped + agentic) surface dates; nothing deferred.
