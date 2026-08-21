# Bench agent harness

The retrieval benchmark suite (`scrinia-bench` under
`benchmarks/Scrinia.Retrieval.Benchmarks/`) ships an opt-in **agent harness**
that wraps the production retriever with three LLM-driven ops — anchor
generation at ingest, query rewriting at search, and HyDE (hypothetical
document embeddings). It exists to bridge a gap between what published memory
benchmarks measure and what our raw substrate measures.

## Purpose

MemBench, LongMemEval, LoCoMo, and ConvoMem are end-to-end agent QA
evaluations. Their published numbers reflect an LLM agent doing query
rewriting, decomposition, and reasoning over retrieved context — not just the
retriever in isolation. Our `BenchmarkRunner` historically passed
`q.QueryText` raw to the retriever, so we were comparing "scrinia + nothing"
against published "scrinia-class system + agent." The substrate measurement is
useful, but it's a different number from what the field reports.

Worse, MemBench analysis showed reranking was the wrong lever entirely. R@5,
R@10, and NDCG@10 are **identical** with and without the sentence-MaxSim
reranker on our runs — the candidate pool is missing the relevant memory on
hard categories (`RecMultiSession`, `lowlevel_rec` sit at 0%), and no
rescoring fixes a pool problem. The right lever is at write time: a
production agent supplies query-vocabulary phrases (anchors) when it persists
a memory, so the retrieval-time BM25/embedding stage has surface area to
match against. The bench harness simulates that production agent so we can
measure the lift directly.

## What it tests

Three independently opt-in ops, controlled by `--agent`:

