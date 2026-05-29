# Benchmark methodology findings

Three corrections discovered on 2026-05-29 while auditing the retrieval bench
loaders and scorer (commit `87dc093`). Each one overturns a conclusion that an
earlier session reached and acted on. They are recorded here because a future
session reading the old benchmark numbers — or the reasoning that motivated the
LLM-at-rest removal — would otherwise re-derive the wrong thing.

> **Status of the numbers that predate `87dc093`:** every benchmark result file
> written before that commit used the buggy loader and/or the old scorer.
> Treat all of them as invalid. The post-fix baselines below are the reference.

---

## 1. "MemBench's candidate pool caps at ~55% R@50" was a loader bug, not a ceiling

**What we believed:** MemBench R@50 was structurally capped around 55%, with
several categories (`RecMultiSession`, `lowlevel_rec`) stuck at 0% R@5 no matter
what intervention we applied. This was read as evidence that the *candidate pool*
was the bottleneck — the relevant memory simply wasn't being retrieved — and it
was a major motivation for pursuing agent-supplied retrieval anchors.

**What was actually true:** `MemBenchLoader` only handled one of MemBench's two
turn schemas. Files like `RecMultiSession.json` / `lowlevel_rec.json` /
`highlevel*.json` use `mid` + `user` + `assistant`; the `roles`-style files
(`simple`, `noisy`, `aggregative`, `comparative`, `conditional`,
`knowledge_update`, `post_processing`) use `sid` + `user_message` +
`assistant_message`. The loader mapped only `sid`/`user`/`assistant`, so:

- `mid` turns collapsed to doc id `0` (default int) → every doc in the haystack
  shared id 0 → gold ids never matched → 0% on those categories.
- `roles` turns lost their text entirely → docs were timestamp-only → near-random
  scores that *looked* like genuine mediocre retrieval.

MemPalace's upstream loader has the fallback we were missing
(`sid = turn.get("sid", turn.get("mid"))`,
`user = turn.get("user") or turn.get("user_message", "")`).

**Post-fix MemBench baseline** (prod chunk, sentence-MaxSim default, no agent,
11,996 questions): **R@5 82.1 / R@10 91.3 / R@50 98.2 / NDCG@10 0.554.**
RecMultiSession 0→72.6 R@5, lowlevel_rec 0→87.9, knowledge_update 3.3→94.5.

**Takeaway:** MemBench retrieval works fine. R@50 at 98%+ means the candidate
pool is *not* the bottleneck on this benchmark. Do not cite "MemBench candidate
pool gap" as motivation for anything. The real headroom is the R@5→R@10 rank gap
(reranker territory), plus the `noisy` category (45 R@5 → 89.8 R@50, the one
place a real pool gap survives).

---

## 2. "Sentence-MaxSim reranking does nothing on MemBench" was the same loader bug

**What we believed:** Reranking was the wrong lever — sentence-MaxSim showed
0.0 / 0.0 / 0.0 deltas vs no-rerank on MemBench. This pushed us away from
reranking and toward write-time enrichment.

**What was actually true:** that delta was measured on the broken-loader MemBench
where docs were empty / timestamp-only. With no sentence content to compute
MaxSim over, the reranker is necessarily a no-op. It was measuring a dead
substrate, not the reranker.

**Post-fix A/B** (prod chunk, 11,996 questions):

| Metric | rerank (default) | `--rerank none` | Δ |
|---|---|---|---|
| R@5 | 82.1% | 75.7% | **+6.4pp** |
| R@10 | 91.3% | 90.3% | +1.0pp |
| R@50 | 98.2% | 99.5% | **-1.3pp** |
| NDCG@10 | 0.554 | 0.533 | +0.021 |

This is textbook reranker behavior: it promotes relevant docs from ranks 6–50
into the top 5 (+6.4pp R@5) at the cost of a sliver of deep recall (-1.3pp R@50,
because the reranker only sees the top-K window and can shuffle a rank-48 true
positive past the K boundary). Net win, because top-5 is what a memory system
serves to an agent. **The default earns its keep.**

