# kompress Arm B — LongMemEval answer-content proxy

**Date:** 2026-06-27
**Benchmark:** LongMemEval dev split, 50 questions, `--mode bm25_embeddings --top-k 50`.
**Arm B** = retrieve on the **original (uncompressed) index**, then compress the **retrieved passages** at `target_ratio` before measuring. Because the index is untouched, R@k is a built-in control (must match baseline). The signal is **answer-content recall**: the fraction of gold-answer content tokens (stopword-filtered, via `TextAnalysis.Tokenize`) present in the compressed recalled passages.

**Metric note:** the first cut measured answer-content recall as a *union over all top-50* passages — it was **saturated** (flat 0.804 regardless of ratio), because an answer token only disappears if compression drops it from *every* one of 50 redundant passages. Refined to **top-5** (the realistic recall budget), the metric is sensitive (baseline 0.608) and the numbers below use it.

## Results

| Run | R@5 | R@10 | R@50 | NDCG@10 | AnswerContentRecall (top-5) | ~Δ recalled size |
|---|---|---|---|---|---|---|
| baseline (off) | 94.0% | 98.0% | 100.0% | 0.906 | 0.608 | — |
| arm-b @0.8 | 94.0% | 98.0% | 100.0% | 0.906 | 0.608 | ~−14% |
| arm-b @0.7 | 94.0% | 98.0% | 100.0% | 0.906 | 0.608 | ~−22% (measured exactly in Task 8) |
| arm-b @0.6 | 94.0% | 98.0% | 100.0% | 0.906 | 0.606 | ~−30% |
| arm-b @0.5 | 94.0% | 98.0% | 100.0% | 0.906 | 0.598 | ~−40% |

R@k/NDCG are bit-identical across all runs — the arm-b index is provably uncompressed (control holds).

## Read

**Compression preserves answer-bearing content very well.** Even with the sensitive top-5 metric, answer-content recall is flat through `target_ratio` 0.7 (zero loss at −14% / −22% size) and dips only **−1.6% relative** (0.608 → 0.598) at the most aggressive 0.5 (−40% size). kompress is doing exactly what it's designed to do: drop low-information/filler tokens, keep the high-information ones — which are the answer-bearing tokens.

**Contrast with Arm A.** Arm A (compress at *ingest* → the index changes) cost ~3 points of R@5 on LoCoMo. Arm B (compress at *recall* → index intact) costs essentially nothing on retrieval *or* answer content, for the same size savings. So if kompress were used at all, the Arm B shape — compress what you hand the agent, not what you index — is technically the better design.

## Combined verdict (Arm A + Arm B)

The technology is sound: kompress compresses recalled context by ~14–40% with minimal answer-content loss. But **neither integration shape earns its place against the project's priors:**

- **Arm A** keeps the read path model-free but taxes retrieval (~3 pt R@5) and adds a **write-path model** (~600 MB ModernBERT + ONNX + batch infra).
- **Arm B** preserves retrieval and answer content, but compresses *per query at recall time* — a **read-path model** with per-query latency (~13 s cold model load; would need a persistent server to amortize). That is precisely the read-time-LLM dependency the project removed with LLM-at-rest.

Either way, a **modest context saving for a model dependency the project deliberately shed** — and the lossless levers already in hand (chunk-scoped recall, concise-write guidance) cover much of the same ground without dropping a token or adding a model.

**Conclusion: no-go for integration.** Evidence-backed and clean — not because kompress fails (it works well), but because the cost/benefit doesn't beat the lossless status quo given *less chaos, more nature*. The reusable byproduct stands: a text-processing seam + recalled-size + answer-content metrics in the bench, available if context cost ever becomes a sharper pain point.

**Caveats:** dev split is 50 questions; a full-split (500q) confirmation would harden the numbers before any reversal. The answer-content-recall proxy (token overlap) is still weaker than a real LLM answer+judge — it shows the answer *content survives*, not that an LLM *answers correctly* from it. Both were deliberate cost trade-offs.
