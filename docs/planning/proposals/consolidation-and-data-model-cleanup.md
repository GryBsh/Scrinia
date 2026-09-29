# Consolidation + data-model cleanup — SUPERSEDED

**Status:** superseded 2026-05-25 by the `linked-forging-dove` plan
(`~/.claude/plans/linked-forging-dove.md`, outside the repo). The proposal
below was a draft sketch; the landed work expanded the scope and changed
several decisions, so what shipped does not match what was originally proposed.
Use this file as a historical pointer only.

## What actually shipped

- **LLM-at-rest enrichment gone.** `LlmConsolidator`, `ImportanceScoringSink`,
  `LlmPrompts`, the `McpLlmPluginHost`, and all three local-LLM plugin projects
  (`Scrinia.Plugin.Llm`, `Scrinia.Plugin.Llm.Cli`, `Scrinia.Plugin.Llama`) were
  deleted. `Scrinia.Core` no longer ships any LLM client code; the server does
  not consult an LLM at write or read time.
- **`Facts` → `RetrievalAnchors`.** The `Facts: string[]?` field on
  `ArtifactEntry` was renamed to `RetrievalAnchors`. Anchors are now
  agent-supplied via a new `anchors` parameter on `memory_remember` (the field
  scorer's +15 weight and the TF-seeding at +2 carried over unchanged). There is
  no LLM extraction path in production — the agent calling scrinia is expected
  to supply the anchors at write time.
- **Bench-side agent harness.** The LLM clients were moved into the benchmarks
  project (`Scrinia.Retrieval.Benchmarks.Llm.Providers`) and wrapped in a new
  agent harness (`--agent <off|anchors|rewrite|hyde|all>`) that simulates a
  production agent for end-to-end retrieval evaluation. See
  `docs/research/agent-harness.md`.
- **`scri consolidate` is mechanical-only.** `--with-llm` is gone, `--importance`
  is gone, and the `SessionEnd` consolidate hook is no longer installed by
  `scri setup --hooks`. Run `scri consolidate` manually when you want session
  compaction.
