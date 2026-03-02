# Core Architecture

`Scrinia.Core` is the shared class library containing encoding, models, search, storage, and extensibility interfaces. It has no ASP.NET dependencies (only `System.IO.Hashing`) and is referenced by all other projects.

## NMP/2 Encoding

### Format Overview

NMP/2 (Named Memory Protocol v2) compresses text using Brotli and encodes it as URL-safe Base64 with CRC32 integrity checks.

**Single-chunk format:**

```
NMP/2 {N}B CRC32:{hex} BR+B64
{base64url lines, 76 chars each}
##PAD:{n}
NMP/END
```

**Multi-chunk format:**

```
NMP/2 {N}B CRC32:{hex} BR+B64 C:{k}
##CHUNK:1
{base64url lines}
##PAD:{n}
##CHUNK:2
{base64url lines}
##PAD:{n}
NMP/END
```

Each chunk is independently Brotli-compressed and decodable. The CRC32 covers the full concatenated original bytes across all chunks.

### Encoding Pipeline

```
text → UTF-8 bytes → Brotli compress → Base64url encode → line-wrap (76 chars) → add header/footer
```

Compression density: ~0.68-0.76 characters per original byte.

### IEncodingStrategy

```csharp
public interface IEncodingStrategy
{
    string StrategyId { get; }                    // "nmp/2"
    EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options);
    byte[] Decode(string artifact);
    bool CanDecode(string artifact);
    ArtifactMetadata ParseHeader(string artifact);
}
```

`Nmp2Strategy` is the sole implementation.

### Nmp2ChunkedEncoder

Public utility for all encoding operations:

```csharp
public static class Nmp2ChunkedEncoder
{
    // Single text → single-chunk artifact
    public static EncodingResult Encode(string text);

    // Multiple texts → multi-chunk artifact (if >1 element)
    public static EncodingResult EncodeChunks(string[] chunks);

    // Append a chunk without re-encoding existing content
    public static string AppendChunk(string existingArtifact, string newChunkText);

    // Count independently decodable chunks
    public static int GetChunkCount(string artifact);

    // Decode one chunk (1-based index)
    public static string DecodeChunk(string artifact, int chunkIndex);
}
```

**Key design:** There is no auto-chunking. A single element always produces a single chunk. Multi-chunk artifacts require explicitly passing multiple elements or using `AppendChunk`.

**AppendChunk** performs surgical promotion:
- Single-chunk → two-chunk: rewrites header with `C:2`, wraps existing content as `##CHUNK:1`, appends new `##CHUNK:2`
- Multi-chunk → N+1 chunks: appends new chunk section, updates header count and CRC32

CRC32 combination uses GF(2) matrix math (zlib algorithm) to incrementally update the checksum without re-reading existing data. This makes append O(new chunk size) instead of O(total artifact size).

## Data Models

### ArtifactEntry

The primary index entry for a stored memory:

```csharp
public sealed record ArtifactEntry(
    string Name,
    string Uri,                                    // file:// path to .nmp2 file
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string Description,
    string[]? Tags = null,
    string? ContentPreview = null,                 // first 500 chars
    string[]? Keywords = null,                     // v3: extracted + user-provided
    Dictionary<string, int>? TermFrequencies = null, // v3: for BM25
    DateTimeOffset? UpdatedAt = null,              // v3
    DateTimeOffset? ReviewAfter = null,            // v3: staleness date
    string? ReviewWhen = null,                     // v3: staleness condition
    ChunkEntry[]? ChunkEntries = null);            // v3: per-chunk metadata
```

### ChunkEntry

Per-chunk metadata within a multi-chunk memory:

```csharp
public sealed record ChunkEntry(
    int ChunkIndex,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null);
```

### EphemeralEntry

In-memory-only entries (prefix `~`):

```csharp
public sealed record EphemeralEntry(
    string Name,
    string Artifact,          // stored inline (no file)
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string Description,
    string[]? Tags = null,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null);
```

### Index File (v3)

```csharp
public sealed class IndexFile
{
    public int Version { get; set; } = 3;
    public List<ArtifactEntry> Entries { get; set; } = [];
}
```

Stored as `index.json` in each scope directory. Backward-compatible with v2 (entries without TF data get BM25 score 0).

### Search Result Types

