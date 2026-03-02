# Embeddings Architecture

The embeddings plugin adds semantic vector search to Scrinia's hybrid scoring system. It embeds text into high-dimensional vectors, stores them per-workspace, and re-ranks BM25 candidates with cosine similarity scores.

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

The embeddings plugin only embeds the top-K BM25 candidates (not the entire corpus), making search O(K) embeddings instead of O(corpus).

## Project Structure

```
src/Scrinia.Plugin.Embeddings/           Shared library (providers, vector store, scoring)
  Providers/
    OnnxEmbeddingProvider.cs             Local ONNX Runtime (all-MiniLM-L6-v2, 384-dim)
    OllamaEmbeddingProvider.cs           Remote Ollama API
    OpenAiEmbeddingProvider.cs           OpenAI API
    VoyageAiEmbeddingProvider.cs         Voyage AI API
    AzureAiEmbeddingProvider.cs          Azure OpenAI API
    GoogleGeminiEmbeddingProvider.cs     Google Gemini API
    NullEmbeddingProvider.cs             No-op fallback
  Onnx/
    OnnxEmbeddingProvider.cs             ONNX Runtime inference
    BertTokenizer.cs                     Hand-rolled BERT tokenizer
    HardwareDetector.cs                  DirectML/CUDA/CPU detection
    ModelManager.cs                      Model file validation
  EmbeddingOptions.cs                    Configuration POCO
  EmbeddingProviderFactory.cs            Provider factory
  EmbeddingsPlugin.cs                    Server in-process plugin
  HybridReranker.cs                      ISearchScoreContributor implementation
  VectorStore.cs                         SVF2 binary vector storage
  VectorIndex.cs                         SIMD cosine similarity
  HnswIndex.cs                           HNSW approximate nearest neighbor
  IEmbeddingProvider.cs                  Provider interface
  Models/VectorEntry.cs                  Vector data record

src/Scrinia.Plugin.Embeddings.Cli/       CLI plugin executable (MCP server)
  Program.cs                             Entry point, config parsing, MCP registration
  EmbeddingsTools.cs                     4 MCP tools (status, search, upsert, remove)
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
- Lazy dimension detection (set on first successful embedding)
- `EmbedBatchAsync` iterates `EmbedAsync` (no batch API optimization)

## Embedding Providers

### Provider Summary

| Provider | Auth | Default Model | Default Dims | Notes |
|----------|------|---------------|-------------|-------|
| `onnx` | None (local) | all-MiniLM-L6-v2 | 384 | ONNX Runtime, DirectML/CUDA/CPU |
| `ollama` | None | all-minilm | (detected) | Local Ollama instance |
| `openai` | Bearer token | text-embedding-3-small | 1536 | OpenAI API |
| `voyageai` | Bearer token | voyage-3.5 | 1024 | Voyage AI API, adds `input_type: "document"` |
| `azure` | `api-key` header | text-embedding-3-small | 1536 | Classic + v1 URL patterns |
| `google` | API key in query | gemini-embedding-001 | 3072 | Unique embedContent format |
| `none` | N/A | N/A | 0 | No-op, disables semantic search |

### ONNX Provider

Runs locally using ONNX Runtime with the `all-MiniLM-L6-v2` model (384 dimensions, ~87 MB).

**Hardware detection order:** DirectML (Windows GPU) → CUDA (NVIDIA) → CPU

**Components:**
- `BertTokenizer`: Hand-rolled WordPiece tokenizer reading `vocab.txt` (no ML.NET dependency)
- `OnnxEmbeddingProvider`: ONNX Runtime session management, batch inference
- `HardwareDetector`: Probes available execution providers
- `ModelManager`: Validates model files exist (`model.onnx`, `vocab.txt`)

**Model location:** `{exeDir}/plugins/{pluginName}/models/all-MiniLM-L6-v2/`

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
- Model field uses `JsonIgnoreCondition.WhenWritingNull` (null for classic, set for v1)

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
        "onnx"    => CreateOnnx(options, dataDir, logger),
        "ollama"  => new OllamaEmbeddingProvider(...),
        "openai"  => new OpenAiEmbeddingProvider(...),
        "voyageai" => new VoyageAiEmbeddingProvider(...),
        "azure"   => new AzureAiEmbeddingProvider(...),
        "google"  => new GoogleGeminiEmbeddingProvider(...),
        "none"    => new NullEmbeddingProvider(),
        _         => new NullEmbeddingProvider(),  // unknown provider = graceful fallback
    };
}
```

