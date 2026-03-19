# Embeddings Architecture

Scrinia has built-in semantic vector search in `Scrinia.Core`. It embeds text into high-dimensional vectors, stores them per-workspace, and re-ranks BM25 candidates with cosine similarity scores. An optional Vulkan GPU plugin provides hardware-accelerated embeddings.

## Overview

```
                Search query
                     |
          +----------v----------+
          |  WeightedFieldScorer |  BM25 + field matching → top-K candidates
          +----------+----------+
                     |
          +----------v----------+
          |   HybridReranker    |  Embed query, cosine similarity vs. candidates
          |  (ISearchScoreContributor) |
          +----------+----------+
                     |
          +----------v----------+
          |    Final scoring     |  fieldScore + bm25*5 + semanticScore
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
  VectorStore.cs                          Per-scope binary vector storage (SVF2 format)
  VectorIndex.cs                          SIMD cosine similarity + flat-scan search
  HnswIndex.cs                            HNSW approximate nearest neighbor
  HybridReranker.cs                       ISearchScoreContributor implementation
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
  EmbeddingsTools.cs                      4 MCP tools (status, search, upsert, remove)
```

## IEmbeddingProvider

```csharp
public interface IEmbeddingProvider : IDisposable
{
    bool IsAvailable { get; }
    int Dimensions { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
    Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
```

All providers:
- Return L2-normalized vectors
- Return `null` on failure (logged, never throws)
- `EmbedBatchAsync` iterates `EmbedAsync` by default (no batch API optimization)

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

Manages per-scope binary vector files in SVF2 format.

```csharp
public sealed class VectorStore : IDisposable
{
    public IReadOnlyList<VectorEntry> GetVectors(string scope);
    public Task UpsertAsync(string scope, string name, int? chunkIndex, float[] vector, CancellationToken ct);
    public Task RemoveAsync(string scope, string name, CancellationToken ct);
    public int TotalVectorCount();
}
```

**Storage location:** `.scrinia/embeddings/{scope}/vectors.bin`

Ephemeral scope vectors are stored in-memory only.

### SVF2 Binary Format

Append-only format with lazy compaction:

```
[magic "SVF2" 4B] [dimensions uint16]
repeated entries:
  [op byte: 0=add, 1=delete]
  [nameLen uint16] [nameUtf8]
  [chunkIndex int32]            // -1 = whole entry
  (add only: [vector float32[dims]])
```

**Compaction:** Triggered when deleted entries exceed 20% of total operations AND at least 10 deletes. Rewrites the file with only live entries.

**Concurrency:** Per-scope `SemaphoreSlim` serializes writes. Atomic file operations (write to `.tmp`, then rename).

### VectorEntry

```csharp
public sealed record VectorEntry(
    string Name,          // qualified memory name (e.g., "api:auth-flow")
    int? ChunkIndex,      // null = whole entry, 0+ = specific chunk
    float[] Vector);      // L2-normalized
```

## Similarity Search

### VectorIndex

SIMD-accelerated cosine similarity search:

```csharp
public static class VectorIndex
{
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b);
    public static IReadOnlyList<(VectorEntry Entry, float Similarity)> Search(
        ReadOnlySpan<float> query, IReadOnlyList<VectorEntry> entries,
        int topK, HnswIndex? hnsw = null);
}
```

**Strategy:**
- < 1000 entries: flat scan (exact, SIMD dot product)
- >= 1000 entries: HNSW approximate nearest neighbor (if index available)
- Vectors are L2-normalized, so dot product = cosine similarity

### HnswIndex

Hierarchical Navigable Small World graph for approximate nearest neighbor search:

```csharp
public sealed class HnswIndex
{
    public void Insert(string key, float[] vector);
    public IReadOnlyList<(string Key, float Similarity)> Search(float[] query, int topK, int efSearch = 0);
    public void Remove(string key);         // lazy deletion (mark + skip)
    public void Save(Stream stream);
    public static HnswIndex Load(Stream stream);
    public int Count { get; }
}
```

**Parameters:**
- `M = 16` (max connections per node per layer)
- `M0 = 32` (doubled at layer 0 per HNSW paper)
- `efConstruction = 200` (beam width during insertion)
- `MaxLayers = 6`
- `efSearch = max(topK, 50)` (beam width during search)

**Persistence:** Binary format with `"HNSW"` magic, serializes full graph topology.

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
1. `WeightedFieldScorer` produces top-K BM25+field candidates
2. `HybridReranker` receives these candidates
3. Embeds the search query
4. Groups candidates by scope
5. For each scope: loads vectors from `VectorStore`, runs `VectorIndex.Search`
6. Returns scores keyed by `"{scope}|{name}"` or `"{scope}|{name}|{chunkIndex}"`
7. Scores = cosine similarity * configured weight (default 50.0)

These supplemental scores are added to the final score:
```
finalScore = weightedFieldScore + (normalizedBm25 * 5.0) + supplementalScore
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

In-process `IMemoryEventSink` that handles embed-and-index without a child process:

- **On store:** Embeds full content + per-chunk vectors, upserts to VectorStore
- **On append:** Embeds the new chunk, upserts to VectorStore
- **On forget:** Removes all vectors for the memory from VectorStore

### Child-Process Plugin (Optional)

The Vulkan plugin runs as a separate executable communicating via MCP over stdio:

```
scri (MCP client) <--stdio--> scri-plugin-embeddings (MCP server)
```

### EmbeddingsTools (Plugin MCP Server)

The plugin exposes 4 MCP tools:

| Tool | Parameters | Returns |
|------|-----------|---------|
| `status` | none | Provider type, availability, dimensions, vector count |
| `search` | `query`, `scope`, `topK` | `Dictionary<string, double>` (name → similarity) |
| `upsert` | `scope`, `name`, `chunkIndex?`, `text` | Success message |
| `remove` | `scope`, `name` | Success message |

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

The server can also use Core's built-in embeddings directly without the plugin.

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

Both the built-in Model2Vec provider and the optional Vulkan plugin produce 384-dimensional vectors in the MiniLM-L6-v2 semantic space. This means vectors are interchangeable — switching between providers does not require reindexing. Model2Vec uses static token embedding lookup (fast, CPU-only) while Vulkan runs the full transformer (slower per-query but with attention-based contextual embeddings).

## Testing

~567 tests in `Scrinia.Tests` include embedding tests in `Embeddings/`:
- VectorStore: SVF2 format, upsert, remove, compaction, scope isolation
- VectorIndex: SIMD cosine similarity, search ranking
- HnswIndex: Insert, search, remove, persistence, large-scale behavior
- HybridReranker: Score integration with BM25
- BertTokenizer: WordPiece tokenization, TokenizeRaw, VocabSize (skipped without vocab file)
- UnigramTokenizer: SentencePiece vocab loading, tokenization, Model2Vec integration (skipped without model)
- SafeTensorsReader: Synthetic SafeTensors parsing, F16 + F32 float extraction
- Model2VecProvider: Embed output shape (384-dim), L2 normalization, determinism, similarity (skipped without model)
- Provider tests: Constructor validation, defaults, factory creation, fallback behavior

12 tests in `Scrinia.Plugin.Embeddings.Tests` for Vulkan plugin CLI integration and 3-way benchmarks.
