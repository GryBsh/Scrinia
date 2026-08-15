# Core Architecture

`Scrinia.Core` is the shared class library containing encoding, models, search, storage, embeddings, and extensibility interfaces. It has no ASP.NET or native dependencies (only `System.IO.Hashing`) and is referenced by all other projects. Built-in semantic search (Model2Vec provider, API embedding providers, VectorStore, HybridReranker) lives in `Scrinia.Core.Embeddings` -- see [Embeddings Architecture](embeddings.md).

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
    string Uri,                                      // file:// path to .nmp2 file
    long OriginalBytes,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    string? ContentPreview = null,                   // first 500 chars, or a curated description
    string[]? Keywords = null,                       // extracted + user-provided
    Dictionary<string, int>? TermFrequencies = null, // for BM25
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null,               // per-chunk metadata
    Dictionary<string, string>? FileHashes = null,   // JSON "codeRefs": dependency path -> content hash (drift baseline); keys also surface as the `file` attribute
    Dictionary<string, string[]>? Attributes = null);// JSON "attributes": unified typed facets
```

Sidecars written before the Fields→Attributes rename stored the dict under the legacy JSON key `"fields"`. `ArtifactEntry.LegacyFields` captures that key at deserialization time; `NormalizeAttributesKey()` folds it into `Attributes` (new key wins) and clears the legacy holder so re-serialization emits only `"attributes"`. Every load seam calls it before an entry escapes.

`Attributes` is multi-valued (`key -> string[]`), lowercase keys, values verbatim. It's `null` for pre-attributes entries and degrades gracefully — no backfill; see [Attributes](#attributes).

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
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null,
    DateTimeOffset? UpdatedAt = null,
    ChunkEntry[]? ChunkEntries = null,
    Dictionary<string, string>? FileHashes = null,     // JSON "codeRefs": same drift baseline as ArtifactEntry
    Dictionary<string, string[]>? Attributes = null);  // JSON "attributes"
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
finalScore = weightedAttributeScore + (normalizedBm25 * 5.0) + supplementalScore
```

Where:
- **weightedAttributeScore**: Attribute-specific matching (0-100+ points)
- **normalizedBm25**: BM25 relevance normalized to 0-100 via min-max
- **supplementalScore**: Plugin-provided scores (e.g., cosine similarity from embeddings)

### Weighted Attribute Scoring

Each query term is matched against entry attributes with different weights:

| Match Type | Score |
|------------|-------|
| Exact name match | 100 |
| Attribute pair match (`key:value` query term matches an `Attributes[key]` value) | 50 |
| Keyword exact match | 40 |
| Attribute value exact match (bare query term equals an attribute value, any key) | 40 |
| Name starts with | 30 |
| Name contains | 20 |
| Keyword contains | 12 |
| Attribute value contains (query term is a substring of an attribute value) | 12 |
| Content preview contains | 5 |

(There is no separate tag or description scoring: `tag` is an ordinary attribute key scored via the attribute rows, and a curated description lives in `ContentPreview`.)

Attribute scores are multiplied by a per-key weight (`KnownAttributes.AttributeWeight(key)`, seeded at 1.0 so it changes no score today; the curation knob that replaced the old EntityKeys-only graph gate). **Legacy entries:** the scorer reads only `entry.Attributes`; `key:value`-shaped legacy `Keywords` (e.g. `person:maria` written before unification) are promoted into `Attributes` by the load-time forward-fill (`AttributeMigration.Forward`), so pre-migration entries score identically without a re-index — the on-the-fly key:value re-parse that once lived in the scorer was retired in P6.5. See `WeightedAttributeScorer.ScoreEntryTerm` in `Scrinia.Core.Search`.

**Chunk scoring** uses parent metadata at half weight plus chunk-specific attributes.

**Topic scoring** follows a similar pattern (exact topic name: 100, tag match: 50, etc.).

**Multi-term intersection bonus:** `(matchedTerms - 1) * 15` -- rewards entries matching multiple query terms.

### BM25

Standard BM25 with parameters `k1 = 1.5`, `b = 0.75`:

- **IDF:** `ln((N - df + 0.5) / (df + 0.5) + 1.0)`
- **TF normalization:** `(tf * (K1 + 1.0)) / (tf + K1 * (1.0 - B + B * (docLen / avgDocLen)))`
- Scores are min-max normalized to 0-100 across the result set

Corpus statistics (average doc length, document frequencies) are computed directly by
`Bm25Scorer.ComputeCorpusStats` on every `WeightedAttributeScorer.Search`/`SearchAll` call
(`WeightedAttributeScorer.cs:80-82`, `:141-143`) — not cached. A per-scope cache for them was
carried on `CachedIndex` and never called; it was deleted on 2026-08-10. See **CachedIndex** below.

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

### Attributes

`Attributes` (`ArtifactEntry.Attributes`, `Dictionary<string, string[]>?`, JSON `"attributes"`) is the unified set of typed facets that replaced the older scheme of encoding structured facts as prefixed keywords (`person:maria`, `file:src/Foo.cs`). An attribute key is either an **entity key** (recommended entity vocabulary) or a **reference key** (feeds cross-reference tracking); everything else is scalar state. Both write paths (MCP `Store`/`Append` and the server orchestrator) route agent keywords, mechanical content extraction, and reference extraction into `Attributes` instead of emitting prefixed keywords. Prefixed-keyword *writes* are retired; prefixed-keyword *reads* are preserved everywhere via dual-read (see below), so entries written before this change keep working with no re-index or backfill.

#### KnownAttributes

`KnownAttributes` in `Scrinia.Core.Search` is the single schema point — the only place that knows which attribute keys are multi-valued and what merge semantics apply:

```csharp
public static class KnownAttributes
{
    public const int MaxAttributes = 32;
    public static readonly IReadOnlySet<string> EntityKeys;     // person, place, org, thing, topic
    public static readonly IReadOnlySet<string> ReferenceKeys;  // file, ref
    public const string ReviewKey = "review";
    public const string TagKey = "tag";                          // legacy free-form tags migrate here
    public const string AnchorKey = "anchor";                    // legacy retrieval anchors migrate here
    public static readonly IReadOnlySet<string> RecommendedKeys; // person, place, org, thing, topic, file, ref, review
    public static double AttributeWeight(string key);            // per-key scoring multiplier, seeded at parity (1.0)
    public static bool IsMultiValued(string key);                // true for EntityKeys ∪ ReferenceKeys
    public static Dictionary<string, string[]>? Merge(existing, incoming);
}
```

- **Entity keys** (`person`, `place`, `org`, `thing`, `topic`) union-merge and are the **recommended** entity vocabulary — but every attribute key feeds the entity graph uniformly, not just these five (see Dual-read below).
- **Reference keys** (`file`, `ref`) union-merge and feed References/Link/orphan tracking plus the **`FileHashes`** builder.
- Per-key influence is a soft weight (`KnownAttributes.AttributeWeight`, seeded at parity — every key `1.0`) that **replaced the old "only EntityKeys build the graph" gate**. `RecommendedKeys` is guidance plus the weight-table seed, not a gate.
- All other keys are scalar: `Merge` treats incoming as newer-wins (first-wins would freeze stale statuses), except multi-valued keys which union-dedupe with existing values keeping their original order.
- `MaxAttributes = 32` caps total attribute keys per entry; existing keys count first against the cap.

#### AttributeExtractor

`AttributeExtractor` in `Scrinia.Core.Search` is the single write-time entry point with three sources feeding one attribute bag:

