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

### LongMemEval, LoCoMo, MemBench

Coming in subsequent commits. Will share the same `scrinia-bench` CLI, the same `IRetriever` abstraction, and the same JSON output schema.

## Output

Results are written to `benchmarks/Scrinia.Retrieval.Benchmarks/results/scrinia_<bench>_<mode>_<UTC>.json` by default. The schema mirrors MemPalace's per-question result files so a separate compare tool can diff per-question hits across systems without a schema translator.

## Modes

- `bm25`: pure BM25 baseline (Scrinia's `WeightedFieldScorer` without embeddings). Default for commit 1.
- `bm25_embeddings`: BM25 + MiniLM embeddings via model2vec. Added in commit 2.
- Ablation knobs (`--alpha-recency`, `--tau-days`, `--diversity-lambda`) wired in commit 5.

## Why not just trust MemPalace's numbers?

We want a Scrinia-specific headline that's auditable end-to-end on our own code. Both systems use MiniLM-L6-v2 (MemPalace via ChromaDB's default sentence-transformer, Scrinia via model2vec/Vulkan-GGUF), so the BM25 + embeddings track is apples-to-apples. Where Scrinia diverges from MemPalace, the deltas tell us something specific about our architecture.
