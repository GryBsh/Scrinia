# Scrinia.Core Internals

> Shared foundation — encoding, models, search, and the store abstraction. Zero ASP.NET Core dependency.

---

## IMemoryStore Interface

27-method contract for persistent and ephemeral memory:

| Group | Methods | Purpose |
|-------|---------|---------|
| **Naming** | `ParseQualifiedName`, `FormatQualifiedName`, `IsEphemeral`, `SanitizeName` | Name resolution and path safety |
| **CRUD** | `ResolveArtifactAsync`, `LoadIndex`, `SaveIndex`, `Upsert`, `Remove` | Index and artifact operations |
| **Listing/Search** | `ListScoped`, `SearchAll` (2 overloads) | Multi-scope enumeration and hybrid search |
| **Ephemeral** | `RememberEphemeral`, `ForgetEphemeral`, `GetEphemeral` | In-memory transient storage |
| **File I/O** | `WriteArtifactAsync`, `ReadArtifactAsync`, `DeleteArtifact` | `.nmp2` file operations |
| **Copy/Archive** | `CopyMemory`, `ArchiveVersion` | Cross-scope copy and version history |
| **Paths** | `ArtifactPath`, `ArtifactUri`, `FindArtifactPath`, `GetStoreDirForScope` | Filesystem path resolution |
| **Topics** | `DiscoverTopics`, `GatherTopicInfos`, `ResolveReadScopes` | Topic directory discovery |
| **Export/Import** | `ListTopicArtifacts`, `ImportTopicEntries` | Bundle support |
| **Utility** | `GenerateContentPreview` | Content truncation |

---

## FileMemoryStore

Instance-based `IMemoryStore` implementation backed by the filesystem. Located in `Scrinia.Core`.

### Naming Conventions

| Pattern | Scope | Storage Path |
|---------|-------|-------------|
| `"subject"` | local | `{workspace}/.scrinia/store/subject.nmp2` |
| `"topic:subject"` | topic | `{workspace}/.scrinia/topics/topic/subject.nmp2` |
| `"~subject"` | ephemeral | In-memory only (dies with process) |

### Index Locking

Per-scope `ReaderWriterLockSlim` locks stored in `ConcurrentDictionary<string, ReaderWriterLockSlim>`. Read operations (Load, List, Search) acquire read locks; write operations (Save, Upsert, Remove) acquire write locks. This allows concurrent reads while serializing writes.

Write operations use atomic rename (`File.Move` from `.tmp` to `index.json`) for crash safety.

### CachedIndex

Internal `CachedIndex` class wraps `IndexFile` with O(1) name-to-position lookup and lazily computed BM25 corpus stats:

- `Dictionary<string, int> NameToPosition` — maps entry names to array indices for O(1) upsert/remove
- `CorpusStats? Stats` — cached BM25 corpus statistics, invalidated on index mutation
- `GetOrComputeCorpusStats()` — computes once and caches for subsequent searches on the same index version

### Artifact LRU Cache

Bounded LRU cache for decoded artifact content (50 MB default). Uses `Dictionary` + `LinkedList` for O(1) access and eviction:

- Cache key: file path
- Eviction: when total cached bytes exceed limit, removes least-recently-used entries
- Invalidated on write/delete operations

### Path Traversal Protection

`SanitizeName()` strips `..`, replaces `/` and `\` with `_`, removes invalid filename chars, and applies `Path.GetFileName()` as a final safety measure.

### Source-Gen JSON

Private nested `FileStoreJsonContext` for trim/AOT-safe index serialization.

---

## MemoryStoreContext

AsyncLocal indirection for `IMemoryStore`. MCP tools read `MemoryStoreContext.Current` to dispatch calls:

```csharp
public static class MemoryStoreContext
{
    public static IMemoryStore? Current { get; set; } // AsyncLocal-backed
}
```

Set per-request by the server middleware, per-session by the CLI, and overridden per-test for isolation.

---

## Extensibility Interfaces

Two AsyncLocal-based extension points enable plugin integration across both REST and MCP code paths. Both have a static `Default` fallback property for CLI mode (where AsyncLocal doesn't propagate through the generic host).

### ISearchScoreContributor + SearchContributorContext

```csharp
public interface ISearchScoreContributor
{
    Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
        string query, IReadOnlyList<ScopedArtifact> candidates,
        IMemoryStore store, CancellationToken ct);
}

public static class SearchContributorContext
{
    public static ISearchScoreContributor? Current { get; set; } // AsyncLocal
    public static ISearchScoreContributor? Default { get; set; } // Static fallback for CLI
}
```

Plugins provide supplemental search scores (e.g., semantic cosine similarity). Both REST and MCP paths check this context.

### IMemoryEventSink + MemoryEventSinkContext

```csharp
public interface IMemoryEventSink
{
    Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct);
    Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct);
    Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct);
}

