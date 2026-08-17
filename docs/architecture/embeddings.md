# Embeddings Architecture

Scrinia has built-in semantic vector search in `Scrinia.Core`. It embeds text into high-dimensional vectors, stores them per-workspace, and re-ranks BM25 candidates with cosine similarity scores. An optional Vulkan GPU plugin provides hardware-accelerated embeddings.

## Overview

```
                Search query
                     |
          +----------v----------+
          |  WeightedAttributeScorer |  BM25 + attribute matching → top-K candidates
          +----------+----------+
                     |
          +----------v----------+
          |   HybridReranker    |  Embed query, cosine similarity vs. candidates
          |  (ISearchScoreContributor) |
          +----------+----------+
                     |
          +----------v----------+
          |    Final scoring     |  attributeScore + bm25*5 + semanticScore
          +---------------------+
```

The reranker only embeds the top-K BM25 candidates (not the entire corpus), making search O(K) embeddings instead of O(corpus).

## Two-Layer Architecture

| Layer | What | Deps | Providers |
|---|---|---|---|
| **Core (built-in)** | Model2Vec + API providers + VectorStore + HybridReranker | Zero native (pure C#) | model2vec, openai, voyageai, azure, google, ollama |
| **Plugin (optional)** | Vulkan GPU acceleration | LLamaSharp native | vulkan (GGUF model) |

Semantic search works out of the box with the built-in Model2Vec provider (384-dim, ~22MB model, distilled from all-MiniLM-L6-v2). Both Model2Vec and the optional Vulkan plugin produce 384-dim vectors in the same MiniLM vector space, making them interchangeable without reindexing.

## Project Structure

```
src/Scrinia.Core/Embeddings/              Built-in embeddings (zero native deps)
  IEmbeddingProvider.cs                   Provider abstraction
  NullEmbeddingProvider.cs                No-op fallback
  EmbeddingOptions.cs                     Configuration POCO (Provider="model2vec")
  EmbeddingProviderFactory.cs             Factory (model2vec/ollama/openai/voyageai/azure/google/none)
  Model2VecProvider.cs                    Local model: m2v-MiniLM-L6-v2, 384-dim, pure C#
  Model2VecModelManager.cs               Downloads model from HuggingFace (~22MB)
  SafeTensorsReader.cs                    Binary SafeTensors parser (F16+F32, internal)
  UnigramTokenizer.cs                    SentencePiece-style tokenizer for distilled models
  BertTokenizer.cs                        WordPiece tokenizer (TokenizeRaw, Encode)
  VectorStore.cs                          Per-scope binary vector storage (SVF3 format)
  IVectorStore.cs                         VectorStore abstraction
  VectorLoadResult.cs                     Load-from-disk result (vectors + authoritative flag)
  VectorWriteRetry.cs                     Bounded retry for detached/event-sink writers
  VectorIndex.cs                          SIMD cosine similarity + flat-scan search
  MemoryVectorIndexer.cs                  The single embed-and-index policy (windowing, cap,
                                           batching, count guard, remove-then-upsert, retry)
  VectorScoreProjection.cs                The single read-side key projection (element + memory keys)
  EmbeddingReindexer.cs                   Bulk reindex (in-process and sink-driven variants)
  ChunkedSignature.cs                     Composes the signature (provider|chunk config|layout)
  IEmbeddingsAdmin.cs                     What the embeddings REST endpoints need, provider-agnostic
  HybridReranker.cs                       ISearchScoreContributor implementation
  VectorMath.cs                           Shared L2Normalize utility (used by all providers)
  PluginConfigProtocol.cs                 Config keys passed to the out-of-process plugin
  Models/VectorEntry.cs                   Vector data record
  Providers/
    OllamaEmbeddingProvider.cs            Remote Ollama API
    OpenAiEmbeddingProvider.cs            OpenAI API
    VoyageAiEmbeddingProvider.cs          Voyage AI API
    AzureAiEmbeddingProvider.cs           Azure OpenAI API
    GoogleGeminiEmbeddingProvider.cs      Google Gemini API

src/Scrinia.Plugin.Embeddings/            Optional Vulkan GPU plugin (LLamaSharp)
  EmbeddingsPlugin.cs                     Server in-process plugin
  VulkanEmbeddingProvider.cs              LLamaSharp Vulkan-accelerated embeddings
  VulkanModelManager.cs                   Downloads GGUF model from HuggingFace

src/Scrinia.Plugin.Embeddings.Cli/        CLI plugin executable (MCP server)
  Program.cs                              Entry point, MCP registration (Vulkan provider)
  EmbeddingsTools.cs                      5 MCP tools (index, status, search, upsert, remove)
```

## IEmbeddingProvider

```csharp
public interface IEmbeddingProvider : IDisposable
{
    bool IsAvailable { get; }
    int Dimensions { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    // Default interface method — sequential fallback, providers can override for batch APIs
    async Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var vec = await EmbedAsync(texts[i], ct);
            if (vec is null) return null;
            results[i] = vec;
        }
        return results;
    }
}
```

All providers:
- Return L2-normalized vectors via `VectorMath.L2Normalize(vec)` (shared utility in `Scrinia.Core.Embeddings`)
- Return `null` on failure (logged, never throws)
- `EmbedBatchAsync` has a default interface implementation (sequential loop) — providers can override for true batch APIs

## Embedding Providers

### Provider Summary

| Provider | Auth | Default Model | Default Dims | Location |
|----------|------|---------------|-------------|----------|
| `model2vec` | None (local) | m2v-MiniLM-L6-v2 | 384 | Core (built-in) |
| `ollama` | None | all-minilm | (detected) | Core |
| `openai` | Bearer token | text-embedding-3-small | 1536 | Core |
| `voyageai` | Bearer token | voyage-3.5 | 1024 | Core |
| `azure` | `api-key` header | text-embedding-3-small | 1536 | Core |
| `google` | API key in query | gemini-embedding-001 | 3072 | Core |
| `vulkan` | None (local) | GGUF model | (model-dependent) | Plugin (optional) |
| `none` | N/A | N/A | 0 | Core |

### Choosing a provider

| Provider | Latency | Cost | Privacy | Setup effort | Use when |
|----------|---------|------|---------|--------------|----------|
| `model2vec` | ~5–20 ms | Free | Local (no network) | `scri setup` (~22 MB download) | **Default for most users.** Local, deterministic, no native deps. Sufficient quality for code/notes search. |
| `vulkan` | ~10–50 ms (GPU) | Free | Local | Plugin install + GPU driver | You have a Vulkan-capable GPU and need higher-quality embeddings on a large corpus without leaving the machine. |
| `ollama` | ~50–200 ms (local LAN) | Free | Local (process boundary) | Ollama install + model pull | You already run Ollama for chat and want to share an embedding model. Larger models than model2vec at the cost of an extra process. |
| `openai` | ~100–400 ms | Per-token billing | Sent to OpenAI | API key | You want best-in-class quality, accept network round-trips, and your data is allowed to leave the machine. |
| `voyageai` | ~150–400 ms | Per-token billing | Sent to Voyage AI | API key | Voyage models are designed for retrieval and often outperform OpenAI on RAG benchmarks. |
| `azure` | ~100–400 ms | Per-token billing | Stays in your Azure tenant | API key + endpoint | Enterprise deployments that already have OpenAI-via-Azure provisioned. Pick the v1 URL pattern unless you have a legacy deployment. |
| `google` | ~150–400 ms | Per-token billing | Sent to Google | API key | You want Gemini's embedding model and accept Google's data policies. Set `GoogleDimensions` to truncate vectors for storage efficiency. |
| `none` | N/A | Free | N/A | None | You don't want semantic search — BM25 keyword search continues to work. |

**Decision shortcuts:**
- *Single laptop, mixed content*: stay on `model2vec`.
- *Multi-user server, high-volume search*: `vulkan` if you have a GPU; otherwise `openai`/`voyageai` for quality, or `ollama` for free local with a stronger model than model2vec.
- *Regulated environment*: `model2vec` or `azure` (data stays in your tenant).
- *Privacy with quality*: `vulkan` or `ollama` — keeps embeddings on-device.

Switching providers re-embeds existing memories on next access — embeddings live in `.scrinia/embeddings/` and providers tag their vectors with the model identifier so cross-provider mixing is avoided.

### Model2Vec Provider (Default)

Runs locally using a pure C# implementation with zero native dependencies. Uses a distilled `m2v-MiniLM-L6-v2` model (384 dimensions, ~22MB, F16 SafeTensors). This model is distilled from `all-MiniLM-L6-v2` via the [model2vec](https://github.com/MinishLab/model2vec) library, producing vectors in the same semantic space as the full MiniLM transformer.

**Components:**
- `SafeTensorsReader`: Parses the SafeTensors binary format, supports both F32 and F16 tensors (F16→F32 conversion via `BitConverter.UInt16BitsToHalf()`)
- `UnigramTokenizer`: SentencePiece-style tokenizer for distilled models (reads `vocab.txt`, greedy longest-match segmentation with `▁` word-start markers)
- `BertTokenizer`: WordPiece tokenizer for BERT-style vocabularies (used as fallback when `##` subword markers detected)
- `Model2VecProvider`: Auto-detects tokenizer type from vocabulary file. Loads embedding matrix from `model.safetensors`, tokenizes via the appropriate tokenizer, averages token embeddings, L2-normalizes.
- `Model2VecModelManager`: Downloads from `https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/main/`

**Model location:** `{exeDir}/models/m2v-MiniLM-L6-v2/` (files: `model.safetensors`, `vocab.txt`)

**Embedding algorithm:**
1. Tokenize text via auto-detected tokenizer (UnigramTokenizer for SentencePiece vocab, BertTokenizer for WordPiece vocab)
2. For each token ID, look up the corresponding row in the embedding matrix
3. Average all token vectors element-wise (using `TensorPrimitives.Add` for SIMD acceleration)
4. L2-normalize the result (using `TensorPrimitives.Norm` + `TensorPrimitives.Divide`)

### Vulkan Provider (Optional Plugin)

GPU-accelerated embeddings via LLamaSharp 0.25.0 with Vulkan backend. Loads a GGUF embedding model and uses `LLamaEmbedder` with `PoolingType.Mean`.

**Model location:** `{exeDir}/plugins/{pluginName}/models/`

### HTTP Providers (OpenAI, Voyage AI, Azure, Google)

All HTTP providers follow the same pattern:
1. Constructor validates required config (API key, endpoint)
2. Creates `HttpClient` with auth headers and 30-second timeout
3. `EmbedAsync` sends JSON request, parses response, L2-normalizes vector
4. On failure: logs warning, returns `null`
5. Each provider has its own internal `JsonSerializerContext` for trimming safety
6. API-based providers use `RetryPolicy` + `CircuitBreaker` from `Scrinia.Core.Resilience` for transient failure resilience (configurable via `EmbeddingOptions.MaxRetries`, `RetryBaseDelayMs`, `CircuitBreakerThreshold`, `CircuitBreakerCooldownSeconds`)

**Azure specifics:**
- Classic URL: `{endpoint}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}`
- V1 URL: `{endpoint}/openai/v1/embeddings` (model in request body)
- Auth: `api-key` header (not Bearer)

**Google specifics:**
- URL: `{baseUrl}/v1beta/models/{model}:embedContent?key={apiKey}`
- Unique request format: `{"content": {"parts": [{"text": "..."}]}, "taskType": "RETRIEVAL_DOCUMENT"}`
- `outputDimensionality` only sent when configured > 0

### EmbeddingProviderFactory

```csharp
public static IEmbeddingProvider Create(EmbeddingOptions options, string dataDir, ILogger logger)
{
    return options.Provider.ToLowerInvariant() switch
    {
        "model2vec" => LoadModel2Vec(dataDir, logger),
        "ollama"    => new OllamaEmbeddingProvider(...),
        "openai"    => new OpenAiEmbeddingProvider(...),
        "voyageai"  => new VoyageAiEmbeddingProvider(...),
        "azure"     => new AzureAiEmbeddingProvider(...),
        "google"    => new GoogleGeminiEmbeddingProvider(...),
        "none"      => new NullEmbeddingProvider(),
        _           => new NullEmbeddingProvider(),
    };
}
```

Constructor failures (e.g., missing API key, model not downloaded) are caught and fall back to `NullEmbeddingProvider` with a warning log.

## Vector Storage

### VectorStore

Manages per-scope binary vector files in SVF3 format.

```csharp
public sealed class VectorStore : IDisposable, IVectorStore
{
    public VectorStore(string baseDir, string? expectedSignature = null);

    public IReadOnlyList<VectorEntry> GetVectors(string scope);
    public Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct);
    public Task RemoveAsync(string scope, string name, CancellationToken ct);
    public int Count();

    public bool HasStaleQuarantines { get; }
    public IReadOnlyCollection<string> StaleQuarantineScopes { get; }
}
```

**Storage location:** `.scrinia/embeddings/{scope}/vectors.bin`

Ephemeral scope vectors are stored in-memory only.

### SVF3 Binary Format

Signed, append-only format with lazy compaction:

```
[magic "SVF3" 4B] [dimensions uint16] [signatureLen uint16] [signatureUtf8]
repeated entries:
  [op byte: 0=add, 1=delete]
  [nameLen uint16] [nameUtf8]
  [chunkIndex int32]            // -1 = null (unused by any current writer, see below)
  (add only: [vector float32[dims]])
```

The binary field is still a single `int32`, but what a live-serving store writes into it
changed: it is no longer a raw window ordinal, and null (`-1`) is no longer written by
`MemoryVectorIndexer` at all — see [Chunked Embeddings](#chunked-embeddings) below for the
two-level element/window encoding a stored chunk index now carries. Null still round-trips
for backward compatibility (a pre-branch SVF2 file, or a plugin's `upsert` tool called with no
`chunkIndex`), it just doesn't participate in the element-keyed scoring path.

**Signature** captures the active embedding provider + chunking config + layout revision — e.g.
`ollama:nomic-embed-text|c1200o200|e1`. On load, if a file's signature doesn't
match `expectedSignature` it gets renamed to `vectors.bin.stale-{timestamp}`,
recorded in `StaleQuarantineScopes`, and the store returns empty for that scope.
The caller (`EmbeddingReindexer.ReindexIfStaleAsync` from `WorkspaceSetup`) then
walks scopes, observes `HasStaleQuarantines`, and rebuilds. This is also the mechanism that
migrates every pre-branch vector file forward automatically — see the layout revision note
under [Signature composition](#signature-composition).

Legacy SVF1 and SVF2 files (no signature) are read transparently when
`expectedSignature` is null (tests and ad-hoc tools). Production callers always
pass a signature, so older files quarantine on first contact and reindex.

**Compaction:** Triggered when deletes exceed 20% of ops AND ≥ 10 deletes.
Rewrites the file with only live entries in SVF3.

**Concurrency:** Per-scope `SemaphoreSlim` serializes writes. Atomic file
operations (write to `.tmp`, then rename) with retry backoff when another
process holds the file open.

### VectorEntry

```csharp
public sealed record VectorEntry(
    string Name,          // qualified memory name (e.g., "api:auth-flow")
    int? ChunkIndex,      // stored chunk index — see "Chunked Embeddings" below; null is legacy
    float[] Vector);      // L2-normalized
```

## Chunked Embeddings

A memory's **content array is its addressable unit**: element index, 1-based, the same unit
`ChunkEntry.ChunkIndex` and the REST contract use (`docs/server-admin.md`'s `chunkEntries` and
`GET .../chunks/{index}`). Each content element is further sliced into overlapping windows
before embedding, and every window is embedded and stored as its own vector — long memories no
longer degrade to one mean-pooled vector, and a needle buried at char 12000 of a long session
log is still vector-reachable (it was silently lost to truncation, or to the provider's context
limit, under the retired whole-document-vector layout).

Windows are an implementation detail of the vector store: they never appear in a supplemental
score key. **`VectorStore.UpsertAsync` deduplicates on `(name, chunkIndex)`**, so two windows of
the same element could not share the element's own index without colliding — the stored chunk
index is therefore two-level, `elementIndex * ChunkIndexStride + windowOrdinal` (stride =
`MemoryVectorIndexer.ChunkIndexStride` = 1000), and `MemoryVectorIndexer.ElementIndexOf` recovers
the element by integer division. `EmbeddingOptions.MaxChunksPerMemory` is asserted well below the
stride so windows from one element can never overflow into the next element's index space. The
alternative — a second field on `VectorEntry` — would change the SVF3 binary format, its op-log
replay and its compaction, which was judged a larger change than this pipeline needed to carry.

### TextChunker

```csharp
public static class TextChunker
{
    public const int DefaultWindowSize = 1200;  // ~300 tokens for English prose
    public const int DefaultOverlap = 200;

    public static IReadOnlyList<(int Index, string Text)> SliceWindows(
        string text, int windowSize = DefaultWindowSize, int overlap = DefaultOverlap);
}
```

- Short text (`Length <= windowSize`) returns one chunk `(0, trimmed)` so we
  don't pay for slicing when there's nothing to slice.
- Otherwise slides a window of `windowSize` chars stepping by `windowSize - overlap`,
  trimming each slice's leading/trailing whitespace.
- `overlap >= windowSize` throws — that would loop forever.
- Char-based, not token-aware: avoids a tokenizer dependency and keeps behavior
  deterministic across providers. The 1200/200 default fits comfortably inside
  every supported provider's context window including nomic-embed-text's 2048-token
  cap.

### MemoryVectorIndexer — the single embed-and-index policy

Every write path and every reindex path routes through one class:
`MemoryVectorIndexer.IndexAsync(scope, subject, content, ct)`. It owns availability checking,
windowing (via `TextChunker`, per content element), the `MaxChunksPerMemory` cap (default 500,
ceiling 999 — excess tail is dropped from the vector path, BM25 still indexes the full text via
the existing sidecar pipeline; note the cap counts windows across the whole content array, and
every non-blank element contributes at least one, so it also bounds how many content elements
can carry a vector), batching through `EmbedBatchAsync`, the vector-count guard (a provider
returning the wrong vector count is treated as failure, not a partial write — the guard runs
*before* `RemoveAsync` so a bad response can't wipe an existing memory's vectors and leave nothing
in their place), remove-then-upsert ordering, and (for detached callers) write retry via
`VectorWriteRetry`.

The write itself is an atomic per-memory replace — `RemoveAsync(scope, subject)` first, then
`UpsertAsync` per window — so a memory shrunk from 8 windows to 5 leaves no stale chunks 5–7.

This one policy replaces what used to be six divergent copies:

- **Hot path** (`remember`/`append`): `CoreEmbeddingEventHandler` (CLI, `IMemoryEventSink`) and
  `BuiltInEmbeddingsService` (server, both `IMemoryEventSink` and `IMemoryOperationHook`) each
  construct a `MemoryVectorIndexer` and delegate to it. An append re-reads the memory's full
  current content and reindexes the whole thing — indexing only the appended fragment would
  destroy the rest of the memory's vectors, since the replace is atomic.
- **Bulk path** (`EmbeddingReindexer.ReindexAsync` / `BuiltInEmbeddingsService.ReindexStoreAsync`):
  same policy, iterating every persistent memory in the store, with `retryOnUnavailable: false` so
  a locked vector file fails fast per item instead of retrying for up to 42s per memory and
  stalling the whole run. Each memory's content array is reconstructed from its stored artifact via
  `Nmp2ChunkedEncoder.DecodeAllChunksVerified` — a reindex must reproduce the same element layout a
  write produces, not collapse every memory to one element. Used after a signature-mismatch
  quarantine, after a `Scrinia:Embeddings:*` config write, or via `scri reindex`.
- **Plugin path**: when the out-of-process plugin offers the `index` tool, `MemoryVectorIndexer`
  runs *inside the plugin process* — see [EmbeddingsTools](#embeddingstools-plugin-mcp-server)
  below. `EmbeddingReindexer.ReindexViaSinkAsync` drives the plugin-owned bulk case by replaying
  every memory through the same `IMemoryEventSink.OnStoredAsync` a live write would use.

### Signature composition

```csharp
public static class ChunkedSignature
{
    public const string LayoutRevision = "e1";

    public static string Compose(string providerSignature, int chunkSize, int overlap) =>
        $"{providerSignature}|c{chunkSize}o{overlap}|{LayoutRevision}";
}
```

`WorkspaceSetup` composes the signature once when constructing each
`VectorStore`. Any change to provider, model, chunk size, or overlap produces a
different signature → file quarantine → reindex on next launch. Same flow for
manual `scri config Scrinia:Embeddings:ChunkSize 1800`.

**`LayoutRevision` migrates every pre-branch vector file automatically.** It is bumped whenever
the meaning of a stored chunk index changes — `e1` is "elements with nested windows," the layout
described above; a file with no `e1` suffix predates it and carries raw window ordinals, not the
`elementIndex * ChunkIndexStride + windowOrdinal` encoding. Because the signature changed, every
such file fails the signature check on next load and is quarantined the same way a model switch
quarantines a file.

**Who rebuilds it, and who has to ask.** Automatic reindex runs from
`WorkspaceSetup`, which reaches `EmbeddingReindexer.ReindexIfStaleAsync` only along the
built-in and explicit-HTTP provider paths — that is, the CLI with an in-process provider. It
covers neither the Vulkan plugin (`TryLoadVulkanPluginAsync` performs no reindex) nor the
server (which never reads `HasStaleQuarantines` to trigger one).

| Deployment | Migration |
|---|---|
| CLI, built-in Model2Vec or explicit HTTP provider | Automatic on next launch |
| CLI, Vulkan plugin | `scri reindex` |
| Server (`scri serve`, container) | `POST /api/v1/stores/{store}/embeddings/reindex`, once per store, with a key holding the `reindex` permission |

The server logs a warning at startup naming the quarantined scopes and this route
(`EmbeddingReindexer.ProbeStaleScopes`). It deliberately does not rebuild automatically: a
full-corpus re-embed is unbounded work with no progress channel at boot, and one server hosts
many stores. Until a rebuild runs, the affected memories fall back to keyword-only search —
nothing is lost, because vectors are derived data.

### Search-time dedup

`VectorScoreProjection.Project` is the single read-side projection from vector hits to
supplemental score keys, called by both `HybridReranker` (CLI) and `BuiltInEmbeddingsService`
(server). It writes up to two keys per matching vector, each holding the maximum score over the
vectors that roll up into it — "the best-matching window's score wins":

- `{scope}|{name}` — max-aggregated score across every window of that memory.
- `{scope}|{name}|{elementIndex}` — max-aggregated score across just that content element's
  windows. `elementIndex` here is `ChunkEntry.ChunkIndex`'s own numbering — 1-based over content
  elements, recovered from a stored chunk index via `MemoryVectorIndexer.ElementIndexOf`. Window
  ordinals never appear in a key.

`WeightedAttributeScorer.SearchAll` reads the whole-memory key for its per-entry scoring pass and
the element key for its chunk pass, ending up with exactly one result per memory at the
best-matching window's score — no chunk-vs-chunk-vs-whole double-counting. A stored chunk index
below one `ChunkIndexStride` (a vector written before the layout-revision migration above ran)
decodes to a nonexistent element 0 and is skipped for the element key, though it still counts
toward the memory key.

## Similarity Search

### VectorIndex

SIMD-accelerated cosine similarity search:

```csharp
public static class VectorIndex
{
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b);
    public static IReadOnlyList<(VectorEntry Entry, float Similarity)> Search(
        ReadOnlySpan<float> query, IReadOnlyList<VectorEntry> entries,
        int topK);
}
```

**Strategy:**
- Flat scan (exact, SIMD dot product) over every candidate vector.
- Vectors are L2-normalized, so dot product = cosine similarity.
- `HybridReranker`/`BuiltInEmbeddingsService` already bound the work by scoring only the
  BM25+attribute candidate set (typically 20–50 memories), not the whole corpus, so a
  large-scale approximate index (HNSW) was carried by this codebase but never reached by any
  production caller — it has been removed. See CHANGELOG's `## [Unreleased]` "Removed" entry.

## Hybrid Scoring

### HybridReranker

Implements `ISearchScoreContributor` to blend semantic scores with BM25:

```csharp
public sealed class HybridReranker : ISearchScoreContributor
{
    public HybridReranker(IEmbeddingProvider provider, VectorStore store, double weight = 50.0);

    public async Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct);
}
```

**Flow:**
1. `WeightedAttributeScorer` produces top-K BM25+attribute candidates
2. `HybridReranker` receives these candidates
3. Embeds the search query
4. Groups candidates by scope
5. For each scope: loads vectors from `VectorStore`, runs `VectorIndex.Search`, and hands the hits
   to `VectorScoreProjection.Project`
6. Returns scores keyed by `"{scope}|{name}"` (whole-memory) or `"{scope}|{name}|{elementIndex}"`
   (per-element, `elementIndex` = `ChunkEntry.ChunkIndex`'s own 1-based numbering) — see
   [Search-time dedup](#search-time-dedup) above
7. Scores = cosine similarity * configured weight (default 50.0)

`BuiltInEmbeddingsService`, the server's equivalent contributor, follows the identical flow —
both delegate to the same `VectorScoreProjection.Project`.

These supplemental scores are added to the final score:
```
finalScore = weightedAttributeScore + (normalizedBm25 * 5.0) + supplementalScore
```

## CLI Integration

### Two-Step Initialization

The CLI uses a two-step initialization in `WorkspaceSetup.LoadPluginsAsync()`:

1. **Built-in embeddings (in-process):**
   - Create `EmbeddingOptions` from workspace config
   - `EmbeddingProviderFactory.Create(options, modelsDir, logger)` → `IEmbeddingProvider`
   - Create `VectorStore(embeddingsDir)` + `HybridReranker(provider, store, weight)`
   - Create `CoreEmbeddingEventHandler(provider, store, logger)` (in-process event sink)
   - Set `SearchContributorContext.Default` + `MemoryEventSinkContext.Default`

2. **Optional Vulkan plugin (child-process, overrides built-in):**
   - Discover `{exeDir}/plugins/scri-plugin-embeddings[.exe]`
   - If found: launch via `McpPluginHost`, override context defaults
   - If not found or fails: built-in remains active

This means semantic search works out of the box with zero plugins installed. The Vulkan plugin is an optional upgrade for GPU acceleration.

### CoreEmbeddingEventHandler

In-process `IMemoryEventSink` that handles embed-and-index without a child process. It holds a
`MemoryVectorIndexer` and delegates every operation to it rather than embedding directly:

- **On store:** `MemoryVectorIndexer.IndexAsync` — windows every content element, upserts every
  window's vector.
- **On append:** re-reads the memory's full current content from its artifact and calls
  `IndexAsync` on the whole thing — indexing only the appended fragment would destroy the rest of
  the memory's vectors, since `IndexAsync`'s replace is atomic.
- **On forget:** `MemoryVectorIndexer.RemoveAsync` — removes all vectors for the memory from
  `VectorStore`.

### Child-Process Plugin (Optional)

The Vulkan plugin runs as a separate executable communicating via MCP over stdio:

```
scri (MCP client) <--stdio--> scri-plugin-embeddings (MCP server)
```

### EmbeddingsTools (Plugin MCP Server)

The plugin exposes 5 MCP tools:

| Tool | Parameters | Returns |
|------|-----------|---------|
| `index` | `scope`, `name`, `content` (the memory's full content array) | `{ ok: bool }` — **preferred.** Runs `MemoryVectorIndexer.IndexAsync` inside the plugin process, so windowing, the chunk cap, and remove-then-upsert ordering are the same policy every other path uses. |
| `status` | none | Provider type, availability, dimensions, vector count |
| `search` | `query`, `scope`, `topK` | `Dictionary<string, double>` (name → similarity), via `VectorScoreProjection` |
| `upsert` | `scope`, `name`, `chunkIndex?`, `text` | Success message — **legacy.** Still supported for a plugin process too old to expose `index`, but does not window: a long memory embeds as one mean-pooled vector. `McpPluginHost` logs a `[scrinia:warn]` once per connection when it falls back to this. |
| `remove` | `scope`, `name` | Success message |

`McpPluginHost` (the CLI/server-side client of this protocol) discovers which tools a connected
plugin offers via `ListToolsAsync` and prefers `index` whenever it's present; `upsert` is called
only as the fallback for a plugin that doesn't offer `index`.

### Config Passthrough

Workspace configuration is forwarded to the plugin process:

```
scri-plugin-embeddings \
  --data-dir /path/.scrinia \
  --models-dir /path/plugins/scri-plugin-embeddings \
  --config Scrinia:Embeddings:Provider=vulkan
```

## Server Integration

### EmbeddingsPlugin (In-Process, Optional)

On the server, the Vulkan embeddings plugin runs in-process as a loaded DLL. It implements all three integration interfaces:

```csharp
public sealed class EmbeddingsPlugin : ScriniaPluginBase,
    IMemoryOperationHook,        // REST path hooks
    ISearchScoreContributor,     // Search scoring
    IMemoryEventSink             // MCP path hooks
```

The server can also use Core's built-in embeddings directly without the plugin, via
`BuiltInEmbeddingsService` — see [`IEmbeddingsAdmin`](#iembeddingsadmin-and-the-embeddings-rest-routes)
below. Whichever one is active also implements `IEmbeddingsAdmin`, so the REST endpoints below
serve both without a second copy of the routing.

### IEmbeddingsAdmin and the embeddings REST routes

Both `BuiltInEmbeddingsService` and `EmbeddingsPlugin` implement `IEmbeddingsAdmin` (provider
name, availability, dimensions, semantic weight, total vector count, a free-text `Hardware`
label, and `ReindexStoreAsync`), and `EmbeddingsEndpoints.MapEmbeddingsEndpoints` maps three
store-scoped routes against whichever implementation is registered:

| Route | Permission | Purpose |
|---|---|---|
| `GET /api/v1/stores/{store}/embeddings/status` | `read` | Provider, hardware, availability, dimensions, vector count |
| `GET /api/v1/stores/{store}/embeddings/settings` | `read` | Provider, hardware, semantic weight |
| `POST /api/v1/stores/{store}/embeddings/reindex` | `reindex` | Triggers `IEmbeddingsAdmin.ReindexStoreAsync` for the resolved store |

These live under `Endpoints/` alongside every other store-scoped route group specifically so the
`{store}` route value resolves through the same auth middleware as everything else — an earlier
inline `/api/v1/plugins/embeddings/*` group carried no `{store}` segment, so its handler could
never resolve a store and reindex always failed with "No store context available." Reindex is
gated by its own `reindex` permission (a full re-embed of an entire store is unbounded work);
status and settings only need `read`. There is no settings-update route: embedding configuration
is read from `Scrinia:Embeddings:*` config, not written through the API, and the web Settings
page reflects that (read-only, with a pointer to `scri config`).

### Two Code Paths

To avoid double-firing, the server uses different hook mechanisms for REST and MCP:

| Path | Mechanism | When |
|------|-----------|------|
| REST API | `IMemoryOperationHook` via `PluginPipeline` | REST endpoint calls |
| MCP over HTTP | `IMemoryEventSink` via `MemoryEventSinkContext` | MCP tool calls |

A plugin implementing both interfaces fires only once per operation.

## Data Isolation

- **Vector data** is always workspace-local: `.scrinia/embeddings/`
- **Model2Vec model** is stored alongside the CLI: `{exeDir}/models/m2v-MiniLM-L6-v2/`
- **Vulkan GGUF model** is stored alongside the plugin: `{exeDir}/plugins/{pluginName}/models/`

This ensures multiple workspaces don't share vector data, even when using the same binary.

## Shared Vector Space

Both the built-in Model2Vec provider and the optional Vulkan plugin produce 384-dimensional vectors in the MiniLM-L6-v2 semantic space. Model2Vec uses static token embedding lookup (fast, CPU-only) while Vulkan runs the full transformer (slower per-query but with attention-based contextual embeddings).

**Switching providers does still trigger a reindex.** The dimensionality matches, but the two
providers report different signatures — `model2vec:m2v-MiniLM-L6-v2` versus
`vulkan:{ggufFileName}` — and both write to the same `{Root}/.scrinia/embeddings` directory, so
the vector files self-quarantine on the first load after a switch and must be rebuilt. That is
the signature mechanism working as designed: comparable geometry is not the same thing as
identical output, and the file header records which provider actually produced the vectors.
Rebuild per the table under "LayoutRevision" above.

## Testing

`Scrinia.Tests` includes embedding tests in `Embeddings/`:
- VectorStore: SVF1/SVF2/SVF3 format, upsert, remove (including the lazy-load-on-cold-scope fix),
  compaction, scope isolation
- VectorIndex: SIMD cosine similarity, search ranking
- MemoryVectorIndexer: windowing, the chunk cap, the vector-count guard ordering, remove-then-upsert,
  the element/window stride encoding, retry behavior
- VectorScoreProjection: element-key and memory-key aggregation, the legacy below-stride guard
- HybridReranker: Score integration with BM25
- BertTokenizer: WordPiece tokenization, TokenizeRaw, VocabSize (skipped without vocab file)
- UnigramTokenizer: SentencePiece vocab loading, tokenization, Model2Vec integration (skipped without model)
- SafeTensorsReader: Synthetic SafeTensors parsing, F16 + F32 float extraction
- Model2VecProvider: Embed output shape (384-dim), L2 normalization, determinism, similarity (skipped without model)
- Provider tests: Constructor validation, defaults, factory creation, fallback behavior

`Scrinia.Plugin.Embeddings.Tests` covers Vulkan plugin CLI integration (including the `index` tool
and the `upsert` fallback negotiation) and 3-way benchmarks.