```csharp
public abstract record SearchResult(double Score);

public sealed record EntryResult(ScopedArtifact Item, double Score) : SearchResult;
public sealed record ChunkEntryResult(ScopedArtifact ParentItem, ChunkEntry Chunk,
    int TotalChunks, double Score) : SearchResult;
public sealed record TopicResult(string Scope, string TopicName, string Description,
    int EntryCount, string[]? Tags, double Score) : SearchResult;
```

## Search System

Search combines three scoring mechanisms into a single ranked result set.

### Scoring Formula

```
finalScore = weightedFieldScore + (normalizedBm25 * 5.0) + supplementalScore
```

Where:
- **weightedFieldScore**: Field-specific matching (0-100+ points)
- **normalizedBm25**: BM25 relevance normalized to 0-100 via min-max
- **supplementalScore**: Plugin-provided scores (e.g., cosine similarity from embeddings)

### Weighted Field Scoring

Each query term is matched against entry fields with different weights:

| Match Type | Score |
|------------|-------|
| Exact name match | 100 |
| Tag exact match | 50 |
| Keyword exact match | 40 |
| Name starts with | 30 |
| Name contains | 20 |
| Tag contains | 15 |
| Keyword contains | 12 |
| Description contains | 10 |
| Content preview contains | 5 |

**Chunk scoring** uses parent metadata at half weight plus chunk-specific fields.

**Topic scoring** follows a similar pattern (exact topic name: 100, tag match: 50, etc.).

**Multi-term intersection bonus:** `(matchedTerms - 1) * 15` -- rewards entries matching multiple query terms.

### BM25

Standard BM25 with parameters `k1 = 1.5`, `b = 0.75`:

- **IDF:** `ln((N - df + 0.5) / (df + 0.5) + 1.0)`
- **TF normalization:** `(tf * (K1 + 1.0)) / (tf + K1 * (1.0 - B + B * (docLen / avgDocLen)))`
- Scores are min-max normalized to 0-100 across the result set

Corpus statistics (average doc length, document frequencies) are lazily computed and cached per scope in `CachedIndex`.

### Keyword TF Boosting

Agent-provided keywords get a +5 boost to their term frequency. Auto-extracted keywords get +2. This ensures user-specified keywords rank higher in BM25 scoring.

### Top-K Selection

Results use a min-heap (`PriorityQueue`) for efficient top-K without sorting the full result set. Tie-breaking: by score descending, then by creation date descending.

### Deduplication

An inline `bestPerMemory` dictionary (keyed by `scope|name`) ensures only the highest-scoring variant of each memory appears in results.

### Text Analysis

```csharp
public static class TextAnalysis
{
    public static IReadOnlyList<string> Tokenize(string text);
    public static Dictionary<string, int> ComputeTermFrequencies(string text);
    public static string[] ExtractKeywords(string text, int topN = 25);
    public static string[] MergeKeywords(string[]? agentKeywords, string[] autoKeywords, int maxTotal = 30);
    public static (Dictionary<string, int> TF, string[] Keywords) AnalyzeText(string text, int topN = 25);
}
```

Tokenization: split on non-alphanumeric characters, lowercase, filter stop words (~200 common English words), require minimum 2 characters.

## Storage

### IMemoryStore

27-method interface covering all memory operations:

**Naming:** `ParseQualifiedName`, `FormatQualifiedName`, `IsEphemeral`, `SanitizeName`

**CRUD:** `LoadIndex`, `SaveIndex`, `Upsert`, `Remove`, `ResolveArtifactAsync`

**Search:** `SearchAll` (two overloads: with and without supplemental scores)

**Ephemeral:** `RememberEphemeral`, `ForgetEphemeral`, `GetEphemeral`

**File I/O:** `WriteArtifactAsync`, `ReadArtifactAsync`, `DeleteArtifact`, `ArtifactPath`, `ArtifactUri`

**Copy/Archive:** `CopyMemory`, `ArchiveVersion`

**Topics:** `DiscoverTopics`, `GatherTopicInfos`, `ListTopicArtifacts`, `ImportTopicEntries`

**Scope resolution:** `ResolveReadScopes`, `GetStoreDirForScope`, `FindArtifactPath`

### FileMemoryStore

Instance-based implementation of `IMemoryStore`. One instance per workspace (CLI) or per store (server).

**Scope mapping:**
- `"local"` → `.scrinia/store/`
- `"local-topic:{name}"` → `.scrinia/topics/{name}/`
- `"ephemeral"` → in-memory `ConcurrentDictionary`

