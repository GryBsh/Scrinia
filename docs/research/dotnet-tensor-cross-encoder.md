# Native C#/.NET Cross-Encoder Reranking with `ms-marco-MiniLM-L6-v2` (No ONNX Runtime)

## TL;DR
- **Yes, this is feasible and is the recommended approach.** With only ~50 candidates per query, an exact pure-managed BERT forward pass using `System.Numerics.Tensors` is the right answer: the model is tiny (6 layers, hidden 384, 22.7M params, `model.safetensors` is 90.9 MB F32), and per-query cost is a few dozen small GEMMs that vectorize well on CPU. You avoid ONNX Runtime's native `.dll`/`.so` payload and its session-init/memory overhead entirely.
- **Do NOT use the model2vec static-lookup trick here** — a cross-encoder jointly attends over (query, [SEP], document), so there is no static per-token table that reproduces the relevance score. Reimplement the actual transformer. Use INT8 weight quantization (FP32 accumulation, dynamic per-row activation quant) to shrink the model to ~23 MB and speed up the matmuls; reranking quality loss is near-nil — PyTorch's own dynamic-quantization BERT tutorial reports FP32 F1 0.9019 → INT8 F1 0.902 on MRPC ("0.6% lower F1"), and ranking only needs the *order* of logits, which is even more robust.
- **The single biggest correctness gotcha:** this model's config says `"hidden_act": "gelu"`, which in HuggingFace maps to the EXACT erf-based GELU (`GELUActivation` → `torch.nn.functional.gelu` with `approximate="none"`), **NOT** the tanh approximation (`gelu_new`/`gelu_pytorch_tanh`). Match it, match LayerNorm eps `1e-12`, use post-LayerNorm BERT ordering, feed an additive attention mask (`(1-mask) * large_negative`), and you will reproduce PyTorch logits to float tolerance.

## Key Findings

### 1. Exact architecture (from config.json, verified)
`cross-encoder/ms-marco-MiniLM-L6-v2` is a `BertForSequenceClassification`. The verified `config.json` (794 bytes, current main, committed by Tom Aarsen "Revert inadvertent config, tokenizer updates"):
- `hidden_size`: 384
- `num_hidden_layers`: 6
- `num_attention_heads`: 12 → head dim = 384/12 = **32**, attention scaled by 1/sqrt(32)
- `intermediate_size`: 1536 (FFN expansion 4×)
- `hidden_act`: `"gelu"` → **exact erf GELU** (confirmed via transformers `activations.py`: `ACT2CLS["gelu"] = GELUActivation`, whose default path calls `nn.functional.gelu`; the Python fallback is `x*0.5*(1+erf(x/sqrt(2)))`). `"gelu_new"` → `NewGELUActivation` (tanh) and `"gelu_pytorch_tanh"` → `GELUTanh` are the approximations you must avoid.
- `max_position_embeddings`: 512
- `type_vocab_size`: 2 (token-type/segment embeddings present and used)
- `vocab_size`: 30522 (standard `bert-base-uncased` WordPiece)
- `layer_norm_eps`: 1e-12
- `position_embedding_type`: `"absolute"`
- `pad_token_id`: 0
- num_labels = **1** → classifier is `Linear(384, 1)` producing a single relevance logit
- `sbert_ce_default_activation_function`: `Identity` → the raw logit is returned (no sigmoid). Sort candidates by descending logit.

