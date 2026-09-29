# Cross-Encoder Reranker Recommendations for a .NET Agent-Memory MCP Tool (2025–2026)

## TL;DR
- **Primary recommendation: ship `cross-encoder/ms-marco-MiniLM-L6-v2` as INT8 ONNX through `Microsoft.ML.OnnxRuntime`, with `cross-encoder/ms-marco-MiniLM-L-4-v2` as a fallback "fast" preset.** Tom Aarsen's HF repo already publishes `onnx/model_qint8_avx512_vnni.onnx` and `onnx/model_quint8_avx2.onnx` (the OpenVINO INT8 file is just 23.1 MB on disk vs 90.9 MB fp32). On an Intel i7-13700K the fp32 SDPA baseline is 143.9 pairs/sec at 512 tokens (≈6.95 ms/pair), so a batch of 20 candidates at 256 tokens with INT8 should comfortably land in the sub-100-ms range with quality essentially equivalent to BGE-reranker-base in MS MARCO/BEIR.
- **Upgrade path at the same memory budget: `cross-encoder/ettin-reranker-32m-v1` or `-68m-v1` (Tom Aarsen, Hugging Face, May 19 2026).** They are ModernBERT-style, Apache-2.0, support 8 K tokens of context, and the 32 M variant beats the 33 M ms-marco-MiniLM-L12-v2 by +0.0713 NDCG@10 on MTEB(eng, v2) Retrieval (0.5779 vs 0.5066) — and even beats the 568 M `BAAI/bge-reranker-v2-m3` by +0.025 (0.5779 vs 0.5526) on the same benchmark.
- **Best heavy-quality option you can keep local: `BAAI/bge-reranker-v2-m3` via llama.cpp / LLamaSharp using a Q4_K_M or Q8_0 GGUF.** llama.cpp shipped first-class rerank support (PR #9510, Sept 2024) with `/v1/rerank` and `pooling=rank`; BGE-v2-m3, BGE-v2-gemma, Jina v1-tiny/turbo/v2-multilingual/m0, and ms-marco-MiniLM-L4/L12 all work in this path. Expect roughly 6 pairs/sec on a desktop CPU at 512 tokens (≈170 ms/pair) — so reserve it for top-K of K hard queries. Qwen3-Reranker, mxbai-rerank-v2, and ColBERT do **not** currently work cleanly in llama.cpp.

## Key Findings

1. **The CPU-reranking landscape in May 2026 is now dominated by three families:** (a) the classic MS MARCO MiniLM cross-encoders — still the speed champions, now with first-party ONNX INT8 weights from Tom Aarsen; (b) the new Ettin reranker family — ModernBERT-based, Apache-2.0, 17 M–1 B sizes, 8 K context, best quality-per-parameter in the small bucket; and (c) BGE-reranker-v2-m3 — the multilingual quality leader you can still run locally. mxbai-rerank-v2 (0.5 B/1.5 B Qwen-2.5 base) and the Qwen3-Reranker series are stronger on BEIR but are decoder-style and effectively GPU-only for a sidecar CPU MCP tool.
2. **Static "model2vec-style" cross-encoders do not yet exist.** Model2Vec distills only single-vector embedding models. The closest practical analogues are (a) `jina-reranker-v1-tiny-en` — 33 M, 4 layers, BEIR NDCG@10 = 48.54, retaining 92.5 % of jina-reranker-v1-base-en per Jina's own "Smaller, Faster, Cheaper" announcement; (b) ColBERT-style late interaction via PyLate, where document tokens are pre-computed at index time so reranking becomes a MaxSim over already-encoded tokens; and (c) heavily quantized ONNX rankers in FlashRank (e.g. `ms-marco-TinyBERT-L-2-v2` at ~4 MB INT8).
3. **llama.cpp / LLamaSharp can do reranking, but only for a subset of architectures.** The `--reranking` flag and `/v1/rerank` endpoint need a GGUF that ships a CLS classifier head and `pooling=rank`. Confirmed-working today (per `sinjab/llamacpp-rerankers` + gpustack/llama-box): bge-reranker-base / large / v2-m3 / v2-gemma; jina-reranker-v1-tiny / turbo / v2-multilingual / m0; ms-marco-MiniLM-L4 / L12. Confirmed-broken or pooling-incompatible: Qwen3-Reranker GGUFs (most public conversions miss `cls.output.weight`, producing 4.5e-23 scores — see llama.cpp issue #16407), mxbai-rerank-v2, ColBERT.
4. **For C#/.NET, ONNX Runtime is the highest-leverage path.** `Microsoft.ML.OnnxRuntime` ships first-class CPU, CUDA, and DirectML execution providers; DirectML targets any DirectX-12-capable GPU (NVIDIA, AMD, Intel, Qualcomm Adreno on Windows), which is the closest practical equivalent to "Vulkan" on Windows. A native Vulkan EP for ONNX Runtime has been requested (issue #21917, 2024) but is not implemented. TensorFlow.NET is viable but offers no advantages over ORT for transformer inference and has almost no community pre-exported reranker models.
5. **Quantization is essentially free quality-wise for these small rerankers.** INT8 dynamic ONNX quantization (avx512-vnni variant) is well known to deliver large CPU speedups on BERT-class models — the original Microsoft Azure / Hugging Face joint benchmark (Li & Funtowicz, Aug 2020) measured "up to 6×" for BERT-base / RoBERTa-base / GPT-2 on VNNI hardware. There is **no published post-INT8 numeric latency table for `ms-marco-MiniLM-L6-v2` itself** (sbert.net's "Speeding up Inference" page only shows aggregated ratio charts), so the practical expectation is "meaningfully faster than fp32 SDPA, but benchmark on your own hardware before committing to a budget."
6. **A two-stage rerank ("cheap then expensive") is usually the right hybrid pattern.** Score all 50 candidates with MiniLM-L4 or Ettin-17M, take the top 10–15, and optionally rerun BGE-reranker-v2-m3 (via llama.cpp) on that smaller set. This keeps p50 under 100 ms while recovering most of the quality of a much larger model on hard queries.

## Details

### 1. Model-by-model summary (with HuggingFace IDs)

#### Lightweight champions (CPU-friendly, < 100 M params)

CPU throughput numbers below are from Tom Aarsen's Ettin-reranker blog (HF, 19 May 2026), Intel Core i7-13700K, max_length=512, fp32 SDPA. INT8 ONNX is generally faster than these baselines but the exact ratio is hardware- and model-dependent.

| Model | Params | Layers | Max Tokens | Quality (MTEB v2 R / BEIR) | CPU throughput (i7-13700K, fp32 SDPA, 512 tok) | Disk (fp32 / INT8) | Notes |
|---|---|---|---|---|---|---|---|
| `cross-encoder/ms-marco-MiniLM-L-2-v2` | 16 M | 2 | 512 | Lowest of MS-MARCO family | very high | ~32 MB / ~9 MB | FlashRank uses an equivalent `ms-marco-TinyBERT-L-2-v2` (≈4 MB INT8). |
| `cross-encoder/ms-marco-MiniLM-L-4-v2` | 19 M | 4 | 512 | ~0.476 BEIR | **206.2 pairs/sec** | ~76 MB / ~20 MB | Best speed/quality trade in classic MS-MARCO line. |
| `cross-encoder/ms-marco-MiniLM-L-6-v2` | 22.7 M | 6 | 512 | BEIR NDCG@10 48.64 | **143.9 pairs/sec** | 90.9 MB / **23.1 MB** (OpenVINO INT8 file on the official repo) | Ships `onnx/model_qint8_avx512_vnni.onnx` and `onnx/model_quint8_avx2.onnx` directly. **The default we recommend.** |
| `cross-encoder/ms-marco-MiniLM-L-12-v2` | 33 M | 12 | 512 | ~0.495 BEIR | **75.9 pairs/sec** | ~133 MB / ~34 MB | Quality top of MS-MARCO family; ~2× slower than L-6. |
| `cross-encoder/ettin-reranker-17m-v1` | 17.6 M | 7 | **8192** | 0.5576 MTEB v2 R | **267.4 pairs/sec** (fastest in their table) | ~70 MB | Beats L-12 by +5 NDCG points at half the params. Apache-2.0. |
| `cross-encoder/ettin-reranker-32m-v1` | 32.8 M | 10 | 8192 | **0.5779** MTEB v2 R — beats `bge-reranker-v2-m3` (568 M) by +0.025 on the same benchmark | 92.5 pairs/sec | ~130 MB | Best small-model quality. |
| `cross-encoder/ettin-reranker-68m-v1` | 68.6 M | 19 | 8192 | 0.5915 MTEB v2 R | 31.2 pairs/sec | ~275 MB | Approaching bge-base quality. |
| `jinaai/jina-reranker-v1-tiny-en` | 33 M | 4 | **8192** (ALiBi) | BEIR NDCG@10 **48.54** (92.5 % of jina-reranker-v1-base-en) | similar to L-6 | 66 MB (bf16 safetensors); GGUF F16 also published as `ggml-org/jina-reranker-v1-tiny-en-GGUF` | Has an `onnx/` subfolder; real 8 K context unlike vanilla MiniLM. |
| `jinaai/jina-reranker-v1-turbo-en` | 37.8 M | 6 | 8192 | BEIR NDCG@10 49.60 (95 % of base) | similar to L-6 | ~77 MB GGUF F16 | Distilled from jina-reranker-v1-base; works in llama.cpp. |

#### Mid-tier (100–500 M params)

| Model | Params | Quality | CPU pairs/sec | Notes |
|---|---|---|---|---|
| `cross-encoder/ettin-reranker-150m-v1` | 151 M | 0.5994 MTEB v2 R | 14.0 | ModernBERT-large class; 8 K context. |
| `Alibaba-NLP/gte-reranker-modernbert-base` | 150 M | strong on BEIR/LoCo/COIR | 14.7 | Drop-in via Text-Embeddings-Inference. |
| `BAAI/bge-reranker-base` | 278 M | 0.479 BEIR; 65.42 CMTEB-R avg | **19.2** | XLM-RoBERTa, 512 tokens max. Ships ONNX directly in BAAI repo (`onnx/model.onnx`); also `Xenova/bge-reranker-base` and `onnx-community/bge-reranker-base-ONNX`. |
| `mixedbread-ai/mxbai-rerank-xsmall-v1` | 70 M | BEIR NDCG@10 43.9; Acc@3 70.0 | 38.9 | DeBERTa-v2 → no FlashAttention/SDPA → slower than parameter count suggests. |
| `mixedbread-ai/mxbai-rerank-base-v1` | 184 M | 46.9 / 72.3 | 13.4 | Same caveat. v2 is much heavier. |
| `BAAI/bge-reranker-large` | 560 M | strong | 6.2 | Don't bother; v2-m3 is better at similar size. |

#### Heavy / SOTA (use sparingly, top-K only)

| Model | Params | Quality | Practical reality |
|---|---|---|---|
| `BAAI/bge-reranker-v2-m3` | 568 M | BEIR ~0.553; MIRACL NDCG@10 69.32 (multilingual leader) | ~6 pairs/sec CPU; **first-class llama.cpp rerank support**; Q4_K_M GGUF ~400 MB. The local multilingual quality king. |
| `BAAI/bge-reranker-v2-gemma` | 2 B | Higher BEIR than v2-m3 | Gemma-based; works in llama.cpp at Q4_K_M; very slow on CPU. |
| `mixedbread-ai/mxbai-rerank-base-v2` | 494 M (Qwen-2.5) | BEIR avg 55.57 | Mixedbread reports 0.67 s/query on A100 GPU; CPU is several seconds per batch. |
| `mixedbread-ai/mxbai-rerank-large-v2` | 1.5 B | BEIR avg 57.49 | A100: 0.89 s/query. Effectively GPU-only. |
| `Qwen/Qwen3-Reranker-0.6B` | 0.6 B | **MTEB-R 65.80, CMTEB-R 71.31, MMTEB-R 66.36, MLDR 67.28, MTEB-Code 73.42** (Qwen official, top-100 candidates from Qwen3-Embedding-0.6B) | Causal-LM scoring (log P(yes) over P(yes)+P(no)); high latency; public llama.cpp GGUFs are buggy — use `tomaarsen/Qwen3-Reranker-0.6B-seq-cls` to get a usable SequenceClassification head. |
| `jinaai/jina-reranker-v2-base-multilingual` | 278 M | NDCG@10 54.83 on MKQA cross-lingual; 6× faster than v1; HF weights are **CC-BY-NC-4.0 — not OK for commercial deployment** without the Jina API. |
| `jina-reranker-v3` | ~0.6 B (Qwen3-based) | BEIR 61.94, beats mxbai-large-v2 with 2.5× fewer params | Strong but Qwen3-architecture; effectively GPU. |

#### Late-interaction alternatives (ColBERT family)

- **`lightonai/GTE-ModernColBERT-v1`** and **`lightonai/Reason-ModernColBERT`** (via PyLate, paper arXiv:2508.03555): late-interaction rerankers where document token vectors can be **precomputed at index time** and MaxSim is essentially a few matmuls per pair at query time — close to embedding-style cost. SOTA on BEIR among late-interaction models.
- **`jinaai/jina-colbert-v2`**: 8192-token multilingual ColBERT.
- For your MCP use case where memory entries are stable, ColBERT is the single most under-used optimization. PyLate is Python-only but the inference is portable — you can export the encoder to ONNX and write the MaxSim in C# in a few dozen lines.

### 2. .NET / C# integration paths

| Path | Pros | Cons | Verdict |
|---|---|---|---|
| **ONNX Runtime for .NET** (`Microsoft.ML.OnnxRuntime` + `Microsoft.ML.OnnxRuntime.DirectML`) | First-class CPU EP with INT8 / AVX-512-VNNI / OpenVINO support; DirectML EP gives cross-vendor GPU on Windows over DX12; covers virtually every cross-encoder on HF that has an ONNX export; supports session pooling, IO binding, OrtValue zero-copy | You must do tokenization yourself (BertTokenizer in C# — FastBertTokenizer NuGet is the simplest); graph-opt level O3/O4 and dynamic INT8 quantization happen via Optimum at export time, not inside .NET | **Primary recommendation.** Use this for all small/medium cross-encoders. |
| **LLamaSharp / llama.cpp** | You already have it; supports BGE-v2-m3, Jina v1/v2, ms-marco MiniLM via GGUF + `/v1/rerank`; great Vulkan / CUDA / Metal back-ends; one binary handles LLM + reranker | Reranking API is on llama.cpp's server, not LLamaSharp's high-level API as of 0.x — you'll spawn `llama-server` or call NativeApi with `pooling=rank` yourself; Qwen3, mxbai-v2, ColBERT don't work cleanly | **Secondary**, for BGE-v2-m3 / Jina-v2 multilingual or when you want a single inference back-end for both LLM and reranker. |
| **TensorFlow.NET** | Pure managed; nice for training | Almost no community pre-exported reranker models; SavedModel format is heavier than ONNX | Skip. |
| **ML.NET (`Microsoft.ML.Transforms.Onnx`)** | High-level pipeline; integrates with `Microsoft.Extensions.AI` | Adds overhead over raw ORT; not designed for hot-path NLP inference | Skip for hot path; use raw ORT. |
| **Community FastEmbed-Sharp / similar wrappers** | Some C# ports for embeddings | No first-class reranker wrapper today | Reference only. |

**DirectML status (critical for the GPU/Vulkan question):** per Microsoft Learn, "DirectML is in sustained engineering. DirectML continues to be supported, but new feature development has moved to Windows ML for Windows-based ONNX Runtime deployments. Windows ML provides the same ONNX Runtime APIs while dynamically selecting the best execution provider based on your hardware." Plan accordingly: DirectML is fine to ship today and gives you AMD/Intel/NVIDIA/Adreno on Windows over DX12, but if you want a long-lived cross-vendor story, also wire up CUDA EP (Linux/Windows) and consider Windows ML when you upgrade.

### 3. Quantization and optimization on CPU

1. **ONNX Runtime dynamic INT8 quantization** (`optimum-cli export onnx --task text-classification ... && optimum-cli onnxruntime quantize --avx512_vnni`) is the highest-ROI single optimization. For MS-MARCO MiniLM you don't even need to run it — Tom Aarsen ships the AVX-512-VNNI INT8 file and a quint8 AVX2 fallback directly in the HF repo. On VNNI-capable hardware, INT8 has historically given large speedups on BERT-class models (Microsoft Azure / HF measured "up to 6×" for BERT-base/RoBERTa-base/GPT-2 in their 2020 joint benchmark; the realized speedup on the smaller MiniLM family is typically more modest — benchmark on your CPU).
2. **Graph optimization levels O3 / O4** in Optimum (operator fusion, attention fusion, FP16 cast) add ~10–30 % on top of INT8. In .NET, set `SessionOptions.GraphOptimizationLevel = ORT_ENABLE_ALL`.
3. **Sequence-length truncation matters more than model choice** at the margin. Attention scales as O(n²); 256-token docs are ~4× faster than 512-token docs. Cap at the smallest length covering 95 % of your real memory entries.
4. **Batch all candidates in one ORT `Run()` call.** Padding overhead amortizes across the batch. Keep intra-op threads ≤ physical cores — the Steven Lopez JVM MiniLM benchmark (Apr 2026) showed 18 threads on a 12-core box regressed average batch time from 410 ms to 1,460 ms vs the same load at 8 threads.
5. **Static pre-encoding of document tokens via ColBERT / PyLate** is the closest analogue to "model2vec for rerankers." It removes the document-side BERT pass at query time entirely. Worth a serious evaluation if your memory store is reasonably stable.
6. **GGUF quantization (Q4_K_M, Q8_0)** is the only practical way to run BGE-reranker-v2-m3 or v2-gemma in a sidecar. Pre-built quants are published by `gpustack/bge-reranker-v2-m3-GGUF`, `Felladrin/gguf-Q8_0-bge-reranker-v2-m3`, `pervll/bge-reranker-v2-gemma-Q4_K_M-GGUF`, etc.

### 4. Hybrid / two-stage pattern (the recommended architecture)

```
Stage 0  model2vec static embedding (existing) → ANN top-50
Stage 1  ms-marco-MiniLM-L-4-v2 INT8 ONNX → top-15  (~15–30 ms on CPU)
Stage 2  (optional, hard queries) bge-reranker-v2-m3 Q4_K_M via LLamaSharp/llama-server → top-5   (~150–300 ms)
```

The Stage-2 gate can be a cheap signal — query length, low Stage-1 score spread, language detection, or an explicit "high-stakes" flag from the agent. This pattern hits the user's "sub-50 ms ideal" on the common path and falls back to top-tier quality only when needed, while staying under the 500 ms ceiling.

## Recommendations

### Stage 1 — Ship today (next 1–2 weeks)
- Add `Microsoft.ML.OnnxRuntime` (CPU EP) plus `Microsoft.ML.OnnxRuntime.DirectML` (for the optional GPU flag) and a BERT WordPiece tokenizer (FastBertTokenizer NuGet) to the MCP tool.
- Bundle `cross-encoder/ms-marco-MiniLM-L-6-v2` `onnx/model_qint8_avx512_vnni.onnx` (≈23 MB) as the default reranker; also ship the AVX2 `model_quint8_avx2.onnx` and dispatch based on `Avx512.IsSupported`.
- Expose a config knob `reranker.preset = { fast | balanced | quality }` mapping to `L-4-v2 INT8`, `L-6-v2 INT8`, `L-12-v2 INT8`. Truncate inputs to 256 tokens by default.
- **Threshold that would change this:** if your measured NDCG@10 lift over no-rerank is < 3 points on your own labelled traces, ship and move on. If < 1 point, the issue is upstream (retrieval / chunking), not the reranker.

### Stage 2 — Quality upgrade (1–2 months out)
- Add an opt-in `quality` preset using `cross-encoder/ettin-reranker-32m-v1` (or `-68m-v1` if RAM allows). Export to ONNX yourself with Optimum at O4 + dynamic INT8 (Optimum supports the ModernBERT path). Expected uplift: +0.07 NDCG@10 over L-12 on MTEB-style evaluation for ~1.2× CPU cost.
- **Threshold that would change this:** make Ettin the default only after you have ≥ 200 labelled in-domain query/document pairs and Ettin wins by ≥ 2 NDCG@10 points; otherwise stay on MiniLM.

### Stage 3 — Multilingual / heavy queries (optional)
- Wire `BAAI/bge-reranker-v2-m3` (Q4_K_M GGUF, ~400 MB) into LLamaSharp by either (a) spawning `llama-server --reranking --pooling rank --port …` as a managed child process and calling its `/v1/rerank` endpoint, or (b) extending LLamaSharp's NativeApi to expose the rerank entry point. Use it only as Stage-2 on the top-5–10 candidates from Stage-1 when language ≠ English or score spread is low.
- **Don't** chase mxbai-rerank-large-v2, Qwen3-Reranker-4B, or jina-reranker-v3 unless you add a GPU budget — their CPU latency (~1.5–10 s per batch of 20) breaks the 500 ms ceiling.

### Stage 4 — Architectural bet (3–6 months out)
- Evaluate ColBERT-style late interaction via `lightonai/GTE-ModernColBERT-v1` or `jinaai/jina-colbert-v2`. Encode and store the per-token document vectors at memory-write time; at query time you only run the encoder on the query and do a MaxSim. This is the "model2vec for rerankers" approach and is where agent-memory shines because entries are stable and benefit from offline pre-compute.
- **Thresholds that would trigger this:** (a) document set size grows past ~100 K, where bi-encoder + cross-encoder rerank starts feeling cold-cache slow, **and** (b) you can spare ~30–50 KB per indexed chunk for the token vectors.

### Hard "do not"s
- Don't ship a raw fp32 cross-encoder via TF.NET or PyTorch-ONNX — INT8 ORT is materially faster for zero engineering work.
- Don't put a 4-billion-parameter LLM reranker (Qwen3-Reranker-4B, RankZephyr-7B, NV-RerankQA-Mistral-4B) into a sidecar that runs alongside user workloads. They are GPU-only in practice.
- Don't use `jinaai/jina-reranker-v2-base-multilingual` HF weights in a commercial product — they are CC-BY-NC-4.0; either pay for the API or pick `bge-reranker-v2-m3` (Apache-2.0).
- Don't rely on llama.cpp for Qwen3-Reranker, mxbai-rerank-v2, or ColBERT — the public GGUFs have known pooling/CLS-head bugs as of May 2026 (see llama.cpp #16407).
- Don't depend on Cohere Rerank — it is API-only and contradicts the "no API dependency" requirement of an MCP tool running alongside user workloads.

## Caveats

1. **Exact CPU latencies depend heavily on the CPU.** The headline pairs/second numbers in this report are from Tom Aarsen's Ettin-reranker blog (HF, 19 May 2026) on an Intel Core i7-13700K at max_length=512, fp32 SDPA. INT8 ONNX is generally faster, but **no published numeric table** gives the post-INT8 latency for `ms-marco-MiniLM-L-6-v2` specifically; sbert.net's "Speeding up Inference" page only shows aggregated ratio charts. Benchmark on your representative hardware before committing to the latency targets in the TL;DR.
2. **"BEIR / MTEB / NDCG@10" numbers are not directly comparable across reports.** Qwen3's own MTEB-R of 65.80 for Qwen3-Reranker-0.6B uses Qwen3-Embedding-0.6B as the first-stage retriever; the Ettin blog's controlled re-evaluation puts the same model lower. Always re-evaluate on a labelled subset of your own queries.
3. **DirectML is in "sustained engineering."** Microsoft is steering new GPU work toward Windows ML; for a long-lived .NET product, plan for a possible migration. CUDA EP and CPU EP are the safest long-term bets.
4. **llama.cpp reranking is still less battle-tested than embedding.** The `--reranking` flag landed in PR #9510 (Sept 2024) and works well for BGE / Jina / ms-marco MiniLM, but Qwen3-Reranker GGUFs commonly emit ~4.5e-23 garbage scores due to missing `cls.output.weight` (llama.cpp #16407). If you go the LLamaSharp route, pin to a known-good GGUF such as `gpustack/bge-reranker-v2-m3-GGUF`.
5. **There is no "model2vec for cross-encoders" today.** Several research directions point that way (ColBERT, MUVERA, jina-reranker-v3's "last but not late" interaction) but no static, dependency-free reranker analogue to `potion-base-8M` exists. If extreme latency is required, late-interaction (PyLate / ColBERT) is the only credible path.
6. **License hygiene matters.** Apache-2.0 / MIT: ms-marco-MiniLM, Ettin family, BGE-reranker series, mxbai-rerank-v1 and v2, Qwen3-Reranker. CC-BY-NC-4.0 (non-commercial): jina-reranker-v2-base-multilingual HF weights. Always re-check before shipping.