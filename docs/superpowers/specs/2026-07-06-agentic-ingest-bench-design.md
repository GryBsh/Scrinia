# Agentic Ingestion Benchmark Arm — Design

**Date:** 2026-07-06
**Status:** Approved (design review with user)

## Motivation

Every existing benchmark arm ingests mechanically: `ScriniaRetriever.IngestAsync` upserts each
haystack doc verbatim (optionally decorated with LLM-generated anchors / typed keywords). That
measures the *store's* ranking under ideal ingestion — it cannot see the value or damage of what
a real agent actually does during a session: choosing paths, writing descriptions, summarizing,
splitting and merging sessions into memories, self-reporting fields.

This arm makes ingestion itself the variable under test: an LLM is given the **real Scrinia MCP
tool surface** and the **real `guide()` text**, and ingests the haystack the way a live session
would. Everything downstream (search, scoring, metrics) stays mechanical and comparable.

## Decisions (from design review)

| Question | Decision |
|---|---|
| Scoring model | Provenance-mapped doc-unit R@k — agent ingests freely; memories carry source doc IDs; retrieved memories expand back to doc IDs so the existing scorer and metric unit are unchanged. |
| Agent vehicle | In-bench chat + tool-calling loop against any OpenAI-compatible endpoint; tool calls execute the **real `Scrinia.Mcp.MemoryTools` code in-process**. No agent-CLI spawning. |
| Query side | Mechanical `SearchAll` with the benchmark question — isolates ingestion as the single variable. Agentic search is a possible later arm. |
| Ingest flow | One agent episode per conversation session ("this conversation just happened — store what's worth remembering"), not turn-by-turn replay and not a whole-haystack dump. |
| Architecture | New `AgenticScriniaRetriever : IRetriever` slotting into the existing runner/commands, with a persistent episode cache. No separate pre-pass command. |

## Architecture

### Data flow

1. `BenchmarkRunner` feeds docs through `IngestAsync` as today. `AgenticScriniaRetriever` only
   **buffers** them, grouped by session.
2. On first `RetrieveAsync` for a question, the retriever runs one **episode** per session (in
   ingest order), executing the agent's `memory_remember` calls against a real per-question
   `FileMemoryStore` workspace (temp dir, same lifecycle as `ScriniaRetriever`'s per-question
   stores, `MemoryStoreContext.Current` set for the dispatch).
3. The question is searched mechanically (`SearchAll`, default `RankerOptions`); returned
   memories are expanded to doc-unit hits via provenance (below).

### Session grouping

`HaystackDoc` gains an optional `SessionId`. Loaders populate it where docs are sub-session
slices (LoCoMo `--granularity dialog`, LongMemEval `--granularity turn`) so an episode sees the
whole session. When `SessionId` is null — `--granularity session`, where one doc already *is* a
session — each doc is its own episode. No grouping heuristics on doc-ID strings.

### Components

- **`Agent/AgenticIngestHarness`** (new) — runs one episode: builds the prompt, drives the
  tool-call loop, dispatches tool calls to `MemoryTools`, enforces the turn cap, records the
  tool-call sequence for the cache.
- **`Core/AgenticScriniaRetriever`** (new) — `IRetriever`; buffering, episode orchestration,
  store lifecycle, provenance expansion, per-run stats.
- **`Llm/Providers`: tool-calling support** (new) — `ChatWithToolsAsync` on
  `OpenAiCompatibleBenchLlmClient` speaking the OpenAI `tools` / `tool_calls` /
  `role:"tool"` wire format (llama.cpp `--jinja`, vLLM, OpenAI all speak it). Message and
  tool-schema types added to `LlmJsonContext`. Other providers may throw
  `NotSupportedException` in v1.
- **`Llm/EpisodeCache`** (new) — persistent ndjson cache of episode tool-call sequences (see
  Cache section).
- **Project reference:** `Scrinia.Retrieval.Benchmarks` → `Scrinia.Mcp` (currently only
  `Scrinia.Core`).

## Episode protocol

- **System prompt:** the real guide text (`EmbeddedPrompts.LoadGuide()`) + a short bench
  addendum:
  - "The conversation below just happened. Store what is worth remembering for future
    questions about it, using the memory tools."
  - "Every memory's content MUST include `ref: <doc-id>` lines naming the labeled turns it
    draws from."
- **User message:** the session transcript, each turn prefixed with its doc ID label,
  e.g. `[conv5_sess3_msg12] Maria: We finally booked the trip to Lisbon.` Timestamps included
  when the dataset has them.