**Name sanitization:** Strips `..`, replaces `/` and `\` with `_`, removes invalid filename characters, extracts final path component.

**Artifact resolution** (`ResolveArtifactAsync`):
1. If input starts with `NMP/2` → return as-is (inline artifact)
2. If input starts with `file://` → read file at URI path
3. If input starts with `~` → read from ephemeral store
4. Otherwise → parse as qualified name, read from filesystem

### CachedIndex

Internal structure for O(1) index operations:

```csharp
internal sealed class CachedIndex
{
    public List<ArtifactEntry> Entries { get; }
    public Dictionary<string, int> NameToPosition { get; }  // O(1) lookup by name
    public CorpusStats? Stats { get; set; }                 // lazy BM25 stats
}
```

`NameToPosition` enables O(1) upsert and remove without scanning. BM25 corpus stats are computed once per cache invalidation.

### Artifact LRU Cache

50 MB bounded cache for decoded artifact content:
- LinkedList-based LRU eviction
- Thread-safe via `lock`
- Keys include modification timestamp for staleness detection
- Cache miss reads from filesystem and populates cache

### Concurrency

- Per-scope `ReaderWriterLockSlim` for index CRUD
- Per-scope `CachedIndex` invalidated on write
- `ConcurrentDictionary` for ephemeral store, scope locks, scope caches
- Topic discovery cache with 2-second TTL

### Version Archiving

When a memory is overwritten, the old version is moved to `.scrinia/store/versions/{name}_{timestamp}.nmp2`. This provides a safety net for accidental overwrites.

### IStorageBackend

```csharp
public interface IStorageBackend
{
    string BackendId { get; }
    IMemoryStore CreateStore(string storeName, string storePath);
}
```

`FilesystemBackend` is the sole implementation, creating `FileMemoryStore` instances. The interface exists for future storage backends (S3, database, etc.).

## MemoryStoreContext

AsyncLocal-based dispatch for routing operations to the correct store:

```csharp
public static class MemoryStoreContext
{
    public static IMemoryStore? Current { get; set; }  // AsyncLocal<IMemoryStore?>
}
```

Set per-request in the server, at startup in the CLI. MCP tools read `Current` to find the active store.

## SessionBudget

Tracks token consumption within a session:

```csharp
internal static class SessionBudget
{
    public static void RecordAccess(string memoryName, long charsLoaded);
    public static long TotalCharsLoaded { get; }
    public static int EstimatedTokensLoaded { get; }  // chars / 4
    public static IReadOnlyDictionary<string, (long Chars, int EstTokens)> Breakdown { get; }
}
```

Records are made when `show()` or `get_chunk()` decode memory content. The MCP `budget` tool reports this data.

## Extensibility Interfaces

### ISearchScoreContributor

Plugin hook for providing supplemental search scores (e.g., semantic similarity):

```csharp
public interface ISearchScoreContributor
{
    Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct);
}
```

Scores are keyed by `"{scope}|{name}"` for entries or `"{scope}|{name}|{chunkIndex}"` for chunks.

Dispatch via `SearchContributorContext`:
- `.Current` (AsyncLocal): set per-request by the server
- `.Default` (static): set at startup by the CLI

### IMemoryEventSink

Plugin hook for reacting to store/append/forget events:

```csharp
public interface IMemoryEventSink
{
    Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct);
    Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct);
    Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct);
}
```

Dispatch via `MemoryEventSinkContext`:
- `.Current` (AsyncLocal): per-request
- `.Default` (static): CLI fallback

This is the MCP path hook mechanism. The server REST path uses `IMemoryOperationHook` instead, preventing double-firing.

## JSON Serialization

All serialized types use source-generated `JsonSerializerContext` for trimming safety:

| Context | Location | Purpose |
|---------|----------|---------|
| `FileStoreJsonContext` | Core | Index file persistence |
| `BundleJsonContext` | Core | Export/import bundles |
| `StoreJsonContext` | CLI | CLI store operations |
| `ServerJsonContext` | Server | API request/response DTOs |
| `ConfigJsonContext` | CLI | Workspace config file |
| `PluginClientJsonContext` | CLI | Plugin MCP communication |

Without source-gen contexts, the trimmed CLI binary silently fails to serialize/deserialize.
