# kompress Arm A — LoCoMo sweep results

**Date:** 2026-06-27
**Benchmark:** LoCoMo10, 1,982 questions, `--mode bm25_embeddings --top-k 50` (team's headline config).
**Arm A** = compress memory text with `kompress-v2-base` *before indexing* (index + recall both compressed). Sweep over `target_ratio` (keep-fraction; lower = more aggressive). Compression knob is `target_ratio`, not `score_threshold` (the latter is inert on the CPU path — see plan Task 1).

## Results

| Run | R@5 | R@10 | R@50 | NDCG@10 | Mean recalled chars | Est tokens (chars/4) | Δ size vs baseline |
|---|---|---|---|---|---|---|---|
| baseline (off) | 79.5% | 88.5% | 100.0% | 0.716 | 78,905 | 19,726 | — |
| arm-a @0.8 | 77.0% | 87.5% | 100.0% | 0.701 | 67,513 | 16,878 | −14.4% |
| arm-a @0.7 | 76.7% | 87.1% | 100.0% | 0.696 | 61,395 | 15,349 | −22.2% |
| arm-a @0.6 | 77.0% | 87.9% | 100.0% | 0.698 | 54,939 | 13,735 | −30.4% |
| arm-a @0.5 | 76.4% | 87.4% | 100.0% | 0.690 | 47,525 | 11,881 | −39.8% |

Retrieval deltas vs baseline: R@5 −2.5 to −3.1 pts; R@10 −0.6 to −1.4 pts; R@50 unchanged (saturated at 100%); NDCG@10 −0.015 to −0.026.

## Read

**Not "effectively the same," not a blowout — a modest, consistent trade.** There is a real recalled-context reduction (14% → 40% across the sweep) *and* a real, small retrieval cost.

**The retrieval tax is paid up front and then plateaus.** R@5 drops ~2.5–3 pts at the mildest setting (0.8) and barely moves as compression gets more aggressive (0.8 → 0.5: R@5 stays ~76–77, NDCG ~0.69–0.70). Meanwhile size savings nearly triple over that same range (14% → 40%). So the size/quality frontier *favors* aggressive compression — once you pay the fixed tax, more aggression is almost free on retrieval.

**If one were to use it, `target_ratio` 0.6 is the sweet spot:** −30% context for R@5 77.0 / NDCG 0.698 — strictly better retrieval than 0.7 with more savings than 0.8.

**Lossiness:** the Task 1 spike confirmed exact identifiers (`STG-7741`, `90 days`) survive even at ~52% drop. But the ~3-pt R@5 regression shows compression *does* drop some query-relevant tokens — some memories became harder to retrieve. It's a real cost, not free.

## Decision context (no accept/reject gate — this is the read)

Whether a fixed ~3-pt R@5 / ~0.02 NDCG retrieval regression is worth a 30–40% recalled-context reduction is a judgment call, weighed against:
- **A write-path model dependency** — a ~600MB ModernBERT + ONNX runtime + batch infra. This is the "more chaos" the project removed LLM-at-rest to avoid.
- **Lossless alternatives already in hand** — chunk-scoped recall + concise-write guidance already cut recalled context without dropping a token. kompress only earns its place if it beats those.
- **Arm B is still open.** LoCoMo measures *retrieval only*. The question that most matters for a memory system — does the compressed recall still let an agent *answer* correctly? — needs a QA bench (LongMemEval). If answer fidelity holds under compression, the context savings get more compelling; if it degrades, the ~3-pt retrieval tax is the least of it.

**Lean:** modest size win for a small-but-real retrieval regression plus a write-path model. Against the project's priors (LLM-at-rest removed for not earning its keep; less chaos, more nature), this is probably not compelling enough to integrate on the retrieval evidence alone — but the size/quality frontier is good enough that **Arm B on LongMemEval is worth running before closing the book.** If compressed memories still answer as well at −30% context, that changes the calculus.

## Reproduce

Sweep (each ratio pays a one-time cold compression of the corpus, then the cache makes reruns instant):
```bash
dotnet run --project benchmarks/Scrinia.Retrieval.Benchmarks -c Release -- locomo \
  --mode bm25_embeddings --top-k 50 --data datasets/locomo/data/locomo10.json \
  --compress-mode off                          # baseline
  # --compress-mode arm-a --compress-ratio {0.8|0.7|0.6|0.5}
```
Wall time: baseline ~6s; each arm-a ratio ~3–4 min cold (batched model load), instant on rerun.
