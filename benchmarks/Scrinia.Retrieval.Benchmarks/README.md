# Scrinia.Retrieval.Benchmarks

Retrieval-quality benchmark harness for Scrinia. Runs Scrinia's BM25 + MiniLM embeddings against academic retrieval benchmarks (ConvoMem, LongMemEval, LoCoMo, MemBench) and reports R@5 / R@10 / R@50 / NDCG@10 with per-category breakdowns.

This is **not** a microbenchmark — for `BenchmarkDotNet` perf measurements see `tests/Scrinia.Benchmarks/`.

## Quickstart

```powershell
# PowerShell — Windows / Mac / Linux with pwsh
./scripts/prepare-datasets.ps1
./scripts/run-benchmarks.ps1
```

```bash
# Bash
./scripts/prepare-datasets.sh
./scripts/run-benchmarks.sh
```

Both scripts are idempotent: re-runs skip already-downloaded datasets and reuse the cached build.

Results land in `benchmarks/Scrinia.Retrieval.Benchmarks/results/scrinia_<bench>_<mode>_<UTC>.json`. The schema mirrors MemPalace's per-question result files so per-question hits can be diffed across systems without a schema translator.

## Selective runs

```powershell
./scripts/run-benchmarks.ps1 -Benchmark longmemeval -Split held_out
./scripts/run-benchmarks.ps1 -Mode bm25                     # BM25-only baseline
./scripts/run-benchmarks.ps1 -ExtraArgs "--alpha-recency","1.0","--tau-days","90"
```

```bash
./scripts/run-benchmarks.sh longmemeval                     # just one benchmark
SPLIT=held_out ./scripts/run-benchmarks.sh longmemeval
MODE=bm25 ./scripts/run-benchmarks.sh                       # BM25-only baseline
./scripts/run-benchmarks.sh -- --alpha-recency 1.0 --tau-days 90    # ablation
```

## What the benchmarks measure

### ConvoMem (Salesforce)

Source: <https://huggingface.co/datasets/Salesforce/ConvoMem>

Six category files (`user_evidence`, `assistant_facts_evidence`, `preference_evidence`, `changing_evidence`, `abstention_evidence`, `implicit_connection_evidence`). Each item ships a question, a small set of messages, and a list of message-text evidences. The loader yields one question per item; ground truth is substring-matched against the retrieved message text.

MemPalace's published recall (50/category, 250 total): **92.9%**.

### LongMemEval (xiaowu0162/longmemeval-cleaned)

Source: <https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned>

The headline benchmark — 500 questions, each with its own per-question haystack of ~53 sessions. Session granularity (concatenated user turns per session) by default; turn granularity via `--granularity turn`. MemPalace's `lme_split_50_450.json` honoured via `--split full|dev|held_out`.

MemPalace's published baselines: **96.6% R@5 raw** (no LLM); **98.4% R@5 on the held-out 450** with their hybrid v4. Scrinia's number on the same 450 questions lands once `run-benchmarks.ps1 -Benchmark longmemeval -Split held_out` finishes.

### MemBench (ACL 2025)

Source: <https://github.com/import-myself/Membench>

8,500 items across 10 categories: `simple`, `highlevel`, `knowledge_update`, `comparative`, `conditional`, `noisy`, `aggregative`, `highlevel_rec`, `lowlevel_rec`, `post_processing`. Each item is its own per-question haystack (5–30 turns); ground truth matches against turn `sid`s.

MemPalace's published baseline: **80.3% R@5 overall**, with `noisy` at 43.4% as the designed hard case.

### LoCoMo (snap-research/locomo)

Source: <https://github.com/snap-research/locomo/blob/main/data/locomo10.json>

10 ultra-long conversations × ~200 QA pairs each = 1986 questions. Per-conversation haystack reuse: the dialogs ingest once per `sample_id` and serve every QA in that conversation. Session granularity (default, doc_id = `session_N`) matches MemPalace's primary report.

We report **R@5, R@10, and R@50** side-by-side. MemPalace flagged their 100% R@50 as structurally guaranteed when K exceeds session count under an LLM rerank — Scrinia's headline mode has no LLM rerank, so ranker ordering still drives R@50 and we want the data, not the assumption.

MemPalace's published headline (top-10, no rerank): **60.3% R@10 raw**, **88.9% R@10 hybrid v5**.

## Modes