**Takeaway:** reranking and anchors are not competing levers. Reranking fixes the
*rank-shuffle* gap (doc retrieved but ranked low). Anchors fix the
*candidate-pool* gap (doc not retrieved at all). On MemBench the pool gap is
nearly closed, so the reranker is the relevant lever there.

---

## 3. LoCoMo session-granularity R@50=100% is structural, not real retrieval quality

**The trap:** at the default `--granularity session`, each LoCoMo question's
haystack is only ~3 sessions. With `--top-k 50`, the top-50 window contains the
entire haystack, so R@50 is 100% by construction and R@5/R@10 are flattered by a
tiny candidate set. This makes session-granularity LoCoMo look like a near-solved
benchmark when it is not.

**Use `--granularity dialog` to see the real retrieval surface.** One doc per
dialog turn (~36 docs/question), doc id = `dia_id`, gold = raw `dia_id`s.

**LoCoMo baseline comparison** (prod chunk, sentence-MaxSim, no agent, 1,982 q):

| Category | session R@5/R@50 | dialog R@5/R@50 |
|---|---|---|
| aggregate | 84.3 / 100 | **54.7 / 74.8** |
| single_hop | 50.0 / 100 | 25.4 / 54.2 |
| temporal_inference | 58.2 / 100 | 24.6 / 45.2 |
| adversarial | 94.2 / 100 | 60.5 / 78.4 |
| open_domain | 92.4 / 100 | 60.6 / 79.9 |
| temporal | 87.2 / 100 | 65.4 / 82.9 |

Dialog granularity is the surface with the most headroom for retrieval
interventions to demonstrate value. `single_hop` (R@50 54%) and
`temporal_inference` (R@50 45%) are genuine candidate-pool gaps — the relevant
turn isn't even in the top-50, so reranking can't help and anchors might.

---

## 4. Agent-supplied anchors are a candidate-pool lever — they help iff a pool gap exists

**The controlled experiment.** LoCoMo run twice with anchors on
(Qwen3-4B Q4_K_M agent, sentence-MaxSim, prod chunk), once per granularity. The
only variable that differs between the two runs is whether the relevant doc is
*already guaranteed* to be in the candidate pool: at session granularity the
haystack (~3 sessions) is smaller than top-k=50, so R@50 is pinned at 100% by
construction; at dialog granularity (~36 turns) there is real pool headroom.

| Granularity | R@50 baseline | pool headroom | anchors Δ R@5 | anchors Δ R@50 |
|---|---|---|---|---|
| session | 100% | none (structural ceiling) | +0.6 (noise) | **0** |
| dialog | 74.8% | large | **+3.4** | **+4.2** |

Per-category at dialog granularity, anchors lift R@50 most on the categories with
the largest genuine pool gap — `single_hop` 54.2→59.6 (+5.4) and
`temporal_inference` 45.2→49.6 (+4.4). At session granularity the same anchors
from the same model are inert: aggregate +0.6pp R@5, individual categories
scattered in the ±noise band (temporal_inference even -2.7 R@5 on n=92).