public static class MemoryEventSinkContext
{
    public static IMemoryEventSink? Current { get; set; } // AsyncLocal
    public static IMemoryEventSink? Default { get; set; } // Static fallback for CLI
}
```

MCP code path fires events after store/append/forget. REST path uses `IMemoryOperationHook` via `PluginPipeline` instead. No double-firing.

### Context Lifecycle

- **CLI**: Sets `.Default` during startup (AsyncLocal doesn't propagate through generic host to MCP handler threads)
- **Server**: Sets `.Current` per-request via middleware
- **Null check**: All callers check for null, falling back to legacy behavior when no plugin is loaded

---

## SessionBudget

Internal static class tracking estimated token consumption per session. Uses `ConcurrentDictionary<string, long>` with AsyncLocal override for test isolation.

- `RecordAccess(memoryName, charsLoaded)` — thread-safe accumulation
- `TotalCharsLoaded` / `EstimatedTokensLoaded` — session totals (`chars / 4` heuristic)
- `Breakdown` — per-memory `(chars, estimatedTokens)` dictionary

---

## Models

All in `Scrinia.Core.Models`:

| Record | Key Fields | Purpose |
|--------|-----------|---------|
| `ArtifactEntry` | Name, Uri, OriginalBytes, ChunkCount, CreatedAt, Description, Tags, Keywords, TermFrequencies, ChunkEntries, ReviewAfter, ReviewWhen, UpdatedAt, ContentPreview | Index entry (v3 format, 14 fields) |
| `ChunkEntry` | ChunkIndex, ContentPreview, Keywords, TermFrequencies | Per-chunk search metadata |
| `EphemeralEntry` | Name, Artifact (inline text), OriginalBytes, Keywords, TermFrequencies, UpdatedAt | In-memory entry (stores artifact directly, no review fields) |
| `IndexFile` | Version (=3), Entries | On-disk `index.json` wrapper |
| `ScopedArtifact` | Scope, Entry | Entry + scope pair for listing/search |

---

## Storage Scopes

| Scope | Path | Lifetime | Use Case |
|-------|------|----------|----------|
| **local** | `.scrinia/store/` | Persistent | Project-level knowledge |
| **topic** | `.scrinia/topics/{name}/` | Persistent | Categorized knowledge |
| **ephemeral** | In-memory | Session | Scratch/temporary |

Each persistent scope has its own `index.json` (v3 format) and `.nmp2` artifact files.

### V3 Index Schema

```json
{
  "v": 3,
  "entries": [{
    "name": "auth-flow",
    "uri": "file://...auth-flow.nmp2",
    "originalBytes": 1234,
    "chunkCount": 1,
    "createdAt": "2026-01-15T...",
    "description": "OAuth 2.0 implementation notes",
    "tags": ["auth", "oauth"],
    "contentPreview": "First 500 chars...",
    "keywords": ["oauth", "token", "refresh"],
    "termFrequencies": {"oauth": 12, "token": 8},
    "updatedAt": "2026-01-16T...",
    "reviewAfter": "2026-07-15T...",
    "reviewWhen": "when auth system changes",
    "chunkEntries": [{
      "chunkIndex": 1,
      "contentPreview": "...",
      "keywords": ["..."],
      "termFrequencies": {}
    }]
  }]
}
```

V3 fields (added over v2): `keywords`, `termFrequencies`, `updatedAt`, `reviewAfter`, `reviewWhen`, `chunkEntries`. Entries from v2 indexes gracefully degrade (BM25 score = 0).

### Version Archiving

Before overwriting an artifact, `ArchiveVersion()` copies to `{storeDir}/versions/{subject}_{yyyyMMdd-HHmmss}.nmp2`.

---

## IStorageBackend + FilesystemBackend

The server decouples store creation from the filesystem:

```csharp
public interface IStorageBackend
{
    string BackendId { get; }
    IMemoryStore CreateStore(string storeName, string storePath);
}
```

- **Default**: `FilesystemBackend` creates `FileMemoryStore` instances with `Directory.CreateDirectory`
- **StoreManager** accepts `IStorageBackend` via DI, caches `IMemoryStore` instances per store name
- **Plugins** can replace the default by registering a custom `IStorageBackend` singleton in `ConfigureServices`
- **`RequestContext.Store`** is typed `IMemoryStore?` (not `FileMemoryStore?`)
- **`MemoryNaming`** provides `StripEphemeralPrefix` and `FormatScopeLabel` as static utilities, decoupled from any store implementation
- **Health endpoint** reports `backend:{id}` in readiness checks