- **`RouteKeywords(keywords)`** -- agent-keyword routing. Splits agent-supplied keywords into attribute-shaped (`key:value` -> `Attributes`) and plain (stay in `Keywords`, still participate in BM25/keyword scoring).
- **`Extract(content)`** -- mechanical content extraction. Line-by-line scan for `key: value` line-starts and `**Bold:** value` labels (frontmatter lines pass through untouched, not specially gated); skips properly closed code-fenced blocks only — an unterminated fence (an opening ``` with no matching closer) is not a fence at all, and everything after it extracts as ordinary prose. Both rules come from the shared `ProseScanner`.
- **`ExtractRefAttributes(content)`** -- reference extraction. Delegates to `ReferenceExtractor.ExtractFileRefs`/`ExtractMemoryRefs`, both fence-aware via the same `ProseScanner` rule as `Extract`, and packages results as `{file: [...], ref: [...]}`, replacing the retired `file:`/`ref:` prefixed-keyword emission.
- **`SeedTermFrequencies(attributes, tf)`** -- not an extraction source; seeds attribute keys/values into the term-frequency dictionary (+3/token) so BM25 also matches attribute vocabulary.

`ReferenceExtractor` itself (source-generated regexes for file-path and memory-reference patterns) is called directly from two write-path sites, not only underneath `ExtractRefAttributes`: `MaintenanceEventSink.ExtractMemoryRefs` calls it directly to find `[[wiki-link]]` targets for auto-linking and orphan detection (`MaintenanceEventSink.cs:100`). Neither call site emits a `ref:`/`file:`-prefixed keyword any more — that emission is retired — but the write paths' direct use of the extractor did not go away with it.

#### Dual-read

Every consumer of typed/prefixed data reads **both** shapes so old entries (which only ever had prefixed `Keywords`, since `Attributes` didn't exist yet) and new entries (which have `Attributes`) retrieve identically:

- **Scorer** (`WeightedAttributeScorer.ScoreEntryTerm`): scores `entry.Attributes` at 50/40/12 (pair/exact/contains — see the scoring table above), each × the per-key `AttributeWeight`. It does not re-parse `key:value` `Keywords` — that on-the-fly parse was retired in P6.5; the load-time forward-fill promotes such keywords into `Attributes` first.
- **Entity graph** (`EntityGraph.Build`): unions `ParseEntityKeywords(keywords)` (legacy `type:value` keywords) with `ParseEntityAttributes(attributes)`, which yields a graph node for **every** attribute key uniformly — not just `KnownAttributes.EntityKeys`. Any attribute key you name consistently links memories that share its value; the entity/recommended keys are guidance, not a graph gate.
- **Orphan/reference tracking** (`MaintenanceEventSink`): checks both `Keywords` for `ref:...` and `Attributes["ref"]` for inbound references; new links are written forward-only into the `ref` attribute (no more `ref:` keyword emission) via `KnownAttributes.Merge`.
- **FileHashes**: the derivation pipeline (`MemoryDerivation`) unions the explicit `codeRefs` parameter with `Attributes["file"]` (deduplicated case-insensitively) before hashing into `FileHashes` — on `ArtifactEntry` for a persistent write and identically on `EphemeralEntry` for an ephemeral one, both local and remote. An append hashes only newly-referenced paths and keeps an already-baselined path's original hash (see CHANGELOG); a plain `remember()`/`Store` hashes every referenced path. Because `Attributes["file"]` is itself fence-aware (`ExtractRefAttributes`, above), a path mentioned only inside a properly closed fenced block no longer reaches `Attributes["file"]` and therefore no longer reaches `FileHashes` either — a drift baseline can only be built from a file reference that reads as a real reference, not a pasted example.

This is a **forward-only migration**: `Attributes` is `null` for entries written before this change and stays `null` — there is no backfill script. Dual-read at the scorer/graph/orphan layers is what keeps old and new entries behaving the same, permanently, without a re-index.

## Storage

### IMemoryStore

The shared contract: **no member of `IMemoryStore` throws for lack of a local filesystem**. 24
abstract methods a conforming store must implement with no default, plus 5 members with default
bodies a store may override for efficiency, covering all memory operations:

That is the property the split actually achieved, and it is narrower than "a remote store honours
the whole contract in substance". Three members are implemented remotely as silent no-ops rather
than throws — `HttpMemoryStore.LoadIndex` returns `[]`, `Upsert` and `ArchiveVersion` are empty
bodies. Two are defensible: the server performs those writes, so the client-side call has nothing
to do. `LoadIndex` returning `[]` is not — the interface's own default `TryUpdateEntry` loads the
index, finds nothing, and returns `false`, so the async-typing promotion seam silently never
persists remotely. Pre-existing, recorded as a known gap in
`/findings/store-boundaries-deferred-followups`.

**Naming:** `ParseQualifiedName`, `FormatQualifiedName`, `IsEphemeral`, `SanitizeName`

**Write surface:** `StoreAsync`, `AppendAsync` — raw caller input in, the entry the store itself
derived out (see `MemoryDerivation`, above). Neither has a default body: a store added later must
say how it derives rather than silently inheriting a derivation that is wrong for it.

**CRUD:** `LoadIndex`, `Upsert`, `Remove`, `ResolveArtifactAsync`

**Listing:** `ListScoped(scopes)`

**Search:** `SearchAll(SearchQuery query)` — one method, no overloads, no default body. Every
overload it replaced (with/without supplemental scores, with/without `excludeTopics`) carried a
default body that post-filtered after the scorer's top-K cut, so a store that didn't override it
silently returned fewer than `Limit` results for a selective filter — the top-K cut had already
thinned the candidate set before the filter ever ran. Collapsing every filter dimension
(`PathPrefix`, `ExcludeTopics`, `AttributeFilter`, `Scopes`, `Limit`, `Candidates`,
`SupplementalScores`, `EntityGraphScores`) onto one `SearchQuery` record and removing the default
body means a store that cannot pre-filter now fails to compile instead of silently under-filling —
the contract moved from "correct if you remembered to override it" to "correct or it doesn't
build."

**Ephemeral:** `RememberEphemeral`, `ForgetEphemeral`, `GetEphemeral`

**File I/O:** `WriteArtifactAsync`, `ReadArtifactAsync`, `DeleteArtifact`

**Naming / addressing:** `ArtifactUri` — stays on the shared interface (not on `ILocalMemoryStore`
below) because `HttpMemoryStore` implements it for real, returning a `remote://{store}/{scope}/{name}`
URI rather than throwing.

**Copy/Archive:** `CopyMemory`, `ArchiveVersion`

**Topics:** `DiscoverTopics`, `GatherTopicInfos`

**Scope resolution:** `ResolveReadScopes(scopes)`

**Default-bodied members (a store may override for efficiency):** `TryUpdateEntry` (non-atomic
load→mutate→Upsert unless overridden), `TryUpdateEphemeral` (the ephemeral counterpart — an
unsynchronized load→mutate→store unless overridden; both real stores override it as a
compare-and-swap retry loop instead, so a concurrent writer's change is re-applied to the newer
entry rather than silently lost), `GetEntityFactStore` (rebuilds fresh from `ListScoped`
unless overridden), `ListScoped(scopes, excludeTopics)`, and `ResolveReadScopes(scopes,
excludeTopics)` — the last two post-filter `ListScoped`/`ResolveReadScopes` by scope, which is
cheap and correct even unoverridden because scope exclusion is a set-membership check, not a
top-K-sensitive filter the way `pathPrefix`/`attributeFilter` were. `FileMemoryStore` overrides
both for efficient scope-level filtering. There is no `SearchAll(..., excludeTopics)` default
overload anymore: `excludeTopics` for search travels as `SearchQuery.ExcludeTopics`, one field
among several on the single `SearchAll` method above, not a separate overload. The MCP `list` and
`search` actions accept `excludeTopics` so agents can scope queries to specific topic subsets.

`GenerateContentPreview` is not a store member at all — it moved to the static
`Scrinia.Core.ContentPreview.Generate(string content, int maxLength = 500)`. Both prior
implementations (`FileMemoryStore`, `HttpMemoryStore`) had byte-identical bodies that touched no
store state, so it was never really a storage operation.

### ILocalMemoryStore

```csharp
public interface ILocalMemoryStore : IMemoryStore
{
    string ArtifactPath(string name, string scope = "local");
    string FindArtifactPath(string subject, string normalizedScope);
    string GetStoreDirForScope(string scope);
    string WorkspaceRoot { get; }
    List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope);
    void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite);
    bool WriteCarriedCopy(
        string destinationName, string artifact, CarriedMetadata carried, bool overwrite,
        out string message);
}
```

The seven members that only make sense against a local filesystem — six path accessors and
import/export helpers that a remote store could previously implement only by throwing
`NotSupportedException` at runtime, plus `WriteCarriedCopy`, a write that only a filesystem-backed
store can perform rather than forward. `FileMemoryStore` is the sole implementation;
`HttpMemoryStore` implements only `IMemoryStore` and does not implement this interface at all, so a
caller that needs a local path now fails to compile against a remote store instead of discovering
the gap at runtime. `HttpMemoryStore` retains exactly one `NotSupportedException`, unrelated to
this split: rejecting a `file://`-prefixed input to `ResolveArtifactAsync`, which is a rejection of
an input value rather than of a capability.

Callers that need a local path narrow to `ILocalMemoryStore` in one of two ways, chosen by how much
of the method needs the filesystem:

- **Whole-tool refusal** — `ScriniaMcpTools.TryGetLayout` (`Scrinia.Mcp`) reads the ambient store
  and returns `false` instead of throwing, so a local-only tool answers with a structured
  `NOT_SUPPORTED` error. Used where the tool needs the workspace end to end: bundle export/import,
  skill and agent files, conflict scanning, drift detection. `WorkspaceSetup.LocalStore` is the
  CLI's equivalent — but it throws, because the CLI only ever installs a `FileMemoryStore`.
- **Per-branch narrowing** — an inline `is`/`as` test at the point the layout member is actually
  reached, leaving the rest of the method to run. Used where only part of a method needs the
  filesystem: `recall('/agent/*')` prefers a workspace `.md` file and otherwise falls through to
  NMP/2 resolution; `remember()` skips drift hashes; `list(mode:'full')` omits the `[drift]` column;
  `list(mode:'drift')` refuses only that mode; `restore()`'s conflict-scan, skills and agent
  sections are each skipped individually.

Narrowing at the point of use is deliberate. Before the split, `HttpMemoryStore`'s member threw at
the moment of use, so an early return upstream still returned, a validation error was still
returned, and an enclosing best-effort `catch` still swallowed it and skipped just that section.
Hoisting the check to method entry converts all three into hard failures — which is what
`RemoteStoreToolTests` now pins. What the split removes is the *hard cast*: no site casts
`IMemoryStore` to a concrete `FileMemoryStore` to reach a path.

`SaveIndex` left `IMemoryStore` entirely rather than moving to `ILocalMemoryStore` — it has no
production caller outside the store implementations themselves, so it is a public member of
`FileMemoryStore` directly, not part of either interface.

### FileMemoryStore

Instance-based implementation of `ILocalMemoryStore` (which extends `IMemoryStore`). One instance
per workspace (CLI) or per store (server).

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

Internal structure holding a scope's cached index entries:

```csharp
internal sealed class CachedIndex
{
    /// <summary>The cached entries. Handed out by reference from LoadIndex — callers get a
    /// live view of this list, not a copy.</summary>
    public List<ArtifactEntry> Entries { get; }
}
```

`Entries` is the list `LoadIndex` hands back directly on a cache hit — see **Keyword-Only Index
Scans**, below, for why that is safe.

The class previously also carried `NameToPosition`, `Rebuild()` and `GetOrComputeCorpusStats()`.
None had a reader anywhere in the repo, and `NameToPosition` was rebuilt on every cache write for
nothing. They were deleted on 2026-08-10. `Upsert` and `Remove` never consulted the position map —
they reload the scope from sidecars via `LoadEntriesFromSidecars` and replace the `CachedIndex`
outright — and corpus stats are computed directly by `WeightedAttributeScorer` on every search.

### Artifact LRU Cache

50 MB bounded cache for decoded artifact content:
- LinkedList-based LRU eviction
- Thread-safe via `lock`
- Keys include modification timestamp for staleness detection
- Cache miss reads from filesystem and populates cache

### Cross-Process File Locking

`FileLock` in `Scrinia.Core` provides OS-enforced shared/exclusive file locks for safe multi-process access. Per-scope `.lock` files (`.scrinia/store/.lock`, `.scrinia/topics/{topic}/.lock`) prevent concurrent index corruption when multiple CLI processes (or a CLI and a server) access the same workspace.

- **Shared locks** for cold reads (multiple readers allowed) — a `LoadIndex` call that hits the
  in-memory cache takes no file lock at all; see **Keyword-Only Index Scans**, below.
- **Exclusive locks** for writes (single writer, no readers)
- Exponential backoff retry on contention
- Unique PID-based `.tmp` filenames prevent write collisions

`FileMemoryStore` uses file locks for all cold index reads and all index/artifact writes; a
cached `LoadIndex` takes no file lock.

### Concurrency

- Per-scope `ReaderWriterLockSlim` for in-process index CRUD
- Per-scope `CachedIndex` invalidated on write
- `ConcurrentDictionary` for ephemeral store, scope locks, scope caches
- Topic discovery cache with 2-second TTL
- Cross-process `FileLock` for filesystem-level coordination

### Keyword-Only Index Scans

Index entries carry `Keywords` so consumers can filter without decoding artifacts. `LoadIndex(scope)` returns `IReadOnlyList<ArtifactEntry>` (O(1) on a cache hit — the cached list itself, not a copy). Filtering on keywords (e.g. `status:active`, `tag:onboarder`) lets callers reduce a large index to a small candidate set before decoding any payload — important for keeping `list` and `search` responsive on large stores.

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

Records are made when `show()` or `get_chunk()` decode memory content.

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
| `ScriniaMcpJsonContext` | Mcp | Skill and agent file sidecar metadata (`SkillFileMeta`, `AgentFileMeta`) |
| `ServerJsonContext` | Server | API request/response DTOs |
| `ConfigJsonContext` | CLI | Workspace config file |
| `PluginClientJsonContext` | CLI | Plugin MCP communication |
| `CliJsonContext` | CLI | `--json` CLI output |

Without source-gen contexts, the trimmed CLI binary silently fails to serialize/deserialize.

## Resilience

`Scrinia.Core.Resilience` provides transient failure handling for HTTP-based providers (embedding and chat). Zero external dependencies — pure .NET.

### TransientDetector

Classifies failures as transient (retryable) or permanent:
- **HTTP status codes**: 429 (Too Many Requests), 500, 502, 503, 504
- **Exceptions**: `TimeoutException`, `IOException`, `SocketException`, `HttpRequestException` wrapping socket/IO errors

### RetryPolicy

Static class with async and sync retry methods using exponential backoff with jitter:
- **Backoff formula**: `baseDelayMs * 2^attempt + random(0, baseDelayMs)`
- **Retry-After header**: Honored when the response is `HttpResponseMessage` (uses the longer of computed delay or header value)
- **Exception retry**: Catches transient exceptions (via `TransientDetector`) and retries up to `MaxRetries`
- **Result retry**: Caller provides `isTransient` predicate for result-based retry decisions

### CircuitBreaker

Per-provider circuit breaker with three states:
- **Closed**: Normal operation. Consecutive failures tracked via `Interlocked` operations.
- **Open**: Failures reached threshold. All requests rejected with `CircuitBreakerOpenException`. Transitions to HalfOpen after cooldown expires.
- **HalfOpen**: Cooldown expired. Next request is a probe — success closes the circuit, failure re-opens it.

Thread-safe via `Interlocked` and `Volatile` operations (no locks).

### CircuitBreakerRegistry

Static `ConcurrentDictionary<string, CircuitBreaker>` registry. Providers register their circuit breaker instances by name so the `/health/details` endpoint can report state without tight coupling.

### Configuration Records

```csharp
public sealed record RetryOptions(int MaxRetries = 3, int BaseDelayMs = 200);
public sealed record CircuitBreakerOptions(int Threshold = 5, int CooldownSeconds = 30);
```

Both `EmbeddingOptions` (Core) and `ChatOptions` (Server) expose `MaxRetries`, `RetryBaseDelayMs`, `CircuitBreakerThreshold`, and `CircuitBreakerCooldownSeconds` properties that map to these records.