Constructor failures (e.g., missing API key) are caught and fall back to `NullEmbeddingProvider` with a warning log.

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

**Algorithms:**
- `GreedyClosest`: Single-layer nearest neighbor descent
- `SearchLayer`: Beam search returning ordered candidates
- `SelectNeighbors`: Distance-based neighbor selection
- `RandomLevel`: Exponential level distribution (`1.0 / M`)

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

### Child-Process Architecture

The CLI can't load plugin DLLs because the trimmed single-file host is incompatible with dynamic assembly loading. Instead, the embeddings plugin runs as a separate executable communicating via MCP over stdio.

```
scri (MCP client) <--stdio--> scri-plugin-embeddings (MCP server)
```

### EmbeddingsTools (CLI Plugin MCP Server)

The CLI plugin exposes 4 MCP tools:

| Tool | Parameters | Returns |
|------|-----------|---------|
| `status` | none | Provider type, availability, dimensions, vector count |
| `search` | `query`, `scope`, `topK` | `Dictionary<string, double>` (name → similarity) |
| `upsert` | `scope`, `name`, `chunkIndex?`, `text` | Success message |
| `remove` | `scope`, `name` | Success message |

### McpPluginHost (CLI Side)

The CLI's `McpPluginHost` acts as the MCP client, implementing both `ISearchScoreContributor` and `IMemoryEventSink`:

- **On store:** Calls `upsert` for each content element + per-chunk embeddings
- **On append:** Calls `upsert` for the new chunk
- **On forget:** Calls `remove` to delete all vectors for the memory
- **On search:** Calls `search` with the query and returns similarity scores

Auto-reconnects up to 3 times on failure, then degrades to BM25-only.

### Config Passthrough

Workspace configuration is forwarded to the plugin process:

```
scri-plugin-embeddings \
  --data-dir /path/.scrinia \
  --models-dir /path/plugins/scri-plugin-embeddings \
  --config Scrinia:Embeddings:Provider=voyageai \
  --config Scrinia:Embeddings:VoyageAiApiKey=pa-...
```

## Server Integration

### EmbeddingsPlugin (In-Process)

On the server, the embeddings plugin runs in-process as a loaded DLL. It implements all three integration interfaces:

```csharp
public sealed class EmbeddingsPlugin : ScriniaPluginBase,
    IMemoryOperationHook,        // REST path hooks
    ISearchScoreContributor,     // Search scoring
    IMemoryEventSink             // MCP path hooks
```

**REST endpoints** (group: `/embeddings`):

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/status` | Provider info, availability, dimensions, vector count |
| POST | `/reindex` | Re-embed all memories in the active store |
| GET | `/settings` | Current semantic weight and batch size |
| PUT | `/settings` | Update semantic weight and batch size |

**Event handling:**
- `OnAfterStore`: Embed full content + per-chunk vectors
- `OnAfterAppend`: Embed the appended chunk
- `OnAfterForget`: Remove vectors if the memory was deleted

### Two Code Paths

To avoid double-firing, the server uses different hook mechanisms for REST and MCP:

| Path | Mechanism | When |
|------|-----------|------|
| REST API | `IMemoryOperationHook` via `PluginPipeline` | REST endpoint calls |
| MCP over HTTP | `IMemoryEventSink` via `MemoryEventSinkContext` | MCP tool calls |

A plugin implementing both interfaces fires only once per operation.

## Data Isolation

- **Vector data** is always workspace-local: `.scrinia/embeddings/`
- **ONNX model** is stored alongside the plugin: `{exeDir}/plugins/{pluginName}/models/`
- `WorkspaceSetup` passes separate `dataDir` (workspace) and `modelsDir` (plugin models) to the plugin host

This ensures multiple workspaces don't share vector data, even when using the same plugin binary.

## Testing

59 tests in `Scrinia.Plugin.Embeddings.Tests`:
- VectorStore: SVF2 format, upsert, remove, compaction, scope isolation
- VectorIndex: SIMD cosine similarity, search ranking
- HnswIndex: Insert, search, remove, persistence, large-scale behavior
- HybridReranker: Score integration with BM25
- BertTokenizer: WordPiece tokenization (skipped without vocab file)
- OnnxEmbeddingProvider: End-to-end embedding (skipped without model)
- Provider tests: Constructor validation, defaults, factory creation, fallback behavior
