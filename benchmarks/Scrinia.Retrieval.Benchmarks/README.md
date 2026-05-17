# Scrinia.Retrieval.Benchmarks

Retrieval-quality benchmark harness for Scrinia. Runs Scrinia's BM25 + (later) embeddings search against academic retrieval benchmarks (ConvoMem, LongMemEval, LoCoMo, MemBench) and reports R@5 / R@10 / R@50 / NDCG@10 with per-category breakdowns.

This is **not** a microbenchmark — for `BenchmarkDotNet` perf measurements see `tests/Scrinia.Benchmarks/`.

## Usage

```
scrinia-bench convomem --data <dir> [--limit N] [--top-k K] [--output file]
```

Datasets are not vendored. Download them yourself (see per-benchmark instructions below) and point `--data` at the directory.

## Benchmarks

### ConvoMem (Salesforce)

Source: <https://huggingface.co/datasets/Salesforce/ConvoMem>

Layout expected at `--data <dir>`:
```
<dir>/
├── user_evidence.json
├── assistant_facts_evidence.json
├── preference_evidence.json
├── changing_evidence.json
├── abstention_evidence.json
└── implicit_connection_evidence.json
```

Each file is `{"evidence_items": [...]}`. The loader yields one question per item, with the haystack built from every message in the item's conversations and ground truth being the verbatim text of `message_evidences` (substring-matched).

```
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  convomem --data ./datasets/convomem/core_benchmark/evidence_questions --limit 50
```

MemPalace's published recall on ConvoMem (50/category, 250 total) is 92.9%. Scrinia's number lands once you run the above.

### LongMemEval (xiaowu0162/longmemeval-cleaned)

Source: <https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned>

The headline benchmark — 500 questions, each with its own per-question haystack of ~53 sessions. Session granularity by default (concatenated user turns per session); turn granularity available via `--granularity turn`.

```
# Download the cleaned dataset
mkdir -p ./datasets/longmemeval
curl -fsSL -o ./datasets/longmemeval/longmemeval_s_cleaned.json \
  https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/main/longmemeval_s_cleaned.json

# Run with embeddings (headline)
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  longmemeval --data ./datasets/longmemeval/longmemeval_s_cleaned.json

# Run BM25-only (apples-to-apples against MemPalace's "raw" track)
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  longmemeval --data ./datasets/longmemeval/longmemeval_s_cleaned.json --mode bm25

# Held-out 450 (drop-in for MemPalace's lme_split_50_450.json)
curl -fsSL -o ./datasets/longmemeval/lme_split_50_450.json \
  https://raw.githubusercontent.com/MemPalace/mempalace/main/benchmarks/lme_split_50_450.json
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  longmemeval --data ./datasets/longmemeval/longmemeval_s_cleaned.json --split held_out
```

MemPalace's published baselines: 96.6% R@5 raw (BM25 + ChromaDB MiniLM embeddings, no LLM). Held-out 450 with their hybrid v4: 98.4% R@5. Scrinia's number on the same 450 held-out questions, BM25 + model2vec MiniLM embeddings, lands once you run the above.

### MemBench (ACL 2025)

Source: <https://github.com/import-myself/Membench>

8,500 items across 10 categories: `simple`, `highlevel`, `knowledge_update`, `comparative`, `conditional`, `noisy`, `aggregative`, `highlevel_rec`, `lowlevel_rec`, `post_processing`. Each item is its own per-question haystack (5–30 turns); ground truth matches against turn `sid`s.

Layout expected at `--data <dir>`:
```
<dir>/
├── simple.json
├── highlevel.json
├── knowledge_update.json
├── comparative.json
├── conditional.json
├── noisy.json
├── aggregative.json
├── highlevel_rec.json
├── lowlevel_rec.json
└── post_processing.json
```

```
git clone https://github.com/import-myself/Membench /tmp/membench
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  membench --data /tmp/membench/MemData/FirstAgent
```

MemPalace's published baseline: 80.3% R@5 overall, with `noisy` at 43.4% as the designed hard case (distractors mixed in with signal). Scrinia's category breakdown lands once you run the above.

### LoCoMo (snap-research/locomo)

Source: <https://github.com/snap-research/locomo/blob/main/data/locomo10.json>

10 ultra-long conversations × ~200 QA pairs each = 1986 questions. Per-conversation haystack (one fresh store per `sample_id`); same haystack drives every QA in that conversation. Session-granularity matches MemPalace's primary report (concatenated dialogs per session, doc_id = `session_N`). Dialog granularity is per-turn (`D1:3` etc.).

```
git clone https://github.com/snap-research/locomo /tmp/locomo
dotnet run -c Release --project benchmarks/Scrinia.Retrieval.Benchmarks -- \
  locomo --data /tmp/locomo/data/locomo10.json
```

We report **R@5, R@10, and R@50** side-by-side so the K=50 caveat (MemPalace flagged it as structurally guaranteed when K exceeds the conversation's session count + an LLM rerank consumes the candidate set) can be settled with data rather than theory — Scrinia's headline runs have no LLM rerank, so ranker ordering still drives R@50.

MemPalace's published headline (top-10, no rerank): 60.3% R@10 raw, 88.9% R@10 hybrid v5.

## Output

Results are written to `benchmarks/Scrinia.Retrieval.Benchmarks/results/scrinia_<bench>_<mode>_<UTC>.json` by default. The schema mirrors MemPalace's per-question result files so a separate compare tool can diff per-question hits across systems without a schema translator.

## Modes

- `bm25`: pure BM25 baseline (Scrinia's `WeightedFieldScorer` without embeddings).
- `bm25_embeddings` *(default)*: BM25 + MiniLM embeddings via model2vec, blended via the same `{scope}|{name}` supplemental-score path the production embeddings plugin uses. Falls back to `bm25` if the model isn't on disk (run `scri setup` first).
- Ablation knobs (`--alpha-recency`, `--tau-days`, `--diversity-lambda`) wired in commit 5.

Headline runs zero out the workspace-tuned ranker (`α_recency = α_importance = 0`, MMR off) so the apples-to-apples comparison against MemPalace's `--mode raw` is meaningful. The ablation commit turns those knobs on so we can measure their effect.

## Why not just trust MemPalace's numbers?

We want a Scrinia-specific headline that's auditable end-to-end on our own code. Both systems use MiniLM-L6-v2 (MemPalace via ChromaDB's default sentence-transformer, Scrinia via model2vec/Vulkan-GGUF), so the BM25 + embeddings track is apples-to-apples. Where Scrinia diverges from MemPalace, the deltas tell us something specific about our architecture.