- **`anchors`** — for each haystack doc, generate 3-5 plausible user-query
  phrases ("how do I rotate session tokens", "what's the policy on expense
  reports") and pass them through to `ArtifactEntry.RetrievalAnchors` at
  ingest. Tests: does the indexer surface memories better when an agent
  supplies query-vocabulary bridges at write time?
- **`rewrite`** — for each query, rewrite the user's natural-language
  question into a more search-friendly form before calling the retriever.
  Tests: does query reformulation by an agent close the vocabulary gap from
  the read side?
- **`hyde`** — generate a hypothetical answer document for each query and
  concatenate it with the original query so the embedding-based retriever
  sees both surfaces (Gao et al., 2022). Tests: does HyDE-style query
  expansion lift recall on semantically oblique queries?

`--agent all` enables all three simultaneously. `--agent off` (default) is
the substrate baseline — every op returns null and the runner code path is
identical to a no-harness run.

## How to run

The agent flags layer on top of any existing benchmark invocation. Anchors
generate per haystack doc, so they dominate runtime on the first pass; the
response cache (see below) makes subsequent passes effectively free.

```powershell
# Anchors-only run against MemBench using a local Ollama qwen2.5 model
./scripts/run-benchmarks.ps1 -Benchmark membench -ExtraArgs @(
  "--agent", "anchors",
  "--agent-provider", "ollama",
  "--agent-model", "qwen2.5:0.5b",
  "--ingest-concurrency", "8"
)

# Query rewrite + HyDE against ConvoMem with a real OpenAI-compat endpoint
./scripts/run-benchmarks.ps1 -Benchmark convomem -ExtraArgs @(
  "--agent", "all",
  "--agent-provider", "openai",
  "--agent-base-url", "https://api.openai.com/v1",
  "--agent-model", "gpt-4o-mini",
  "--agent-api-key", $env:OPENAI_API_KEY
)

# Reuse a logged-in Claude Code subscription via the claude CLI (no API key)
./scripts/run-benchmarks.ps1 -Benchmark longmemeval -ExtraArgs @(
  "--agent", "anchors",
  "--agent-provider", "claude-cli",
  "--agent-model", "sonnet"
)
```

The runner labels output files with the active mode (`__agent-anchors`,
`__agent-rewrite`, `__agent-hyde`, `__agent-all`) so an off / anchors / all
sweep doesn't collide in `benchmarks/.../results/`.

## Supported providers

| `--agent-provider` | Notes | Required flags |
|---|---|---|
| `openai` | Any OpenAI-compatible chat-completions endpoint (real OpenAI, LM Studio, llama.cpp server, vLLM). | `--agent-model`, optional `--agent-base-url`, optional `--agent-api-key` |
| `ollama` | OpenAI-compat shape against `http://localhost:11434/v1` (overridable). No API key. | `--agent-model`, optional `--agent-base-url` |
| `anthropic` | Native Anthropic Messages API. | `--agent-model` (e.g. `claude-sonnet-4-5`), `--agent-api-key` |
| `gemini` | Native Google Gemini generateContent. | `--agent-model` (e.g. `gemini-2.5-flash`), `--agent-api-key` |
| `claude-cli` | Shells out to the `claude` CLI in non-interactive print mode, reusing the user's subscription auth. | optional `--agent-model` |
| `codex-cli` | Same shape against the `codex` CLI. | optional `--agent-model` |
| `copilot-cli` | Same shape against the GitHub Copilot CLI. | optional `--agent-model` |

`AgentClientBuilder.BuildAsync` runs an `IsAvailableAsync` probe on the chosen
backend before the run starts, so a misconfigured provider fails up-front with
a specific message — no silent degrade to a no-op that would produce a
misleading `__agent-anchors` result file.

## Anti-data-leakage design

The harness exists to simulate a production agent that has only the
content-being-ingested or the user-query-being-asked. **The prompts must
never see the benchmark gold answer set.** If they did, the measurement
would be meaningless — we'd be measuring "what if the LLM cheats by reading
the answer key" instead of "what if a production agent supplies anchors based
on the content alone."

The discipline is built into the prompt shapes:

- **Anchor prompt** (`AgentPrompts.AnchorsSystem`) receives only the document
  content. It explicitly instructs the model to imagine plausible user
  queries — not to summarize the content — and to use vocabulary that
  *differs* from the literal content (the indexer already covers
  exact-match). The few-shot examples in the prompt are domain-neutral
  ("how do I undo my last commit", "when did I last visit") so they don't
  bias the model toward our benchmark categories.
- **Rewrite prompt** (`AgentPrompts.RewriteSystem`) receives only the user
  query. It instructs the model to add synonyms and category-level terms but
  to preserve user intent exactly — "do not invent specifics that aren't in
  the question."
- **HyDE prompt** (`AgentPrompts.HydeSystem`) receives only the user query.
  It generates a hypothetical answer, never a gold-derived one.

The contract is documented on `IAgentHarness` and enforced via
`tests/Scrinia.Retrieval.Benchmarks.Tests/LlmAgentHarnessTests.cs`, which
asserts the prompts received by the LLM never contain gold-set markers.

## Caching

Per-op response caching lives at
`benchmarks/Scrinia.Retrieval.Benchmarks/cache/agent-{provider}-{model}.ndjson`.
Keys are SHA-256 of the input scoped by op label (`anchors|<content-hash>`,
`rewrite|<query-hash>`, `hyde|<query-hash>`), so distinct ops never
cross-contaminate. Null results are cached too — a model that fails to produce
parseable anchors for a given doc shouldn't be retried every rerun.

To force a re-run against fresh model output, delete the cache file (or
delete specific entries). Provider+model is part of the filename, so
switching `--agent-model` automatically uses a separate cache file.

## Compounding with production retrieval anchors

What the bench harness generates as anchors at ingest time mirrors what a
production agent (Claude Code, Codex, Copilot, etc.) would pass to scrinia's
MCP `memory_remember` tool via the `anchors` parameter:

```jsonc
// What an agent calling scrinia in production sends
{
  "path": "/findings/auth-refactor",
  "content": ["…"],
  "anchors": ["how do I rotate session tokens", "what's the OAuth grant flow"]
}
```

The bench's `LlmAgentHarness.GenerateAnchorsAsync` produces exactly the kind
of array a production agent would supply. The retriever doesn't distinguish:
both paths feed `ArtifactEntry.RetrievalAnchors`, which the weighted field
scorer matches at +15 per contains-match and which the TF-seeding step picks
up at +2 per token. The bench is a faithful end-to-end simulation of the
production read/write loop, not a different mechanism — which is why we can
read the bench's lift on hard categories as a credible signal for how
production deployments will improve when their agents start populating
`anchors`.