**Mechanism confirmed.** R@50 is the candidate-pool metric — whether the relevant
doc made it into the retrieved set at all. Anchors lifting R@50 means they pull
docs *into* the pool that pure BM25+embedding missed, which is exactly the
theorized mechanism (agent writes query-vocabulary bridges at ingest; bridges
surface docs whose raw text didn't lexically/semantically match the query).
Anchors move the needle **iff there is a pool gap to close.** This retroactively
explains why earlier anchor tests (ConvoMem, LongMemEval, session-LoCoMo) were
unimpressive — all near the R@50 ceiling, no gap for anchors to work on.

This is the positive counterpart to finding #1: #1 says "MemBench's pool gap was a
phantom (loader bug)"; #4 says "where a *real* pool gap exists, anchors close part
of it, and where none exists they're inert."

**Caveat.** The lift is modest and does not close the gap — single_hop R@50 is
still only ~60% with anchors, temporal_inference ~50%. (An earlier draft of this
finding worried that some of the R@5 lift was rank-shuffle the reranker
contributes to; the full 2×2 in finding #5 resolves that — anchors measured
against the true no-anchors/no-rerank baseline are *larger* than the rerank-on
comparison showed, +6.2 R@5 / +7.8 R@50.)

**Product implication.** Ship anchors as opt-in; recommend them when retrieval
granularity is fine-grained enough that the candidate pool isn't trivially
complete. On coarse-grained stores where everything relevant is already retrieved,
anchors cost ingest-time LLM calls for ~0 benefit. See finding #5 for the
anchors-vs-reranker tradeoff.

---

## 5. Anchors and sentence-MaxSim reranking are substitutes, not complements

**The full 2×2.** LoCoMo dialog granularity (1,982 q, Qwen3-4B Q4_K_M agent for
anchors), every combination of anchors on/off × rerank on/off:

| | rerank ON | rerank OFF |
|---|---|---|
| **no anchors** | 54.7 / 61.0 / 74.8 | 51.7 / 59.3 / 70.5 |
| **anchors** | 58.1 / 64.9 / 79.0 | 57.9 / 65.3 / 78.3 |

*(R@5 / R@10 / R@50; NDCG@10: 0.484 / 0.456 / 0.517 / 0.510 reading the same
order)*

Marginal effects:

| Effect | R@5 | R@50 |
|---|---|---|
| Rerank, anchors OFF | +3.0 | +4.3 |
| Rerank, anchors ON | +0.2 | +0.7 |
| Anchors, rerank OFF | +6.2 | +7.8 |
| Anchors, rerank ON | +3.4 | +4.2 |
| Both vs neither | +6.4 | +8.5 |

**Sub-additive.** If the two levers were independent, anchors-alone (+6.2 R@5) +
rerank-alone (+3.0) would predict +9.2; the actual combined effect is +6.4 — a
−2.8pp negative interaction (−3.6pp at R@50). Roughly 70% of the combined gain is
captured by *either lever alone*. The natural reading: both promote the same
relevant docs from the same ranks, so whichever runs first captures most of the
gain and leaves the second little to do.

**This refutes a doc-length hypothesis floated mid-investigation** — that
sentence-MaxSim is inert on LoCoMo dialog because single-turn docs have no
multi-sentence structure to rerank. Run #4 (no-anchors, no-rerank) disproves it:
the reranker is worth +3.0/+4.3 on its own. It only *looked* inert in the
anchors-on row because anchors had already captured the gain.

**Operational consequence** — best config depends on budget:

| Config | R@5 / R@50 | Cost |
|---|---|---|
| neither | 51.7 / 70.5 | cheapest |
| rerank only | 54.7 / 74.8 | query-time compute |
| anchors only | 57.9 / 78.3 | ingest-time LLM |
| both | 58.1 / 79.0 | both |

Anchors-only ≈ both, for less query-time work. So **if you already pay the
ingest-time LLM for anchors, the reranker is redundant** — turn it off to save
query-time compute. If you can't afford ingest-time LLM, the reranker is the best
single lever. Running both is the most expensive config for a marginal gain over
anchors-alone.

Caveat on transfer: this 2×2 is one benchmark (LoCoMo) at one granularity
(dialog). On MemBench the reranker was worth +6.4pp R@5 standalone (finding #2) —
larger than here — so the substitute relationship's magnitude is surface-specific.
The *direction* (sub-additive overlap) is likely general; the exact split between
levers is not. Re-run the 2×2 on a new surface before assuming the same tradeoff.

**Superseded in part by finding #6:** the substitution result above is specific to
the *weak* reranker (sentence-MaxSim, sentence-level late interaction over the
static model2vec embeddings). With a real cross-encoder the relationship flips to
complementary — see #6.

---

## 6. The anchors↔reranker substitution depends on reranker strength; a cross-encoder is complementary and stronger

**Setup.** Re-ran the finding-#5 2×2 with a third reranker: an ONNX BERT
cross-encoder (`cross-encoder/ms-marco-MiniLM-L6-v2`, INT8, depth 100) added
bench-side via `--rerank cross-encoder` (see
`docs/research/cross-encoding-reranking.md`). LoCoMo dialog, prod chunk, anchors
via Qwen3-4B Q4_K_M (cached). Full ladder:

| config | R@5 | R@10 | R@50 | NDCG@10 | LLM |
|---|---|---|---|---|---|
| none | 51.7 | 59.3 | 70.5 | 0.456 | — |
| sentence-MaxSim | 54.7 | 61.0 | 74.8 | 0.484 | — |
| sentence-MaxSim + anchors | 58.1 | 64.9 | **79.0** | 0.517 | ingest |
| cross-encoder | 60.7 | 65.9 | 72.9 | 0.563 | — |
| **cross-encoder + anchors** | **63.8** | **69.7** | 78.5 | **0.587** | ingest |

**The substitution result was a property of the weak reranker, not a law.**
Marginal value of the reranker *on top of anchors*:
- sentence-MaxSim on top of anchors: 57.9 → 58.1 = **+0.2 R@5** (redundant — finding #5)
- cross-encoder on top of anchors: 57.9 → 63.8 = **+5.9 R@5** (strongly additive)

Marginal value of anchors is stable across rerankers (+3.4 R@5 on sentence-MaxSim,
+3.1 on cross-encoder) and anchors still add +5.6pp R@50 even on top of the
cross-encoder. So anchors fill the candidate pool; the cross-encoder orders within
it; the two compound. Finding #5's "substitutes" holds only when the reranker is
too weak to extract signal the anchors didn't already surface.

**The cross-encoder is a materially stronger reranker than sentence-MaxSim**,
anchors-held-constant: **+5.7–6.0 R@5, +4.8–4.9 R@10, +0.07–0.08 NDCG@10**, at a
cost of ~−1 to −2 pp R@50 (it reorders the depth-100 window aggressively and
occasionally ranks a true positive below a lexically-similar hard negative;
sentence-MaxSim is gentler and preserves a hair more deep recall). For a memory
tool that serves top-5/10 to an agent, the precision win dominates and the R@50
cost is near-irrelevant.

**Best config: cross-encoder + anchors** — wins every metric except R@50 (78.5 vs
79.0, a tie). Notably, **cross-encoder alone (no LLM)** beats sentence-MaxSim +
anchors on R@5 (60.7 vs 58.1) and NDCG (0.563 vs 0.517) — relevant for deployments
that can't pay ingest-time LLM.

**Ship gate (from the research doc): ≥2–3 NDCG@10 points over the incumbent.**
CE+anchors is +0.070 over sentence-MaxSim+anchors; CE-alone is +0.046. Both clear
the bar. On *quality*, the cross-encoder earns a place in Core.

**What still gates Core adoption — measure before importing ONNX Runtime:**
1. **Latency.** The cross-encoder is CPU BERT forward passes, not vector dot
   products — materially slower than sentence-MaxSim, and `--rerank-threads` fights
   `-Parallel`. The product hot-path latency (p50/p95 at realistic depth) is unmeasured
   here; the bench measures quality, not query latency. The research doc's "benchmark
   on your own hardware" applies before any Core integration.
2. **Second surface.** This is one benchmark. Confirm the cross-encoder advantage
   holds on MemBench (where sentence-MaxSim was already worth +6.4pp R@5 standalone)
   before generalizing.
3. **Surface re-expansion.** Adding ONNX Runtime + a BERT tokenizer to Core reverses
   part of the just-completed simplification (the LLM-at-rest removal). Worth it only
   if latency clears and the second surface confirms.

---

## Scorer semantics (context for reading any result file)

`87dc093` also split the scorer to match each upstream evaluator verbatim. R@K is
now per-question `double` in `[0,1]`, not boolean:

- **MemBench, LongMemEval** — any-hit by id (1.0 if any gold in top-K). Unchanged
  from before; LongMemEval R@5 stayed at 95.0% across the rewrite, which is the
  proof the any-hit path was preserved.
- **LoCoMo** — fractional by id (`found / len(evidence_ids)`).
- **ConvoMem** — fractional by bidirectional substring text-match.
- **NDCG@10** — unified standard binary-relevance cumulative DCG / ideal DCG.

ConvoMem and LoCoMo R@K read *lower* than pre-`87dc093` numbers because any-hit ≥
fractional always; that is the bug fix, not a regression. NDCG@10 reads lower for
multi-gold questions because ideal DCG now sums over more positions. All four are
now apples-to-apples with MemPalace's published evaluators.
