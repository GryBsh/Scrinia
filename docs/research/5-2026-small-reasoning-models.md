# Best Small Language Models for ≤2 GB VRAM (GGUF, Local Reasoning & Summarization) — May 2026

## TL;DR
- **Use Qwen3-1.7B (Q5_K_M or Q4_K_M) as your default** — it is the strongest reasoning/IF model that comfortably fits in 2 GB with an 8K context, exposes an explicit `/think` mode for hard summarization and extraction tasks, and has well‑maintained bartowski/unsloth GGUFs.
- **If you need higher quality and can quantize KV cache** to q8_0, run **Qwen3-4B-Instruct-2507 at IQ3_M (1.96 GB) or Q3_K_S (1.89 GB)**; it beats every other open ≤4 B model on MMLU-Pro/GPQA/IFEval and is the best summarizer in the class — at the cost of ~3–5 % quality vs. Q4_K_M.
- **Stay away from 4 B Q4_K_M (~2.5 GB)** in a strict 2 GB budget; for ultra-light loads, **Llama-3.2-3B at IQ3_XS / Q3_K_M** is the best summarization-only fallback, and **IBM Granite 4.0-H-1B** is the best choice when instruction-following and tool/function calling matter more than knowledge breadth.

## Key Findings

1. **Qwen3 dominates the ≤2 GB tier.** Per the Qwen3 Technical Report (arXiv 2505.09388, Tables 17–20), Qwen3-1.7B in thinking mode scores MMLU-Redux 73.9, MATH-500 93.4, GPQA-Diamond 40.1 and IFEval 72.5 — beating DeepSeek-R1-Distill-Qwen-1.5B on every reasoning axis (45.4 / 83.9 / 33.8 / 39.9) and matching the much larger Qwen2.5-7B-Instruct on MMLU-Redux. Qwen3-0.6B (thinking) hits MATH-500 77.6 and IFEval 59.2 — remarkable for a model whose Q8_0 GGUF is ~640 MB.
2. **Quantization economics make 1.5–2 B at Q4/Q5 the sweet spot for 2 GB.** Per bartowski's published file sizes, Qwen3-1.7B Q4_K_M = 1.28 GB, Q5_K_M = 1.47 GB, Q6_K = 1.67 GB. With FP16 KV cache at 8K context (~200–350 MB for a GQA 1.7 B model), Q5_K_M fits comfortably under 2 GB; Q6_K_L (1.82 GB) is the maximum usable quant.
3. **4 B models require Q3-class quants and KV cache compression to fit.** Qwen3-4B Q4_K_M is 2.50 GB — too large. IQ4_XS = 2.27 GB still doesn't fit. Q3_K_M = 2.08 GB and IQ3_M = 1.96 GB are the only viable options, plus KV cache quantization to q8_0 (~half the size) to leave room for an 8K context.
4. **Reasoning ("thinking") mode is a double-edged sword.** A community-filed lm-evaluation-harness issue (#3576, opened by Nkluge-correa on Feb 11, 2026, titled "Qwen3 and SmolLM3 are worst with `enable_thinking` enable mode?") found that for short-answer GSM8K-style tasks, no-think outperforms think across every tested token cap from 2,048 to 32,768 — verbatim "no-think is always better" — with Qwen3-1.7B scoring gsm8k_flexible 0.130 in think vs 0.438 in no-think, and SmolLM3-3B 0.192 vs 0.635. For fact extraction with hard output limits, **disable thinking**; for multi-step reasoning summaries, enable it.
5. **Gemma 3 1B is the smallest "competent" model**, and IBM Granite 4.0-H-1B is the IFEval/function-calling champion in the sub-2 B class (IFEval 78.5 vs Qwen3-1.7B's 73.1 per IBM's Hugging Face blog "Granite 4.0 Nano: Just how small can you go?", Oct 29, 2025), but both fall well short of Qwen3-1.7B on knowledge and math.
6. **For pure English summarization, Llama-3.2-3B-Instruct remains the leading sub-4B model** per arXiv 2502.00641 ("Evaluating Small Language Models for News Summarization: Implications and Factors Influencing Performance", NAACL 2025, ACL Anthology 2025.naacl-long.253), where Llama-3.2-3B-Ins and Phi-3-Mini were the top performers on the BBC2024 subset (Table 5): BertScore 74.95 / 73.72 and factual consistency 95.6 % / 97.6 % respectively, across the 19-SLM benchmark on 2,000 news samples.

## Details

### A. Candidate model landscape (open-weights, GGUF-available, late-2025 / early-2026)

| Family | Sizes that target ≤2 GB | Notable feature |
|---|---|---|
| **Qwen3** (Alibaba, Apr 2025) | 0.6B, 1.7B, 4B (Q3/IQ3 only) | Hybrid thinking/non-thinking switch in single weights |
| **Qwen3-4B-Instruct-2507 / -Thinking-2507** (Jul 2025) | 4B (Q3 only) | Updated post-train; 256K native context; sharply higher MMLU-Pro and GPQA |
| **Llama 3.2** (Meta, Sep 2024) | 1B, 3B (Q3/Q4 only) | QAT+LoRA & SpinQuant official quantized variants; best for raw English summarization |
| **Gemma 3** (Google DeepMind, Mar 2025) | 270M, 1B, 4B (Q3 only) | 128K context for 4B+; QAT Q4_0 quants released; vision in 4B+ |
| **Gemma 3n E2B / E4B** (mid-2025) | E2B ~2.3B effective, E4B ~4B effective | Per-layer embeddings; native audio (E2B/E4B); on-device focus |
| **Phi-4-mini & Phi-4-mini-reasoning** (Microsoft, Feb 2025) | 3.8B (Q3 only) | Best-in-class small math/reasoning when reasoning trained; 128K context |
| **SmolLM3-3B** (Hugging Face, Jul 2025) | 3B (Q3 only) | Fully open data; dual think/no-think; 128K via YaRN |
| **IBM Granite 4.0 Nano** (Oct 2025) | H-350M, H-1B (~1.5B), 350M, 1B | Hybrid Mamba-2/Transformer; SOTA IFEval + BFCLv3 at size |
| **DeepSeek-R1-Distill-Qwen-1.5B** (Jan 2025) | 1.5B | Distilled from R1; strong MATH-500 (83.9), weak instruction-following (IFEval 39.9) |

> Note on naming: This report covers stable open-weight releases through May 2026. "Gemma 4" (released Apr 2026) E2B/E4B variants are newer but exist; their smallest variant (E2B, ~2.3 B effective / 5.1 B total params) is *not* a fit for a 2 GB budget because the full parameter set must be loaded — only ~2.3 B are *activated* per token. Qwen3.5/Qwen3.6 are 27B+ and out of scope.

### B. VRAM math at 4–8 K context

Rule of thumb: total VRAM ≈ weights file size + KV cache + ~100–200 MB runtime overhead. For dense GGUF models with grouped-query attention (GQA), KV cache at FP16 is roughly:
`KV bytes ≈ 2 (K+V) × n_layers × n_kv_heads × head_dim × seq_len × 2 bytes`

For Qwen3-1.7B (28 layers, 8 KV heads, head_dim 128) at 8K context FP16: ≈ 230 MB. Quantizing K/V to q8_0 roughly halves it (~120 MB), and q4_0 quarters it.

| Model | Quant | File size (bartowski) | KV @ 8K FP16 | Approx total VRAM | Fits 2 GB? |
|---|---|---|---|---|---|
| Qwen3-0.6B | Q8_0 | 0.64 GB | ~110 MB | ~0.85 GB | ✅ trivially |
| Qwen3-0.6B | Q4_K_M | 0.40 GB | ~110 MB | ~0.6 GB | ✅ |
| Qwen3-1.7B | Q6_K_L | 1.82 GB | ~230 MB | ~2.15 GB | ⚠ tight, needs q8_0 KV |
| **Qwen3-1.7B** | **Q5_K_M** | **1.47 GB** | ~230 MB | **~1.85 GB** | ✅ recommended |
| Qwen3-1.7B | Q4_K_M | 1.28 GB | ~230 MB | ~1.65 GB | ✅ comfortable |
| Qwen3-1.7B | IQ4_XS | 1.18 GB | ~230 MB | ~1.55 GB | ✅ |
| Qwen3-4B | Q4_K_M | 2.50 GB | ~280 MB | ~2.95 GB | ❌ |
| Qwen3-4B | IQ4_XS | 2.27 GB | ~280 MB | ~2.70 GB | ❌ |
| Qwen3-4B | Q3_K_M | 2.08 GB | ~280 MB | ~2.5 GB | ❌ at FP16 KV |
| **Qwen3-4B** | **IQ3_M + q8_0 KV** | **1.96 GB** | ~140 MB | **~2.20 GB** | ⚠ borderline; need 4K ctx or q4_0 KV |
| **Qwen3-4B** | **Q3_K_S + q8_0 KV** | **1.89 GB** | ~140 MB | **~2.10 GB** | ⚠ tight |
| Llama-3.2-1B | Q8_0 | ~1.3 GB | ~130 MB | ~1.55 GB | ✅ |
| Llama-3.2-1B | Q5_K_M | ~0.85 GB | ~130 MB | ~1.1 GB | ✅ |
| Llama-3.2-3B | Q4_K_M | ~2.0 GB | ~200 MB | ~2.3 GB | ❌ tight |
| **Llama-3.2-3B** | **IQ3_XS / Q3_K_M** | **~1.6 GB** | ~200 MB | **~1.9 GB** | ✅ |
| Gemma 3 1B (it) | Q4_K_M | ~0.85 GB | ~140 MB | ~1.1 GB | ✅ |
| Gemma 3 4B (it) | Q3_K_M | ~2.1 GB | ~210 MB | ~2.4 GB | ❌ |
| Phi-4-mini (3.8B) | Q3_K_M | ~2.0 GB | ~260 MB | ~2.4 GB | ❌ tight |
| SmolLM3-3B | Q3_K_M | ~1.6 GB | ~180 MB | ~1.9 GB | ✅ |
| Granite 4.0-H-1B (~1.5B) | Q5_K_M | ~1.1 GB | hybrid SSM (low) | ~1.3 GB | ✅ |
| DeepSeek-R1-Distill-Qwen-1.5B | Q5_K_M | ~1.1 GB | ~180 MB | ~1.4 GB | ✅ |

Always set `--cache-type-k q8_0 --cache-type-v q8_0` in llama.cpp / Ollama (`OLLAMA_KV_CACHE_TYPE=q8_0`) when squeezing 3–4 B models into 2 GB; quality loss is negligible for K/V at q8_0 and remains usable at q4_0 per localbench.substack.com ("Gemma 4 and Qwen 3.6 with q8_0 and q4_0 KV cache: KL divergence results"), which reports that "both Qwen models stay below KL 0.04 at q8_0, and even q4_0 cache (KL 0.087–0.117) is usable."

### C. Benchmark performance for ≤2 GB candidates

All Qwen3 numbers below are from the **Qwen3 Technical Report (arXiv 2505.09388), Tables 17–20**; Qwen3-4B-Instruct-2507 from its HF model card; Granite from IBM's blog; Llama / Phi / Gemma from their respective tech reports.

**Thinking-mode reasoning (best for hard extraction):**

| Model | MMLU-Redux | GPQA-D | MATH-500 | IFEval (strict) | LiveCodeBench v5 | BFCL v3 |
|---|---|---|---|---|---|---|
| Qwen3-0.6B (think) | 55.6 | 27.9 | 77.6 | 59.2 | 12.3 | 46.4 |
| **Qwen3-1.7B (think)** | **73.9** | **40.1** | **93.4** | **72.5** | **33.2** | **56.6** |
| **Qwen3-4B (think)** | **83.7** | **55.9** | **97.0** | **81.9** | **54.2** | **65.9** |
| DeepSeek-R1-Distill-Qwen-1.5B | 45.4 | 33.8 | 83.9 | 39.9 | 13.2 | n/a |
| DeepSeek-R1-Distill-Llama-8B | 66.4 | 49.0 | 89.1 | 59.0 | 42.5 | n/a |
| Phi-4-Mini-reasoning (3.8B) | n/a | 49.0 | 90.4 | n/a | n/a | n/a |

**Non-thinking mode (best for short, low-latency summarization):**

| Model | MMLU-Redux | MMLU-Pro | GPQA-D | MATH-500 | IFEval | LiveCodeBench v5 |
|---|---|---|---|---|---|---|
| Qwen3-0.6B (no-think) | 44.6 | — | 22.9 | 55.2 | 54.5 | 3.6 |
| Qwen3-1.7B (no-think) | 64.4 | — | 28.6 | 73.0 | 68.2 | 11.6 |
| Qwen3-4B (no-think, original) | 77.3 | 58.0 | 41.7 | 84.8 | 81.2 | 21.3 |
| **Qwen3-4B-Instruct-2507** | **84.2** | **69.6** | **62.0** | — | **83.4** | 35.1 (v6) |
| Gemma-3-1B-IT | 33.3 | — | 19.2 | 46.4 | 54.5 | 1.8 |
| Phi-4-mini (non-reasoning) | 67.9 | — | 25.2 | 67.6 | 68.6 | 10.4 |
| Qwen2.5-1.5B-Instruct | 50.7 | — | 29.8 | 55.0 | 42.5 | 5.3 |
| Llama-3.1-8B-Instruct | 61.7 | — | 32.8 | 54.8 | 75.0 | 10.8 |

**Granite Nano vs. peers** (IBM Granite Hugging Face blog "Granite 4.0 Nano: Just how small can you go?", Oct 29, 2025; cross-posted as the IBM corporate announcement at ibm.com/new/announcements/ibm-granite-4-0-hyper-efficient-high-performance-hybrid-models): Granite-4.0-H-1B (~1.5 B) — IFEval **78.5** vs Qwen3-1.7B 73.1 and Gemma 3-1B 59.3; BFCLv3 (tool-calling) **54.8** vs Qwen3-1.7B 52.2 and Gemma 3-1B 16.3; average across knowledge/math/code/safety **68.3 %** — the highest at its size. But Granite's GPQA and MATH are not competitive with Qwen3-1.7B's thinking mode.

**Summarization** (arXiv 2502.00641, NAACL 2025, ACL Anthology 2025.naacl-long.253): On the BBC2024 subset (Table 5), **Llama-3.2-3B-Instruct** leads with BertScore 74.95 and 95.6 % factual consistency, with **Phi-3-Mini** close behind at BertScore 73.72 and 97.6 % factual consistency. Qwen2-1.5B-Instruct trails at BertScore 71.97. The paper predates Qwen3 — community use of Qwen3-1.7B/4B (non-thinking) on similar datasets reportedly reaches Llama-3.2-3B-class summaries with better fact retention because of Qwen3's stronger MMLU-Redux baseline, but no peer-reviewed corroboration of that exists yet.

### D. Choosing thinking vs. non-thinking for each task

- **Single-document summarization (2–4 sentence summaries):** Use **non-thinking**. Output budget is tight; thinking burns it. Qwen3-1.7B non-think (IFEval 68.2) is essentially as good as Llama-3.2-3B at half the size.
- **Multi-fact extraction → structured JSON:** Use **non-thinking** with a function-calling prompt or grammar/JSON schema; Granite 4.0-H-1B is the most reliable in this niche (BFCLv3 54.8).
- **Multi-document or reasoning-heavy summarization (compare/contrast, contradictions, numeric):** Use **thinking** with at least 1.5–2 K reserved output tokens. Qwen3-1.7B (think) jumps from 28.6 → 40.1 on GPQA-Diamond and 64.4 → 73.9 on MMLU-Redux versus non-think.
- **Chain-of-thought summary with verification:** Qwen3-4B IQ3_M (think) is worth the squeeze if you can give it 8K context + q8_0 KV cache.

### E. Quantization sensitivity

- **Qwen3 family handles aggressive quantization remarkably well** — per arXiv 2505.02214 ("An Empirical Study of Qwen3 Quantization"), Qwen3-1.7B-Base FP16 → AWQ 4-bit g128 keeps MMLU 5-shot at 61.0 → 60.9 (essentially lossless); GPTQ 4-bit moves it to 60.7. Going below 4-bit (Bi-LLM, AWQ 2-bit) collapses perplexity to 10⁴–10⁷.
- **Q4_K_M is the practical floor for K-quants.** Going to Q3_K_M loses ~3–6 % on MMLU and noticeably more on GPQA/IFEval.
- **IQ4_XS (imatrix) is the new quality king at ~4-bit** — bartowski recommends it on every model card; it is slightly slower than Q4_K_M because of lookup-table decoding but gives ~95 % of FP16 quality for ~5 % less VRAM than Q4_K_M (per insiderllm.com testing). **Use it whenever you have CUDA/ROCm**; avoid on Vulkan/pure-CPU.
- **Unsloth Dynamic 2.0 GGUFs** apply per-layer optimal bit allocation. From Unsloth's blog "Unsloth Dynamic v2.0 GGUFs" (Daniel & Michael, Apr 24, 2025, unsloth.ai/blog/dynamic-v2): "Our dynamic 4bit version is 2GB smaller whilst having +1% extra accuracy vs the QAT version!" — referring to Gemma 3 27B against Google's official QAT-4. Prefer Unsloth dynamic quants where available.
- **Qwen3-0.6B is the most quant-fragile** — its base FP16 MMLU is 52.3; AWQ 4-bit drops to 43.8 (an 8.5-pt cliff per arXiv 2505.02214 Table 4). Stay at Q5_K_M+ for the 0.6B.

### F. Concrete download links (Hugging Face, bartowski / unsloth / IBM-Granite)

- **Qwen3-1.7B-Instruct**: `bartowski/Qwen_Qwen3-1.7B-GGUF` → grab `Qwen_Qwen3-1.7B-Q5_K_M.gguf` (1.47 GB) or `Qwen_Qwen3-1.7B-Q4_K_M.gguf` (1.28 GB). For an alternative dynamic quant: `unsloth/Qwen3-1.7B-GGUF`.
- **Qwen3-4B-Instruct-2507**: `unsloth/Qwen3-4B-Instruct-2507-GGUF` → use `*-Q3_K_S.gguf` or `*-IQ3_M.gguf` (~1.9 GB). Mirror at `bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF`.
- **Qwen3-4B-Thinking-2507**: `unsloth/Qwen3-4B-Thinking-2507-GGUF` (use only with chain-of-thought workflows; otherwise stick with Instruct-2507).
- **Qwen3-0.6B**: `bartowski/Qwen_Qwen3-0.6B-GGUF` — use Q8_0 (0.64 GB) when this small.
- **Llama-3.2-3B-Instruct**: `bartowski/Llama-3.2-3B-Instruct-GGUF` → `Llama-3.2-3B-Instruct-IQ3_M.gguf` or `Q3_K_M.gguf`. Official Meta QAT variant: `meta-llama/Llama-3.2-3B-Instruct-QLORA_INT4_EO8`.
- **Llama-3.2-1B-Instruct**: `bartowski/Llama-3.2-1B-Instruct-GGUF` → Q8_0 fits easily.
- **Gemma 3 1B-IT**: `unsloth/gemma-3-1b-it-GGUF` (Q4_K_XL recommended) or `google/gemma-3-1b-it-qat-q4_0-gguf` (Google's official QAT GGUF).
- **Phi-4-mini-instruct (3.8B)**: `bartowski/microsoft_Phi-4-mini-instruct-GGUF` — Q3_K_M only at this VRAM.
- **Phi-4-mini-reasoning (3.8B)**: best math/CoT under 4B; same publisher.
- **SmolLM3-3B**: `unsloth/SmolLM3-3B-GGUF` or `bartowski/HuggingFaceTB_SmolLM3-3B-GGUF`; Q3_K_M.
- **IBM Granite 4.0-H-1B-Instruct**: `ibm-granite/granite-4.0-h-1b-GGUF` (official, Q4_K_M ~0.95 GB).
- **DeepSeek-R1-Distill-Qwen-1.5B**: `unsloth/DeepSeek-R1-Distill-Qwen-1.5B-GGUF` or `bartowski/DeepSeek-R1-Distill-Qwen-1.5B-GGUF` (Q5_K_M ~1.1 GB).

## Recommendations

**Stage 1 — Start here (default pick):**

> **Qwen3-1.7B Q5_K_M from `bartowski/Qwen_Qwen3-1.7B-GGUF`** with `-c 8192 --cache-type-k q8_0 --cache-type-v q8_0 --flash-attn`. Wraps under ~1.9 GB, scores 73.9 MMLU-Redux / 72.5 IFEval (thinking) and 64.4 / 68.2 (non-thinking). For summarization: turn thinking OFF with `/no_think`. For multi-fact extraction with reasoning needed: `/think`.

**Stage 2 — If quality is insufficient and you can quantize KV cache aggressively:**

> **Qwen3-4B-Instruct-2507 IQ3_M (`unsloth/Qwen3-4B-Instruct-2507-GGUF`)** with `--cache-type-k q4_0 --cache-type-v q4_0 -c 4096 --flash-attn`. Total VRAM ~2.0 GB. Expect +12 pts MMLU-Pro and +20 pts GPQA over Qwen3-1.7B. Trade-off: ~5 % quality loss from Q3 quantization and a small additional loss from q4_0 KV (KL ≈ 0.10 on Qwen models per localbench testing).

**Stage 3 — If you only do English summarization (no math/reasoning):**

> **Llama-3.2-3B-Instruct IQ3_XS from `bartowski/Llama-3.2-3B-Instruct-GGUF`**. Per arXiv 2502.00641, this model leads BBC2024 BertScore at 74.95 with 95.6 % factual consistency — stronger English fluency than Qwen3 at the cost of weaker knowledge tests. Only consider if your evaluation shows Qwen3-1.7B's summaries feel "clinical" or omit detail.

**Specialized alternatives:**
- For **function calling / structured JSON / tool use**: `ibm-granite/granite-4.0-h-1b-GGUF` Q4_K_M (~1 GB). IFEval 78.5 / BFCLv3 54.8 — best in class.
- For **math-only chain-of-thought**: `bartowski/microsoft_Phi-4-mini-reasoning-GGUF` at IQ3_XS (~1.85 GB). Reasoning-trained Phi-4-mini achieves MATH-500 90.4, outperforming DeepSeek-R1-Distill-Llama-8B (86.9).
- For **multilingual or extreme-edge (CPU-only)**: `unsloth/gemma-3-1b-it-GGUF` Q4_K_XL (~0.85 GB).

**Benchmarks that should change your choice:**
- If MMLU-Redux on your validation set < 60 → step up from 1.7B to 4B IQ3_M.
- If IFEval (instruction adherence) < 70 on your prompts → try Granite 4.0-H-1B.
- If summaries are factually wrong > 5 % of the time → swap to Llama-3.2-3B IQ3_XS or enable Qwen3 thinking mode.
- If tokens/sec < 15 on your GPU → drop one quant level or shorten context to 4K.

## Caveats

- **The 2 GB limit is genuinely tight.** A 4 B model at the community-recommended Q4_K_M (~2.5 GB) does not fit; everything 4B in this report assumes Q3-class quants and KV cache quantization. Quality will be noticeably below the cloud-API version of the same model.
- **Thinking mode burns output budget.** Reserve ≥1 K output tokens or you'll truncate the answer mid-reasoning. The community-filed lm-evaluation-harness issue #3576 (Nkluge-correa, Feb 11 2026) showed that across token caps from 2,048 to 32,768, no-think mode actually outperformed think on GSM8K for every Qwen3 small variant and SmolLM3-3B — Qwen3-1.7B think 0.130 vs no-think 0.438; SmolLM3-3B 0.192 vs 0.635. The finding is task-specific (it is GSM8K-style short-answer evaluation), but it is a real and reproducible pattern. Test both modes on your specific prompts.
- **Some benchmarks are inflated.** Several "2026" model-comparison blog posts (gemma4-ai.com, aurigait.com, gemma4.wiki) cite scores that don't appear in any peer-reviewed paper or vendor benchmark; treat such single-source numbers skeptically. The numbers in the Details section above come from Qwen's own technical report, IBM's Hugging Face announcement, the Phi-4-mini and Qwen3-Quantization arXiv papers, and arXiv 2502.00641 / NAACL 2025 for summarization.
- **Qwen3-4B-Instruct-2507 vs. original Qwen3-4B:** the July 2025 update jumps MMLU-Pro from 58.0 to 69.6 — a major step. Don't accidentally download the older `Qwen/Qwen3-4B` non-thinking weights.
- **"Effective parameter" models (Gemma 3n / Gemma 4 E2B, E4B):** the "effective" parameter count is for compute, not memory. You must still load the full set of parameters, so a "2 B effective" Gemma 3n actually needs more than 2 GB at Q4. Skip these for a strict 2 GB envelope.
- **Knowledge cutoffs:** Qwen3 ~Apr 2025; Llama 3.2 ~Aug 2024; Gemma 3 ~Aug 2024; Phi-4-mini ~Feb 2025; SmolLM3 ~Jul 2025; Granite 4.0 ~Oct 2025. None will know events from late 2025 forward — pair with RAG for fact-extraction over recent documents.
- **Hard math (MATH-500, AIME) shows the opposite pattern** from the GSM8K finding — there, thinking mode is dramatically better (Qwen3-1.7B think 93.4 vs no-think 73.0 on MATH-500). It is short-answer / format-constrained tasks where think mode underperforms, not reasoning per se.