Total params 22.7M. Reported teacher quality, from the official sentence-transformers Cross-Encoder MS MARCO table (reproduced on the HuggingFace model card): `cross-encoder/ms-marco-MiniLM-L-6-v2` scores **NDCG@10 74.30 on TREC DL 19, MRR@10 39.01 on MS MARCO Dev, 1800 docs/sec** — essentially matching its larger sibling L-12-v2 (74.31 / 39.02) at nearly 2× the throughput (L-12 runs 960 docs/sec). Runtime figures were measured on a V100 GPU. (Base architecture: `microsoft/MiniLM-L12-H384-uncased`. Note the config's `_name_or_path` cosmetically references "L-12-v2"; the real architecture is 6 layers / hidden 384 as listed.)

### 2. Weight extraction (Python → C#)
The safetensors format is trivial to parse from C#: an 8-byte little-endian uint64 header length, then a UTF-8 JSON header mapping each tensor name to `{dtype, shape, data_offsets:[begin,end]}`, then a contiguous little-endian row-major (C-order) data blob (offsets are relative to the start of the data region, not the file). You can `MemoryMappedFile` the data region and reinterpret spans as `float`. Verified BERT tensor names to extract (confirmed against `modeling_bert.py`):
- **Embeddings**: `bert.embeddings.word_embeddings.weight` [30522,384], `bert.embeddings.position_embeddings.weight` [512,384], `bert.embeddings.token_type_embeddings.weight` [2,384], `bert.embeddings.LayerNorm.weight`/`.bias` [384].
- **Per layer i ∈ 0..5**: `bert.encoder.layer.{i}.attention.self.{query,key,value}.weight`/`.bias`, `...attention.output.dense.weight`/`.bias`, `...attention.output.LayerNorm.weight`/`.bias`, `...intermediate.dense.weight`/`.bias` ([1536,384]), `...output.dense.weight`/`.bias` ([384,1536]), `...output.LayerNorm.weight`/`.bias`.
- **Head**: `bert.pooler.dense.weight`/`.bias` [384,384] (followed by tanh), and top-level `classifier.weight` [1,384]/`classifier.bias` [1] (**no** `bert.` prefix — `classifier` is a sibling of `self.bert`).

Critical layout note: PyTorch `nn.Linear.weight` is `[out_features, in_features]`; the op is `y = x @ Wᵀ + b`. Either pre-transpose weights at conversion time (recommended — store as `[in,out]` so the GEMM inner loop is contiguous) or account for it in your matmul. Simplest robust path: in Python, load the model, transpose every Linear weight, optionally quantize, and dump to your own flat binary (or re-save safetensors) with a tiny JSON manifest of names/shapes/scales. This is the "upfront Python tooling" you are willing to do.

### 3. Native forward pass with `System.Numerics.Tensors`
`TensorPrimitives` (System.Numerics.Tensors, current 10.0.x; downlevel-capable to .NET Standard 2.0 / strong SIMD on .NET 8+) provides SIMD-accelerated elementwise/reduction ops: `Add`, `Multiply`, `Dot`, `Sum`, `Exp`, `Tanh`, `Sigmoid`, `SoftMax`, `CosineSimilarity`, etc. It does **not** provide a batched GEMM — you write the matmul yourself.

- **GEMM (the workhorse)**: for each output row, each output element is `TensorPrimitives.Dot(activationRow, weightRow)` over the contiguous (pre-transposed) weight rows, plus bias. This makes each inner product a single vectorized call. For cache behavior, tile over rows/columns and parallelize the outer loop with `Parallel.For`. With seq len ~128–256 and hidden 384, every Linear is small (e.g. [S×384]·[384×384], or the [S×384]·[384×1536] FFN). You can hand-roll register-blocked SIMD with `Vector256<float>`/`Vector512<float>` + FMA for a further ~2–10× over naïve loops, but `TensorPrimitives.Dot` per row is the pragmatic baseline.
- **Embedding lookup**: gather `word + position + token_type` rows (three array adds), then LayerNorm.
- **Self-attention**: Q,K,V projections (3 GEMMs) → reshape into 12 heads of dim 32 → scaled scores `QKᵀ/√32` → add additive mask → `TensorPrimitives.SoftMax` per row → `×V` → concat heads → output dense GEMM → residual add → LayerNorm.
- **FFN**: dense → erf-GELU → dense → residual → LayerNorm.
- **Head**: take `[CLS]` (token 0) hidden → pooler dense → tanh → classifier `Linear(384→1)` → logit.
- **erf-GELU**: `TensorPrimitives` has no `Erf`, and `System.Math` lacks `Erf`. Implement `0.5*x*(1+erf(x/√2))` with a high-accuracy erf polynomial (e.g. Abramowitz–Stegun 7.1.26). Do **not** silently substitute tanh-GELU.
- **Avoiding allocations**: preallocate scratch buffers sized to `maxSeq × hidden`, `maxSeq × intermediate`, and `heads × seq × seq`; reuse across the 50 candidates and across layers; use `Span<float>`/`ArrayPool<float>`. Loop (or `Parallel.ForEach`) over candidates; true batching into one big matmul is optional given the small count.

### 3b. Reference implementations to adapt
- **Tokenizer**: `FastBertTokenizer` (georg-jung) — pure-managed, dependency-free, Native-AOT compatible, results equivalent to HF `AutoTokenizer` in essentially all practical cases; exposes `LoadFromHuggingFaceAsync` + `Encode`.
- **Transformer math patterns**: the TorchSharp "Build Your Own GPT in C#" article shows attention/FFN/LayerNorm structure (read for structure; it depends on libtorch — don't use it as the engine). `K024/llm-sharp` demonstrates safetensors + JSON-manifest conversion patterns in C# (also libtorch-backed). `Lokad`/`Microsoft.ML`/`SmartComponents.LocalEmbeddings` (now a wrapper over Semantic Kernel's `BertOnnxTextEmbeddingGenerationService`) all route through ONNX Runtime — useful only as references for tokenization/IO contracts, not as the inference engine you want.

### 4. Tokenization for the cross-encoder pair
Input is `[CLS] query-tokens [SEP] document-tokens [SEP]` with token_type_ids 0 for query + first [SEP] and 1 for document + final [SEP], plus an attention mask. WordPiece, uncased, vocab 30522. **Important**: FastBertTokenizer is optimized for single-sequence encoding and historically did not produce two-segment (pair) encodings with correct token-type IDs (its docs note the older BERTTokenizers supported pairs but handled token types incorrectly, and that FastBertTokenizer did not support pairs). For a cross-encoder you should construct the pair yourself: tokenize query and document separately, concatenate with the special tokens, build the segment IDs, and truncate to ≤512 — cross-encoders in this family use a standard context length, typically 512 tokens (the HF usage example instantiates `CrossEncoder(..., max_length=512)`). Verify your token IDs against HF `AutoTokenizer` on a sample to lock correctness.

### 5. Quantization
- **INT8 dynamic (recommended)**: quantize each Linear weight matrix to int8 with per-output-channel (per-row) scales; at runtime quantize the activation row dynamically (per-row absmax) and accumulate in int32 → dequant to float. This matches PyTorch `quantize_dynamic` semantics. PyTorch's official BERT dynamic-quantization tutorial (GLUE/MRPC) reports **FP32 F1 0.9019 at 438 MB → INT8 F1 0.902 at 181 MB** (≈2.4× smaller, "0.6% lower F1"), with 1-thread inference dropping from 160 s to 90 s. Ranking is even more robust because only the order of logits matters. Recent `System.Numerics.Tensors` versions support sbyte/byte tensor ops; you can also hand-roll int8 dot products with widening `Vector256` / VNNI-style intrinsics.
- **Float16 storage**: halve file/memory to ~45 MB and upconvert to float on load (or compute in `Half` where supported). Simplest accuracy-preserving option; less CPU compute speedup than int8.
- **Keep LayerNorm, softmax, GELU, and the final classifier in FP32** — I-BERT shows the nonlinearities are where naïve low precision hurts; keeping them float is cheap and safe. Net sweet spot for reranking: **INT8 weights + FP32 nonlinearities**.

### 6. Distillation / architectural alternatives
- **"model2vec for rerankers" does not exist as a drop-in.** model2vec/Minish Lab distills *bi-encoders* into static token tables (potion-base/-retrieval-32M). Per the MinishLab results README, `potion-retrieval-32M` is "the most performant static retrieval model, reaching 86.65% of the performance of all-MiniLM-L6-v2 with a retrieval score of 36.35" (its own HF card more conservatively cites 81.69% / 35.06). But no published static-lookup model reproduces cross-encoder joint scoring, because the score depends on cross-attention between the specific query and document. Minish Lab's `korok` does "reranking" via static-embedding similarity, not cross-encoder fidelity.
- **Late interaction (ColBERT/ColBERTv2)** is the genuine middle ground: encode query and document token vectors independently, score with summed per-query-token MaxSim. It is more native-friendly (document vectors precomputable; scoring is dot products + max + sum, all `TensorPrimitives`-friendly) and beats bi-encoders, but it is a *different model* you'd adopt/train, not a conversion of ms-marco-MiniLM, and it needs per-token vector storage. Adopt only if you're willing to change models.
- **Cross-encoder→cross-encoder and LLM→cross-encoder distillation** (e.g. Rank-DistiLLM) can match larger teachers, but your model is *already* a 6-layer distilled student — further distillation buys little versus running it exactly.
- **Bottom line:** for fidelity to *this* model, run it exactly. The alternatives change the model's identity and add training/storage burden without clearly beating an exact 50-pass native run.

### 7. Is ONNX Runtime really that heavy?
The "managed-only" `Microsoft.ML.OnnxRuntime.Managed` package still pairs with the native `Microsoft.ML.OnnxRuntime` runtime (per-platform native libraries: CPU EP, CoreML, XNNPACK, etc.). Your stated pain — session-init time, native dependency, memory footprint, and that GPU EPs (Vulkan/CUDA) ran ~4× slower than a lean CPU path — is exactly how ORT behaves on a tiny model with tiny batches: fixed per-session/transfer overhead dominates the trivial compute. A custom managed forward pass has near-zero init (just mmap weights), no native dependency, trivial Native-AOT/single-file publishing, and predictable low memory — the same win you already saw with model2vec. A minimal hand-rolled ONNX interpreter is unnecessary since you only need one fixed architecture.

## Details / Recommended implementation plan
1. **Convert once in Python**: load `cross-encoder/ms-marco-MiniLM-L6-v2`, transpose Linear weights to `[in,out]`, optionally INT8-quantize weights with per-row scales (keep embeddings/LayerNorm/classifier in fp16/fp32), dump to a flat binary + small JSON manifest (names, shapes, dtype, scales). Export `vocab.txt`.
2. **C# loader**: mmap/parse the binary; expose typed spans per tensor.
3. **C# tokenizer**: FastBertTokenizer for WordPiece; hand-build the pair `[CLS] q [SEP] d [SEP]`, segment IDs, attention mask; truncate to 512.
4. **C# engine**: embeddings → 6× {MHA(post-LN), FFN(post-LN)} → CLS pooler+tanh → classifier → logit, using `TensorPrimitives.Dot`/`SoftMax`/`Tanh`/`Add`/`Multiply`, a custom erf-GELU, preallocated scratch, and `Parallel.For` over the 50 candidates and/or over GEMM output rows.
5. **Validate**: compare logits against the HF model on fixed (query, doc) pairs; target FP32 agreement within ~1e-3 absolute. The model card's Berlin example should yield ≈8.61 (relevant) and ≈-4.32 (irrelevant). Confirm ranking matches.

### Realistic performance/accuracy assessment
- **Init**: milliseconds (mmap + parse), versus ORT session setup.
- **Per-query latency (50 candidates, seq ~128–256)**: dominated by 50 × (6 layers × ~6 small GEMMs of dims ≤ 256×1536). With SIMD + multicore this is comfortably interactive; on low-spec hardware it is slower but bounded and far cheaper than spinning up a GPU/ORT stack. INT8 helps most exactly on the weak hardware you care about.
- **Accuracy fidelity**: with exact erf-GELU, eps 1e-12, correct masking, and post-LN ordering, FP32 reproduces PyTorch to float tolerance; INT8 weights cost ≲1 ranking point and typically leave top-k order unchanged.

## Recommendations
1. **Build the exact FP32 managed forward pass first** and validate numerically against PyTorch (use the Berlin example logits ≈[8.61, -4.32] as a smoke test). De-risk correctness before optimizing.
2. **Then add INT8 weight quantization** (per-row scales, FP32 nonlinearities). Ship it if top-10 ordering on your eval set is essentially unchanged. **Threshold to change course:** if NDCG@10 / MRR@10 on your own reranking eval drops more than ~1 point vs FP32, fall back to fp16 weights.
3. **Use FastBertTokenizer but write your own pair-encoding + token-type logic**, and unit-test token IDs against HF `AutoTokenizer`.
4. **Parallelize across the 50 candidates** (embarrassingly parallel) and preallocate/`ArrayPool` all scratch. Only invest in hand-tuned `Vector512`+FMA GEMM if profiling shows the `TensorPrimitives.Dot` baseline is insufficient.
5. **Skip ColBERT/distillation unless you decide to change models.** They don't reproduce this model's scores and add training/storage cost. Revisit ColBERT only if candidate sets grow far beyond 50 or you need precomputed document indexing.
6. **Keep the GPU path retired** — your measured 4× slowdown on small models/batches is expected; the managed CPU path is the correct architecture for this workload and hardware profile.

## Caveats
- **GELU variant is the top trap**: `"gelu"` = exact erf GELU in HF (`GELUActivation` → `torch.nn.functional.gelu`, `approximate="none"`, equiv. `0.5*x*(1+erf(x/√2))`); `"gelu_new"`/`"gelu_pytorch_tanh"` are tanh approximations. Using tanh here introduces a small but real logit drift.
- **Linear weight transpose** (`[out,in]` in PyTorch) is the most common porting bug; handle it at conversion time.
- **Attention masking**: add the mask to scores pre-softmax; on any fp16 path beware the historical `-1e4` vs `dtype.min` overflow issue — in FP32 use a large negative (e.g. `-1e9`) or `float.NegativeInfinity` with care.
- **Token-type IDs matter** for a cross-encoder (segment 0 = query, 1 = document); getting them wrong silently degrades scores.
- **FastBertTokenizer pair support**: confirm the current version's two-segment behavior or build the pair yourself; verify against HF `AutoTokenizer`.
- **Numerical determinism**: `TensorPrimitives` documents that exact results may differ across OS/architecture; expect tiny cross-platform logit differences (irrelevant to ranking order).
- The `_name_or_path` in config.json says "L-12-v2" — a cosmetic leftover; the real architecture is 6 layers / hidden 384.