- **Tools exposed:** `memory_remember` and `memory_search` (search lets the agent consolidate
  across earlier sessions of the same conversation, as a real session would). Tool schemas are
  derived from the real `MemoryTools` signatures — same names, same parameter descriptions.
  `memory_append`/update flows are deferred.
- **Loop:** assistant turn → execute any tool calls in order → return real tool responses
  (the YAML the production code emits) as `role:"tool"` messages → repeat until the model stops
  calling tools or the turn cap is hit (`--episode-max-turns`, default 16).
- **Dispatch is the production write path:** path validation, chunking, field extraction
  (including the `ref:` lines → `Fields["ref"]`), TF seeding — all of it is what's measured.

## Provenance and scoring

- The `ref: <doc-id>` lines ride the production `FieldExtractor` into `Fields["ref"]` — no
  bench-only schema deviation, and the unified-fields path is exercised for free.
- At retrieve time, each returned memory is **expanded into doc-unit hits**: its cited doc IDs
  in cited order, each inheriting the memory's score; duplicates across memories are removed
  (first occurrence wins); then top-K is taken over docs. `BenchmarkScorer` is untouched and
  the metric unit remains *doc* — numbers are directly comparable with mechanical arms.
- A memory whose ref set is empty or unresolvable cannot map to any doc. Such memories are
  **counted and logged loudly** (per-episode and in the run summary) — that is ergonomics
  signal, not noise. Unresolvable refs (IDs not in the haystack) are counted separately.

## Episode cache

- Persistent ndjson alongside the existing LLM response caches
  (`benchmarks/Scrinia.Retrieval.Benchmarks/cache/`), one file per provider+model.
- **Key:** hash(model + prompt-version salt + full session transcript). Bump the salt whenever
  the system prompt or tool schemas change.
- **Value:** the ordered tool-call sequence (tool name + raw JSON args) the episode produced.
  Replay executes the calls against a fresh store without touching the LLM — sweeps and
  re-runs are cheap, and the cache file doubles as the inspectable artifact of what the agent
  chose to remember.
- **Hard rules** (from the typed-keywords null-cache incident):
  - Failed episodes are **never cached**.
  - Every swallowed failure (timeout, transport error, empty completion, malformed tool call)
    is logged with its cause at the point it is swallowed.

## Failure handling

- An episode that errors or times out is retried once; on second failure the session's docs are
  ingested **mechanically** (plain `ScriniaRetriever`-style upsert) so one bad session doesn't
  poison the run. Fallback ingests are marked and counted.
- Malformed tool calls (bad JSON args, unknown tool) get the production error response returned
  to the model as the tool result — the model gets a chance to self-correct within the turn cap.
- **Run summary reports:** episodes agentic vs fallback, memories written, memories without
  refs, unresolvable refs, tool-call errors, LLM turns, wall-clock per episode.

## CLI

- New flag `--agentic-ingest` on `locomo` and `longmemeval` commands.
- Reuses existing `--agent-provider` / `--agent-model` / `--agent-base-url` plumbing for the
  episode LLM endpoint.
- One new knob: `--episode-max-turns` (default 16).
- Wired in `RetrieverFactory`: flag set → `AgenticScriniaRetriever` (mutually exclusive with
  `--agent` query-side harness decorations in v1; fail fast with a clear message if combined).

## Testing

Unit tests drive the harness with a scripted fake tool-calling client (deterministic tool-call
sequences), no network:

1. Episode executes `memory_remember` calls → entries land in the store with fields extracted
   (`Fields["ref"]` populated from `ref:` lines).
2. Provenance expansion: cited order preserved, duplicate docs deduped across memories, top-K
   over docs.
3. Cache replay round-trip: replayed store state produces identical retrieval results to the
   live episode.
4. Fallback path: scripted double-failure → session ingested mechanically, counted as fallback.
5. No-ref memory counted and logged; unresolvable ref counted separately.
6. Turn cap enforced; malformed tool call returns the production error to the model.

Real-endpoint runs remain manual (`run-benchmarks.ps1` with the new flag).

## Out of scope (possible later arms)

- Agentic search/answering at question time (QA-judge scoring).
- Turn-by-turn live-session replay with the per-turn hint.
- `memory_append`/update/compact in episodes (cross-session consolidation beyond
  search-then-remember).
- Agent-CLI-driven spot-check mode over real MCP stdio.