- `bm25`: pure BM25 baseline (Scrinia's `WeightedFieldScorer` without embeddings).
- `bm25_embeddings` *(default)*: BM25 + MiniLM embeddings via model2vec, blended via the same `{scope}|{name}` supplemental-score path the production embeddings plugin uses. Falls back to `bm25` if the model isn't on disk (run `scri setup` first to download m2v-MiniLM-L6-v2).

Headline runs zero out the workspace-tuned ranker (`α_recency = 0`, MMR off) so the apples-to-apples comparison against MemPalace's `--mode raw` is meaningful.

## Ablations

The headline numbers come from a deliberately neutral ranker. Ablations turn each Scrinia knob on in isolation. All flags compose with `--mode` and any benchmark subcommand.

| Flag | What it does | Default |
|---|---|---|
| `--alpha-recency <r>` | Weight for exp-decay recency term | 0.0 (off) |
| `--tau-days <τ>` | Recency time-constant | 14 |
| `--diversity-lambda <λ>` | MMR diversity tradeoff [0,1] | 1.0 (no MMR) |
| `--diversity-inner-limit <N>` | MMR candidate pool size | 50 when λ set |

Output filenames pick up tag suffixes (`scrinia_longmemeval_full_bm25_embeddings__rec1.0_tau30_<UTC>.json`) so sweep results don't collide.

## Agentic ingestion arm

The other arms all ingest mechanically: Scrinia writes one entry per haystack doc and the ranker does the rest. The agentic-ingest arm instead measures **ingestion quality** — what happens when a real LLM decides what's worth remembering, the way a live agent actually would.

```powershell
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  locomo --data ./datasets/locomo/locomo10.json \
  --agentic-ingest --agent-provider openai --agent-model gpt-4o-mini
```

- `--agentic-ingest` swaps in `AgenticScriniaRetriever` for the mechanical `ScriniaRetriever`. Each session becomes one **episode**: a real tool-calling loop (`remember` / `search`) against the actual Scrinia MCP tools, capped at `--episode-max-turns <n>` (default 16) and `--episode-max-tokens <n>` per turn (default 4096). No embedder, no reranker — this arm is BM25 + fields only, so it's directly comparable to the BM25 mechanical baseline (`--mode bm25`, no `--agentic-ingest`).
- `--episode-max-tokens` caps reasoning + assistant text + tool-call arguments **together** for one turn. Set it too low and a `remember` carrying real memory content gets truncated mid-arguments (`finish_reason=length`), producing invalid JSON that reads as a bogus malformed tool call — the harness logs a loud `WARNING: ... hit the token cap` when this happens. Reasoning models (which spend part of the budget thinking before the call) may need it raised well above the default.
- `--agentic-ingest` requires `--agent-provider` (OpenAI-compatible providers only — the harness needs real tool-calling support) and is mutually exclusive with `--agent` (that bare flag makes the agent curate attributes + keywords during *mechanical* ingest; here the episode itself *is* the agent).
- Recall is scored the same way as the other arms: each agent-authored memory is mapped back to the haystack doc IDs it cited (via `ref:` provenance fields), so R@k still compares doc IDs.
- Episodes are cached NDJSON at `benchmarks/Scrinia.Retrieval.Benchmarks/cache/episodes-{provider}-{model}.ndjson`, keyed on `(model, prompt version, session transcript)`. Reruns replay the recorded tool calls instead of re-invoking the LLM — required to make repeated per-question sweeps tractable (one episode per session, times hundreds of questions). A session whose episode fails twice (once retried) stores **nothing** — there is deliberately no mechanical-ingestion fallback, because substituting verbatim doc ingest would inflate the arm's recall with non-agentic work. A failed episode honestly degrades recall and is never cached.
- After the run, a stats block prints to stdout: episode count (agentic vs. failed), memories written, memories without a usable `ref:` citation, unresolvable refs, and tool-call errors — read this before trusting the R@k number, since a high failed-episode or tool-call-error count means the model isn't ingesting reliably (often a token-cap or provider issue), not that the agent's curation is bad.

**What to compare it against:** the BM25 mechanical arm on the same dataset (`--mode bm25`, no ablation flags). The delta tells you whether an agent deciding what to remember actually beats Scrinia's own mechanical per-doc ingest — not whether it beats `bm25_embeddings` (a different, unrelated axis: retrieval-time semantic matching vs. ingest-time curation).

## Judge answerability metric

R@k tells you whether the gold document made it into the top-K; it doesn't tell you whether that content is actually *usable* — retrieval can hit the right doc ID and still surface something too thin, stale, or off-topic to answer the question from. `--judge` adds a second, LLM-based metric that measures that directly: **can the top-K retrieved content answer the question at all.**

```powershell
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  longmemeval --data ./datasets/longmemeval/longmemeval_s_cleaned.json \
  --judge --judge-provider openai --judge-model gpt-4o-mini
```

- **Generate-then-grade**: for each question, an ANSWERER call sees the question plus the top `--judge-k` (default 10) retrieved items — no gold answer — and produces an answer; a GRADER call then compares that answer against the gold answer and returns correct/incorrect. Both calls are cached NDJSON (see below), so reruns don't re-pay the LLM cost.
- **One held-constant judge model** drives both the answerer and grader roles, configured via `--judge-provider` / `--judge-model` / `--judge-base-url` / `--judge-api-key`. Keep this model fixed across arms and sweeps so `answerable@k` numbers stay comparable to each other.
- **The judge model must differ from the agent model.** If `--judge-model` matches `--agent-model`, the CLI prints a warning (it does not fail the run) — a model judging content it authored is a self-preference risk that would bias the metric toward whichever arm it wrote.
- **Applies to both arms** (mechanical and `--agentic-ingest`) so `answerable@k` is directly comparable across them, unlike R@k for the agentic arm (see below).
- **Provenance R@k is a lower bound for the agentic arm.** The agentic arm's R@k is computed from `ref:` provenance fields the agent chose to cite when writing a memory — it undercounts any case where the agent's memory answers the question using content it didn't explicitly cite. When `--judge` is on, the agentic arm's R@5/R@10/R@50 lines print with a `(provenance lower bound)` suffix as a reminder; `answerable@k` from the judge is the metric that isn't subject to that undercount.
- Judge verdicts are cached at `benchmarks/Scrinia.Retrieval.Benchmarks/cache/judge-{provider}-{model}.ndjson`, keyed on `(model, question, judge-k, retrieved content)` — separate from the agent/episode caches so judge reruns don't collide with agent-harness caches for the same provider.
- After the run, a summary block prints: `answerable@k` (the aggregate), plus `judged` / `no gold` (questions with no gold answer to grade against, excluded from the aggregate) / `failed` (judge calls that errored, also excluded) counts — check these before trusting the number, since a high `no gold` or `failed` count means the aggregate is over fewer questions than `questions:` implies.

## Methodology

### Metrics

- **R@K**: fraction of questions where at least one ground-truth document is in the top-K. Binary per-question, averaged.
- **NDCG@10**: for single-relevant-doc benchmarks reduces to `1 / log2(rank+1)` if the doc is in top-10, else 0.
- **Per-category**: simple means over each category's question subset.

### Ranker composition

The formula lives in `src/Scrinia.Core/Search/RankerOptions.cs`:

```
total = α_relevance · relevance
      + α_recency   · exp(-Δt_days / τ_days) · 50
```

Where `relevance = fieldScore + normalizedBm25·5 + semanticSimilarity·50` (the last term only when `--mode=bm25_embeddings`).

### Ground-truth matching

Two modes, dispatched by `BenchmarkScorer`:

1. **ID match** (LongMemEval, LoCoMo, MemBench): retrieved doc's ID must equal a ground-truth ID (case-insensitive ordinal).
2. **Substring text match** (ConvoMem): retrieved doc's text must contain a ground-truth evidence string as a case-insensitive substring. ConvoMem doesn't ship stable doc IDs.

### Reproducibility

- **Embeddings are deterministic.** Model2Vec is a quantized averaged-embedding model; same text gives the same vector across runs.
- **No randomness in the harness.** The runner iterates the dataset's natural order; no shuffling, no sampling unless `--limit` is set.
- **MiniLM-L6-v2 across both systems.** MemPalace uses ChromaDB's default sentence-transformer (PyTorch); Scrinia uses model2vec quantized MiniLM-L6-v2 (pure C#). Same architecture, different inference paths — possible small cosine drift on identical text. Documented here so deltas vs MemPalace's numbers aren't surprising.

### Comparing per-question against MemPalace

MemPalace commits their full per-question JSONL result files under `benchmarks/results_*.jsonl`. Both systems write `question_id` consistently, so a per-question diff is straightforward:

```powershell
# Quick sketch — match question_id between our JSON and MemPalace's JSONL.
$ours = (Get-Content scrinia_longmemeval_full_*.json | ConvertFrom-Json).Questions |
        ForEach-Object { "$($_.QuestionId) $($_.RecallAt5)" } | Sort-Object
$theirs = Get-Content results_mempal_raw_session_*.jsonl |
          ForEach-Object { $j = $_ | ConvertFrom-Json; "$($j.qid) $($j.hit_at_5)" } | Sort-Object
Compare-Object $ours $theirs
```

```bash
jq -r '.Questions[] | "\(.QuestionId) \(.RecallAt5)"' scrinia_longmemeval_full_*.json | sort > ours.txt
jq -r '"\(.qid) \(.hit_at_5)"' results_mempal_raw_session_*.jsonl | sort > theirs.txt
diff ours.txt theirs.txt
```

The *which questions* matter more than the *what fraction*.

## Manual invocation (without the scripts)

If you'd rather wire the dataset paths yourself:

```
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  longmemeval --data ./datasets/longmemeval/longmemeval_s_cleaned.json \
  --split held_out --mode bm25_embeddings --top-k 50
```

See `scrinia-bench --help` for the full flag set.

## Why not just trust MemPalace's numbers?

We want a Scrinia-specific headline that's auditable end-to-end on our own code. Both systems use MiniLM-L6-v2, so the BM25 + embeddings track is apples-to-apples. Where Scrinia diverges from MemPalace, the deltas tell us something specific about our architecture — and that's a finding worth knowing, not a number to paper over.
