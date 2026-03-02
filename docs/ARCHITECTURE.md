# Scrinia Architecture

> Persistent, portable memory for LLMs — CLI, MCP server, HTTP API, and web UI.

**Version**: 0.1.0 | **Runtime**: .NET 10.0 | **License**: BSD-3-Clause | **Copyright**: 2026 Nick Daniels

### Related Documentation

- **[Core Internals](core.md)** — IMemoryStore, FileMemoryStore, extensibility, models, storage scopes
- **[Search System](search.md)** — BM25, weighted field scoring, hybrid search, TextAnalysis
- **[Encoding](encoding.md)** — NMP/2 format, chunked encoder, AppendChunk
- **[CLI Reference](cli.md)** — 11 commands, configuration, workspace setup
- **[Server Guide](server.md)** — HTTP API, authentication, deployment, web UI
- **[Plugins](plugins.md)** — embeddings plugin, CLI/server plugins, HnswIndex, HybridReranker
- **[NMP/2 Spec](../NMP_SPEC.md)** — byte-level format specification

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Solution Structure](#2-solution-structure)
3. [Dependency Graph](#3-dependency-graph)
4. [Core Library (Scrinia.Core)](#4-core-library-scriniacore)
5. [MCP Tools (Scrinia.Mcp)](#5-mcp-tools-scriniamcp)
6. [CLI Application (Scrinia)](#6-cli-application-scrinia)
7. [HTTP API Server (Scrinia.Server)](#7-http-api-server-scriniaserver)
8. [Plugin System](#8-plugin-system)
9. [Embeddings Plugin (Scrinia.Plugin.Embeddings)](#9-embeddings-plugin-scriniapluginembeddings)
10. [Web UI](#10-web-ui)
11. [NMP/2 Encoding Format](#11-nmp2-encoding-format)
12. [Search System](#12-search-system)
13. [Storage Architecture](#13-storage-architecture)
14. [Authentication and Authorization](#14-authentication-and-authorization)
15. [Production Hardening](#15-production-hardening)
16. [Testing Architecture](#16-testing-architecture)
17. [Build, CI/CD, and Deployment](#17-build-cicd-and-deployment)
18. [Configuration Reference](#18-configuration-reference)
19. [API Reference](#19-api-reference)

---

## 1. System Overview

Scrinia compresses text into NMP/2 artifacts (Brotli + URL-safe Base64), stores them as named memories in `.scrinia/` directories, and exposes 17 MCP tools for LLM agents to read, write, search, and manage knowledge across sessions.

```
                    ┌──────────────────────────────────────────┐
                    │               Consumers                  │
                    │   Claude Code │ Cursor │ Any MCP Client  │
                    └────────┬──────────────┬──────────────────┘
                             │              │
                    ┌────────▼────┐  ┌──────▼──────────────────┐
                    │  CLI + MCP  │  │     HTTP API Server     │
                    │   (stdio)   │  │  REST + MCP/HTTP + SPA  │
                    │   scri.exe  │  │    Scrinia.Server       │
                    └────────┬────┘  └──────┬──────────────────┘
                             │              │
                    ┌────────▼──────────────▼──────────────────┐
                    │           Scrinia.Mcp                     │
                    │        17 MCP Tool Methods                │
                    └────────────────┬─────────────────────────┘
                                     │
                    ┌────────────────▼─────────────────────────┐
                    │           Scrinia.Core                    │
                    │  Encoding │ Models │ Search │ Store       │
                    └────────────────┬─────────────────────────┘
                                     │
                    ┌────────────────▼─────────────────────────┐
                    │         .scrinia/ filesystem              │
                    │   store/ │ topics/ │ ephemeral (memory)   │
                    └──────────────────────────────────────────┘
```

Three deployment modes:
- **CLI + MCP stdio**: `scri serve` — single-user, runs as MCP server over stdio
- **HTTP API Server**: `dotnet run --project Scrinia.Server` — multi-user, multi-store, API key auth
- **Docker**: `docker compose up` — containerized server with persistent volume

---

## 2. Solution Structure

```
Scrinia.sln (11 projects)
│
├── src/
│   ├── Scrinia.Core/                    net10.0 classlib
│   │   ├── Encoding/                    NMP/2 codec (Brotli + Base64url)
│   │   ├── Models/                      ArtifactEntry, ChunkEntry, IndexFile, etc.
│   │   ├── Search/                      BM25 + weighted field hybrid search
│   │   ├── IMemoryStore.cs              27-method store abstraction
│   │   ├── FileMemoryStore.cs           Filesystem implementation
│   │   ├── MemoryStoreContext.cs        AsyncLocal dispatch
│   │   └── SessionBudget.cs             Token consumption tracking
│   │
│   ├── Scrinia.Mcp/                     net10.0 classlib
│   │   └── ScriniaMcpTools.cs           17 MCP tools (sealed, no DI)
│   │
│   ├── Scrinia/                         net10.0 exe (AssemblyName: scri)
│   │   ├── Program.cs                   CLI entry point (3 lines)
│   │   ├── Commands/                    ScriniaCommands (11 commands), WorkspaceSetup, WorkspaceConfig
│   │   ├── Mcp/                         ScriniaArtifactStore (static CLI store)
│   │   ├── Services/                    McpPluginHost, PluginClientJsonContext
│   │   └── HttpMemoryStore.cs           Remote IMemoryStore proxy
│   │
│   ├── Scrinia.Plugin.Abstractions/     net10.0 classlib
│   │   ├── IScriniaPlugin.cs            Plugin lifecycle interface
│   │   ├── ScriniaPluginBase.cs         Convenience base class
│   │   ├── IMemoryOperationHook.cs      Before/After hooks
│   │   └── HookContexts.cs              6 context classes (Store/Append/Forget × Before/After)
│   │
│   ├── Scrinia.Server/                  net10.0 web (ASP.NET Core)
│   │   ├── Program.cs                   Full middleware pipeline + bootstrap
│   │   ├── Auth/                        ApiKeyStore, ApiKeyAuthHandler, RequestContext
│   │   ├── Endpoints/                   MemoryEndpoints, KeyEndpoints, HealthEndpoints
│   │   ├── Services/                    StoreManager, MemoryOrchestrator, PluginLoader, PluginPipeline
│   │   ├── Models/                      ApiDtos (20+ records), ServerJsonContext
│   │   └── Middleware/                  RequestTimingMiddleware
│   │
│   ├── Scrinia.Plugin.Embeddings/       net10.0 classlib
│   │   ├── EmbeddingsPlugin.cs          IScriniaPlugin + ISearchScoreContributor + IMemoryEventSink
│   │   ├── EmbeddingOptions.cs          Config POCO (Provider, Hardware, SemanticWeight)
│   │   ├── IEmbeddingProvider.cs        Provider abstraction
│   │   ├── NullEmbeddingProvider.cs     No-op fallback
│   │   ├── EmbeddingProviderFactory.cs  Factory (onnx/ollama/openai/none)
│   │   ├── VectorStore.cs               Per-scope binary vector storage (SVF2 format, append-only)
│   │   ├── VectorIndex.cs               SIMD cosine similarity + flat-scan search
│   │   ├── HnswIndex.cs                 Hierarchical Navigable Small World graph (ANN search)
│   │   ├── HybridReranker.cs            ISearchScoreContributor: re-ranks BM25 top-K with cosine
│   │   ├── Models/VectorEntry.cs        (Name, ChunkIndex?, Vector) record
│   │   ├── Onnx/                        ModelManager, HardwareDetector, BertTokenizer, OnnxInferenceSession
│   │   └── Providers/                   OnnxEmbeddingProvider, OllamaEmbeddingProvider, OpenAiEmbeddingProvider
│   │
│   └── Scrinia.AppHost/                 .NET Aspire AppHost
│       └── Program.cs                   Orchestrates Scrinia.Server
│
├── tests/
│   ├── Scrinia.Tests/                   325 tests (xunit + FluentAssertions)
│   ├── Scrinia.Server.Tests/            53 tests (WebApplicationFactory)
│   └── Scrinia.Plugin.Embeddings.Tests/ 38 tests (VectorIndex, VectorStore, HybridScorer, BertTokenizer, HNSW)
│
└── web/                                 React 19 + Vite + Tailwind CSS 4
    └── src/                             SPA: Login, Dashboard, MemoryBrowser, MemoryDetail, KeyManagement
```

**Total: 416 tests across 3 test projects.**

---

## 3. Dependency Graph

```
Scrinia.Core ← System.IO.Hashing 9.0.0
    ↑
Scrinia.Mcp ← ModelContextProtocol 1.0.0
    ↑
Scrinia (CLI) ← Spectre.Console, ConsoleAppFramework 5.7.13, Microsoft.Extensions.Hosting

Scrinia.Core
    ↑
Scrinia.Plugin.Abstractions ← FrameworkReference: Microsoft.AspNetCore.App
    ↑
Scrinia.Plugin.Embeddings ← Microsoft.ML.OnnxRuntime, Microsoft.ML.OnnxRuntime.DirectML (Windows)
    ↑                         refs: Core + Plugin.Abstractions
    │
Scrinia.Server ← Microsoft.Data.Sqlite, Microsoft.AspNetCore.OpenApi,
                   Scalar.AspNetCore, ModelContextProtocol.AspNetCore
                   refs: Core + Mcp + Plugin.Abstractions
```

### InternalsVisibleTo

Core exposes internals to: `scri` (CLI), `Scrinia.Mcp`, `Scrinia.Server`, `Scrinia.Tests`, `Scrinia.Server.Tests`.

---

## 4. Core Library (Scrinia.Core)

The shared foundation — encoding, models, search, and the store abstraction. Zero ASP.NET Core dependency.

> **Detailed reference:** [Core Internals](core.md) — IMemoryStore (27 methods), FileMemoryStore (ReaderWriterLockSlim, CachedIndex, LRU cache), extensibility interfaces, models, storage scopes, v3 index schema.

Key components: `IMemoryStore` (27-method contract), `FileMemoryStore` (ReaderWriterLockSlim locking, CachedIndex with O(1) lookup, 50 MB LRU artifact cache), `MemoryStoreContext` (AsyncLocal dispatch), `SessionBudget` (token tracking), `ISearchScoreContributor` + `IMemoryEventSink` (plugin extensibility with static `Default` fallback for CLI).

---

## 5. MCP Tools (Scrinia.Mcp)

17 tools in `ScriniaMcpTools` (sealed class, no constructor, no DI). Uses `MemoryStoreContext.Current` via `CurrentStore` property.

| Tool | Parameters | Returns | Purpose |
|------|-----------|---------|---------|
| `guide` | — | markdown | Session playbook (call once) |
| `encode` | `content[]` | NMP/2 artifact | Compress text to artifact |
| `chunk_count` | `artifactOrName` | int | Count independently decodable chunks |
| `get_chunk` | `artifactOrName`, `chunkIndex` | text | Decode one chunk (1-based) |
| `show` | `artifactOrName` | text | Decode full artifact |
| `store` | `content[]`, `name`, `description?`, `tags?`, `keywords?`, `reviewAfter?`, `reviewWhen?` | status | Compress + persist |
| `list` | `scopes?` | table | Formatted inventory |
| `search` | `query`, `scopes?`, `limit?` | table | Hybrid BM25 + field search |
| `copy` | `nameOrUri`, `destination`, `overwrite?` | status | Copy between scopes |
| `forget` | `nameOrUri` | status | Delete memory + index entry |
| `export` | `topics[]`, `filename?` | status | Export topics to `.scrinia-bundle` |
| `import` | `bundlePath`, `topics?`, `overwrite?` | status | Import from bundle |
| `append` | `content`, `name` | status | Append chunk (or create if new) |
| `reflect` | — | markdown | Session-end checklist |
| `ingest` | — | markdown | 5-phase knowledge capture playbook |
| `kt` | `scopes?` | markdown | Knowledge transfer briefing |
| `budget` | — | table | Token consumption report |

**Key design decisions:**
- `store()` performs text analysis: keyword extraction, term frequency computation, keyword TF boosting (+5 agent, +2 auto), and per-chunk metadata for multi-chunk content.
- `append()` uses `Nmp2ChunkedEncoder.AppendChunk()` for surgical chunk promotion: single-chunk → 2-chunk, or adds to existing multi-chunk. Re-computes keywords and TF across all content.
- `show()` records access via `SessionBudget.RecordAccess()`.

**Supporting types (defined in same file):**
- `BundleIndex(List<ArtifactEntry> Entries)` — per-topic index in bundles
- `BundleManifest(int Version, string Exported, List<string> Topics, int TotalEntries)` — bundle header
- `BundleJsonContext` — source-gen JSON for bundle serialization

---

## 6. CLI Application (Scrinia)

**Entry point** (3 lines):
```csharp
var app = ConsoleApp.Create();
app.Add<ScriniaCommands>();
await app.RunAsync(args);
```

ConsoleAppFramework v5 — source-gen, zero-reflection, trim/AOT safe.

### 6.1 Commands (11)

| Command | Key Parameters | Description |
|---------|---------------|-------------|
| `serve` | `--remote`, `--api-key`, `--store`, `--stdio` | Start MCP server (stdio transport) |
| `list` | `--scopes` | Table of all memories |
| `search` | `query`, `--scopes`, `--limit` | Hybrid search |
| `store` | `name`, `file?`, `--description`, `--tags` | Store from file or stdin |
| `show` | `name`, `--output` | Decode and display |
| `forget` | `name` | Delete memory |
| `export` | `topics`, `--filename` | Export topics to bundle |
| `import` | `path`, `--topics`, `--overwrite` | Import from bundle |
| `bundle` | `topic`, `files` | Bundle raw files (CLI-only, no MCP equivalent) |
| `setup` | `--workspace-root` | Download ONNX embedding model |
| `config` | `key?`, `value?`, `--unset` | Get/set workspace configuration |

### 6.2 Workspace Discovery

`WorkspaceSetup.Configure(workspaceRoot?)`:
1. If `--workspaceRoot` is provided, use it directly
2. Otherwise, walk up directory tree from `cwd` looking for `.scrinia/` (git-like discovery)
3. Fall back to `cwd` if no `.scrinia/` found
4. Configure both `ScriniaArtifactStore` (CLI static store) and `MemoryStoreContext.Current` (FileMemoryStore for MCP tools)

### 6.3 Store Implementations

**ScriniaArtifactStore** — Static class in `Scrinia/Mcp/`. Uses AsyncLocal overrides for test isolation. Mirrors `FileMemoryStore` API but as static methods.

**HttpMemoryStore** — `IMemoryStore` implementation that proxies to a Scrinia.Server REST API. Used when `scri serve --remote <url>` is passed. Ephemeral storage stays client-side. Has its own source-gen JSON context (`HttpStoreJsonContext`).

---

## 7. HTTP API Server (Scrinia.Server)

ASP.NET Core minimal API. Multi-store, multi-user, with full production hardening.

### 7.1 Startup Sequence

1. **Kestrel config**: 10 MB max request body, 50 MB multipart form limit
2. **Data directory**: from `Scrinia:DataDir` config or `%LOCALAPPDATA%/scrinia-server`
3. **Store definitions**: from `Scrinia:Stores` section (name → path), default store `"default"`
4. **Plugin loading**: scan `{dataDir}/plugins/*.dll` via `PluginLoader`
5. **Service registration**: `ApiKeyStore`, `StoreManager`, `RequestContext`, plugin DI, `PluginPipeline`
6. **Auth**: API key scheme + authorization policies
7. **CORS, rate limiting, OpenAPI, MCP over HTTP, JSON source-gen**
8. **Bootstrap key**: if no keys exist, creates admin key with all permissions, writes raw key to `BOOTSTRAP_KEY` file

### 7.2 Middleware Pipeline (in order)

```
Request
  → Global exception handler (500 + sanitized JSON)
  → HSTS + HTTPS redirect (prod only)
  → Security headers (X-Content-Type-Options, X-Frame-Options, etc.)
  → Static files (Web UI, no auth required)
  → RequestTimingMiddleware (logs method, path, status, elapsed)
  → CORS
  → Rate limiter (100/min sliding window)
  → Authentication
  → Authorization
  → RequestContext middleware (populates UserId, Stores, Permissions,
    resolves {store} route → 404/403/FileMemoryStore)
  → Plugin middleware (each plugin's ConfigureMiddleware)
  → Endpoints
  → SPA fallback (index.html)
```

### 7.3 Services

| Service | Lifetime | Purpose |
|---------|----------|---------|
| `ApiKeyStore` | Singleton | SQLite-backed API key CRUD (SHA-256 hashed) |
| `StoreManager` | Singleton | ConcurrentDictionary cache of named `FileMemoryStore` instances |
| `RequestContext` | Scoped | Per-request auth context (UserId, Stores, Permissions, ActiveStore) |
| `PluginPipeline` | Singleton | Wraps `MemoryOrchestrator` with before/after hooks |
| `IReadOnlyList<IScriniaPlugin>` | Singleton | Loaded plugin instances |

**MemoryOrchestrator** — Static class with `StoreAsync`, `AppendAsync`, `ShowAsync`, `ForgetAsync`. Encapsulates encoding + text analysis + version archiving + index operations. Takes `IMemoryStore` as parameter.

**BundleService** — Static class for `.scrinia-bundle` ZIP export/import.

---

## 8. Plugin System

### 8.1 Abstractions

```csharp
public interface IScriniaPlugin
{
    string Name { get; }
    string Version { get; }
    int Order => 0;
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void ConfigureMiddleware(IApplicationBuilder app);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
```

`ScriniaPluginBase` provides empty virtual defaults.

### 8.2 Memory Operation Hooks

```csharp
public interface IMemoryOperationHook
{
    int Order => 0;
    Task OnBeforeStoreAsync(BeforeStoreContext context, CancellationToken ct = default);
    Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default);
    // + BeforeAppend/AfterAppend, BeforeFoget/AfterForget
}
```

All methods have default implementations returning `Task.CompletedTask`.

**Before contexts** have `Cancel` + `CancelReason` properties. When a hook sets `Cancel = true`, `PluginPipeline` throws `OperationCanceledException`, which endpoints catch and return as `409 Conflict`.

**After contexts** include content for encoding extensibility:
- `AfterStoreContext.Content` (`string[]`) — the stored content elements
- `AfterAppendContext.Content` (`string`) — the appended content

### 8.3 Search Score Contributors

Plugins can contribute supplemental search scores via `ISearchScoreContributor` (§4.4). The `PluginPipeline.SearchAsync()` method orchestrates this:

1. Get `ISearchScoreContributor` from `SearchContributorContext.Current`
2. If available, gather candidates via `store.ListScoped(scopes)`
3. Call `ComputeScoresAsync()` to get supplemental scores
4. Pass scores to `store.SearchAll()` overload (§12.1)

Both REST (via `PluginPipeline.SearchAsync`) and MCP (via `ScriniaMcpTools.Search`) use the same pattern.

### 8.4 MCP Event Sink

The MCP code path (`ScriniaMcpTools`) fires `IMemoryEventSink` events after mutations:
- `Store()` → `OnStoredAsync(qualifiedName, content, store, ct)`
- `Append()` → `OnAppendedAsync(qualifiedName, content, store, ct)`
- `Forget()` → `OnForgottenAsync(qualifiedName, wasDeleted, store, ct)`

Events are fire-and-forget with `try/catch` — plugin errors never block the tool response. The REST path does NOT set `MemoryEventSinkContext` (hooks handle it).

### 8.5 Server Integration Points

1. **Plugin loading**: `PluginLoader` scans `*.dll` in plugins directory. Each DLL gets an isolated `PluginAssemblyLoadContext` that falls back to Default ALC for `Scrinia.*`, `Microsoft.*`, `System.*` assemblies.
2. **DI**: `plugin.ConfigureServices()` called during service registration
3. **Hooks**: Plugins implementing `IMemoryOperationHook` are registered as singletons
4. **Middleware**: `plugin.ConfigureMiddleware()` called after auth, before endpoints
5. **Endpoints**: `plugin.MapEndpoints()` called with `/api/v1/plugins` route group

**PluginPipeline** has zero overhead when no hooks are registered (array length check).

---

## 9. Embeddings Plugin (Scrinia.Plugin.Embeddings)

Drop-in plugin that adds semantic search via vector embeddings. Implements three core interfaces:
- `ISearchScoreContributor` — contributes cosine similarity scores to search (both REST and MCP)
- `IMemoryEventSink` — indexes embeddings on store/append/forget (MCP path)
- `IMemoryOperationHook` — indexes embeddings on store/append/forget (REST path)

### 9.1 Architecture

```
Store/Append → EmbedAndIndexAsync → IEmbeddingProvider.EmbedAsync → VectorStore.UpsertAsync
Search       → ComputeScoresAsync → IEmbeddingProvider.EmbedAsync → VectorIndex.Search (cosine)
Forget       → RemoveVectorsAsync → VectorStore.RemoveAsync
```

### 9.2 Embedding Providers

| Provider | Config | Description |
|----------|--------|-------------|
| `onnx` (default) | `Provider: "onnx"` | Local ONNX Runtime, all-MiniLM-L6-v2 (384-dim, ~80MB) |
| `ollama` | `Provider: "ollama"` | HTTP POST to Ollama `/api/embed` |
| `openai` | `Provider: "openai"` | HTTP POST to OpenAI embeddings endpoint |
| `none` | `Provider: "none"` | Null provider (disabled) |

**ONNX pipeline:** `HardwareDetector` (CUDA → DirectML → CPU) → `ModelManager` (auto-download from HuggingFace) → `BertTokenizer` (WordPiece, 30,522 tokens) → `OnnxInferenceSession` (tokenize → infer → mean pool → L2 normalize).

### 9.3 Vector Storage (SVF2 Format)

Append-only binary file per scope at `{dataDir}/plugins/embeddings/{storeName}/{scope}/vectors.bin`:
```
[magic "SVF2" 4B] [dimensions uint16]
Operation: [op uint8 (0=add, 1=delete)] [nameLen uint16] [nameUtf8] [chunkIndex int32 (-1=none)] [vector float32[dims]]
```

- **Append-only**: new entries and deletions are appended as operations
- **Compaction**: when deletes exceed 20% of total operations (and >= 10 deletes), the file is rewritten
- Atomic writes (`.tmp` → rename), per-scope `SemaphoreSlim` locking
- Ephemeral scope: in-memory only (not persisted)
- Legacy SVF1 files are read-only (auto-migrated on next write)
- `VectorIndex.Search`: flat-scan cosine similarity using `System.Numerics.Vector<float>` SIMD
- `HnswIndex`: Hierarchical Navigable Small World graph for approximate nearest neighbor search (M=16, efConstruction=200)
- `HybridReranker`: `ISearchScoreContributor` implementation that re-ranks only BM25 top-K candidates with cosine similarity

### 9.4 Scoring

Default `SemanticWeight = 50.0`: a perfect cosine similarity (1.0) adds 50 points, competitive with exact name match (100) or tag match (50).

### 9.5 Configuration

```json
{ "Scrinia": { "Embeddings": { "Provider": "onnx", "Hardware": "auto", "SemanticWeight": 50.0 } } }
```

### 9.6 Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/embeddings/status` | Provider, hardware, availability, dimensions, vector count |
| POST | `/embeddings/reindex` | Re-embed all existing memories in the current store |

### 9.7 Graceful Degradation

At every level, missing embeddings silently fall back to BM25-only:
- No plugin loaded → contexts are null → old scoring path
- Model download fails → `IsAvailable = false` → skip embedding
- Embed fails at store time → catch, continue without vector
- Old memories without vectors → `supplementalScore = 0` → ranked by BM25+field only
- `/embeddings/reindex` backfills vectors for existing memories

---

## 10. Web UI

React 19 + TypeScript + Vite 7 + Tailwind CSS 4 + React Router 7 + TanStack Query.

### 9.1 Pages

| Route | Component | Purpose |
|-------|-----------|---------|
| `/login` | `LoginPage` | API key input |
| `/` | `DashboardPage` | Health checks, store cards |
| `/stores/:store` | `MemoryBrowserPage` | Memory list, search, scope filters |
| `/stores/:store/memories/:name` | `MemoryDetailPage` | Content viewer, chunk navigation, delete |
| `/keys` | `KeyManagementPage` | API key CRUD, create form, revoke |

### 9.2 Auth Flow

`ProtectedRoute` checks `hasToken()` (localStorage `scrinia-api-key`). Redirects to `/login` if no token.

### 9.3 Build Integration

- `vite build` outputs to `Scrinia.Server/wwwroot/` (SPA served as static files)
- MSBuild target `BuildWebUI` auto-runs `npm ci && npm run build` if `wwwroot/index.html` is missing
- Dev proxy: `vite dev` on `:5173` proxies `/api`, `/health`, `/mcp` to `:5000`

### 9.4 Shared Components

- `Layout` — Sidebar navigation with health indicator, sign-out
- `MemoryList` — Table display with scope badges, formatted sizes/dates
- `MemoryContent` — Monospace `<pre>` with dark theme
- `ChunkViewer` — Chunk navigation buttons + per-chunk fetch via TanStack Query
- `SearchBar` — Search form with icon

---

## 11. NMP/2 Encoding Format (Named Memory Protocol v2)

Brotli-compressed, URL-safe Base64 encoded artifacts with CRC32 integrity.

> **Detailed reference:** [Encoding](encoding.md) — Nmp2ChunkedEncoder API, single/multi-chunk formats, AppendChunk with incremental CRC32, compression density.

See also [NMP_SPEC.md](../NMP_SPEC.md) for the byte-level format specification.

Key design: no auto-chunking — single elements always produce single-chunk format. Multi-chunk only via explicit multiple elements or `AppendChunk()`. `AppendChunk` uses `Crc32Combine` for incremental CRC32 (no re-read of existing data). Density: ~0.68-0.76 chars/byte.

---

## 12. Search System

Hybrid BM25 + weighted field + semantic scoring in `Scrinia.Core.Search`.

> **Detailed reference:** [Search System](search.md) — scoring formula, BM25 normalization, intersection bonus, min-heap top-K, weighted field tables, keyword TF boosting, TextAnalysis API.

```
finalScore = weightedFieldScore + normalizedBm25 × 5.0 + supplementalScore
```

Key features: BM25 min-max normalized to 0–100, multi-term intersection bonus `(matchedTerms - 1) × 15`, min-heap top-K via `PriorityQueue`, inline deduplication via `bestPerMemory` dict, keyword TF boost (+5 agent, +2 auto), `CachedIndex` for corpus stats reuse.

---

## 13. Storage Architecture

> **Detailed reference:** [Core Internals](core.md) — three scopes, v3 index schema, version archiving, IStorageBackend extensibility.

Three scopes: **local** (`.scrinia/store/`), **topic** (`.scrinia/topics/{name}/`), **ephemeral** (in-memory). Each persistent scope has its own v3 `index.json` and `.nmp2` artifact files. V3 fields include `keywords`, `termFrequencies`, `chunkEntries`, `reviewAfter`, `reviewWhen`. Version archiving copies `.nmp2` to `versions/` before overwrite.

Bundle format (`.scrinia-bundle`): ZIP containing `manifest.json` + `topics/{name}/index.json` + `topics/{name}/{subject}.nmp2`.

---

## 14. Authentication and Authorization

### 14.1 API Key Authentication

- **Format**: `scri_` + 32 random bytes base64url (e.g., `scri_aB3dE5fG7h...`)
- **Storage**: SHA-256 hash in SQLite (`scrinia-keys.db`)
- **Schema**: `api_keys` (id, key_hash, user_id, permissions JSON, label, created/last_used/revoked) + `key_stores` (FK, store_name)
- **Validation**: Hash incoming key, lookup by hash, check not revoked, update `last_used_at`
- **Claims**: `NameIdentifier`, `store[]`, `permission[]`
- **Bootstrap**: First run creates admin key with all permissions, writes raw key to `BOOTSTRAP_KEY` file

### 14.2 Granular Permission Model

Permissions enforced per-endpoint:

| Permission | Description | Endpoints |
|-----------|-------------|-----------|
| `read` | View memory content/chunks | GET memories/{name}, GET chunks/{i} |
| `search` | Search and list | GET memories, GET search |
| `store` | Create/overwrite memories | POST memories |
| `append` | Append chunks | POST memories/{name}/append |
| `forget` | Delete memories | DELETE memories/{name} |
| `copy` | Copy between scopes | POST memories/{name}/copy |
| `export` | Export bundles | POST export |
| `import` | Import bundles | POST import |
| `manage_keys` | API key CRUD | /api/v1/keys/* |

**Privilege escalation prevention** in key creation: caller cannot grant stores or permissions they don't have.

### 14.3 RequestContext

Scoped per-request context populated by inline middleware after authentication:

```csharp
public sealed class RequestContext
{
    public string UserId { get; set; }
    public string[] Stores { get; set; }      // from "store" claims
    public string[] Permissions { get; set; }  // from "permission" claims
    public string? ActiveStore { get; set; }
    public IMemoryStore? Store { get; set; }

    public bool HasPermission(string permission);
    public bool CanAccessStore(string storeName);  // supports "*" wildcard
}
```

---

## 15. Production Hardening

| Feature | Implementation |
|---------|---------------|
| **Global exception handler** | Sanitizes unhandled errors to `{"error":"An internal error occurred."}` (500) |
| **HTTPS/HSTS** | Enforced in production (`!IsDevelopment()`) |
| **Security headers** | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-XSS-Protection: 0` |
| **Request size limits** | Kestrel 10 MB max body, 50 MB multipart form |
| **Input validation** | 256 char name limit, 5 MB per content element |
| **Rate limiting** | 100 requests/min sliding window (6 segments), 429 on exceed |
| **Path traversal** | `SanitizeName()` strips `..`, `/`, `\`, applies `Path.GetFileName()` |
| **Graceful shutdown** | `app.RunAsync()` + `ApplicationStopping` disposes `ApiKeyStore` |
| **Request timing** | Logs method, path, status code, elapsed ms for every request |
| **Atomic writes** | Index saved to `.tmp` then `File.Move` for crash safety |
| **Docker** | Non-root `scrinia` user, dedicated data volume |

---

## 16. Testing Architecture

### 16.1 Test Suite Summary

| Project | Tests | Framework | Key Patterns |
|---------|-------|-----------|-------------|
| `Scrinia.Tests` | 325 | xunit + FluentAssertions | `TestHelpers.StoreScope` for isolation |
| `Scrinia.Server.Tests` | 53 | + WebApplicationFactory | `ScriniaServerFactory` with temp data dir |
| `Scrinia.Plugin.Embeddings.Tests` | 38 | xunit + FluentAssertions | VectorIndex, VectorStore, HybridScorer, BertTokenizer, OnnxProvider, HNSW |
| **Total** | **416** | | |

### 16.2 Test Isolation

**StoreScope** (Core/CLI tests): Creates temp directory with `.scrinia/store/` structure, overrides all AsyncLocal stores (`ScriniaArtifactStore`, `MemoryStoreContext`, `SessionBudget`, ephemeral store). Resets all overrides on `Dispose`.

**ScriniaServerFactory** (Server tests): `WebApplicationFactory<Program>` that provisions temp data dir, two stores (`test-store`, `store-2`), and a test API key with all permissions. Cleans up temp dir on dispose.

### 16.3 Test Categories by File

**Scrinia.Tests (11 files):**

| File | Tests | Coverage |
|------|-------|----------|
| `ScriniaMcpToolsTests` | 123 | All 17 MCP tools + edge cases |
| `Nmp2StrategyTests` | 36 | Encoding format, roundtrip, headers |
| `ScriniaArtifactStoreTests` | 36 | CLI store CRUD, scopes, search |
| `Nmp2ChunkedEncoderTests` | 19 | Single/multi chunk, append |
| `FileMemoryStoreTests` | 18 | Store implementation |
| `TextAnalysisTests` | 15 | Tokenizer, TF, keywords |
| `Bm25ScorerTests` | 10 | BM25 scoring |
| `ChunkIndexingTests` | 10 | Chunk-level indexing and search |
| `KtToolTests` | 9 | Knowledge transfer tool |
| `SessionBudgetTests` | 5 | Budget tracking |
| `Nmp2BenchmarkTests` | 3 | Compression benchmarks |

**Scrinia.Server.Tests (10 files):**

| File | Tests | Coverage |
|------|-------|----------|
| `PluginPipelineTests` | 13 | Hook lifecycle, cancellation, ordering |
| `MemoryEndpointTests` | 10 | Full CRUD cycle |
| `AuthTests` | 6 | Auth flows (missing/invalid/revoked/valid key, store access) |
| `PermissionTests` | 4 | Privilege escalation prevention |
| `KeyManagementTests` | 4 | Key CRUD + revoke |
| `HealthEndpointTests` | 3 | Health probes |
| `McpEndpointTests` | 3 | MCP over HTTP |
| `RateLimitTests` | 2 | Rate limiting + health exempt |
| `StoreIsolationTests` | 2 | Cross-store isolation |
| `OpenApiTests` | 2 | OpenAPI spec + Scalar UI |

**Scrinia.Plugin.Embeddings.Tests (5 files):**

| File | Tests | Coverage |
|------|-------|----------|
| `VectorIndexTests` | 8 | Cosine similarity, SIMD search, edge cases |
| `VectorStoreTests` | 7 | Binary persistence, upsert, remove, ephemeral |
| `HybridScorerTests` | 3 | Supplemental score integration via FileMemoryStore |
| `BertTokenizerTests` | 2 | WordPiece tokenization (SkippableFact, requires model) |
| `OnnxEmbeddingProviderTests` | 2 | ONNX inference (SkippableFact, requires model) |

---

## 17. Build, CI/CD, and Deployment

### 17.1 Build Commands

```bash
dotnet build                           # Build all projects
cd Scrinia.Tests && dotnet test        # 325 core tests
cd Scrinia.Server.Tests && dotnet test # 53 server tests
cd Scrinia.Plugin.Embeddings.Tests && dotnet test # 38 embeddings tests
cd web && npm run dev                  # Vite dev server on :5173
```

### 17.2 CI Pipeline (`.github/workflows/ci.yml`)

Triggers on push to `main` and PRs targeting `main`:
1. Setup .NET 10 preview + Node 22
2. Build web UI: `npm ci && npm run build`
3. `dotnet restore && dotnet build -c Release`
4. Run `Scrinia.Tests` and `Scrinia.Server.Tests`

### 17.3 Release Pipeline (`.github/workflows/release.yml`)

Triggered on GitHub Release publish. Three parallel jobs:
1. **CLI**: Self-contained, single-file, trimmed for win-x64, linux-x64, osx-arm64
2. **Server**: Self-contained, single-file (not trimmed) for same 3 RIDs
3. **Docker**: Build + push to GHCR (`ghcr.io/{owner}/scrinia-server`)

### 17.4 Docker

```yaml
services:
  scrinia-server:
    build: { context: ., dockerfile: Scrinia.Server/Dockerfile }
    ports: ["8080:8080"]
    volumes: [scrinia-data:/data]
    environment: [Scrinia__DataDir=/data]
```

Multi-stage Dockerfile: restore → web-build (Node) → .NET build → publish → runtime (non-root user).

### 17.5 Publish Scripts

`publish.ps1` / `publish.sh`: Publish CLI for 3 RIDs with `PublishTrimmed=true`, self-contained, single-file.

---

## 18. Configuration Reference

### 18.1 Server Configuration

```jsonc
{
  "Scrinia": {
    "DataDir": "/path/to/data",        // Default: %LOCALAPPDATA%/scrinia-server
    "PluginsDir": "/path/to/plugins",   // Default: {DataDir}/plugins
    "Stores": {
      "default": "",                    // Empty = {DataDir}/stores/default
      "archive": "/custom/path"
    },
    "CorsOrigins": ["http://localhost:5173"]
  }
}
```

### 18.2 Embeddings Plugin Configuration

```jsonc
{
  "Scrinia": {
    "Embeddings": {
      "Provider": "onnx",       // "onnx" | "ollama" | "openai" | "none"
      "Hardware": "auto",       // "auto" | "cuda" | "directml" | "cpu"
      "SemanticWeight": 50.0,   // Multiplier for cosine similarity in search scoring
      "OllamaBaseUrl": "http://localhost:11434",
      "OllamaModel": "all-minilm",
      "OpenAiApiKey": "",
      "OpenAiModel": "text-embedding-3-small"
    }
  }
}
```

---

## 19. API Reference

### 19.1 Memory Endpoints (`/api/v1/stores/{store}/`)

All require authentication + store access. Rate limited at 100/min.

| Method | Path | Permission | Description |
|--------|------|-----------|-------------|
| POST | `/memories` | `store` | Create/overwrite memory |
| GET | `/memories` | `search` | List all memories |
| GET | `/memories/{name}` | `read` | Show decoded content |
| DELETE | `/memories/{name}` | `forget` | Delete memory |
| POST | `/memories/{name}/append` | `append` | Append chunk |
| POST | `/memories/{name}/copy` | `copy` | Copy between scopes |
| GET | `/memories/{name}/chunks/{i}` | `read` | Get chunk by index |
| GET | `/search?q=...` | `search` | Hybrid search |
| POST | `/export` | `export` | Export topics to bundle |
| POST | `/import` | `import` | Import from bundle (multipart) |

### 19.2 Key Management (`/api/v1/keys/`)

Requires `manage_keys` permission.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/` | Create key (returns raw key once) |
| GET | `/` | List all keys |
| GET | `/{keyId}` | Get key details |
| DELETE | `/{keyId}` | Revoke key |

### 19.3 Health (`/health/`)

No authentication required.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health/live` | Always 200 |
| GET | `/health/ready` | Readiness checks (SQLite, stores, plugins) |
| GET | `/health` | Alias for `/health/ready` |

### 19.4 MCP over HTTP

| Method | Path | Description |
|--------|------|-------------|
| POST | `/mcp?store={store}` | MCP Streamable HTTP (requires auth) |

### 19.5 JSON Source-Gen Contexts

| Context | Location | Purpose |
|---------|----------|---------|
| `StoreJsonContext` | `ScriniaArtifactStore` (private) | CLI index serialization |
| `FileStoreJsonContext` | `FileMemoryStore` (private) | Server index serialization |
| `BundleJsonContext` | `Scrinia.Mcp` (public) | Export/import bundles |
| `ServerJsonContext` | `Scrinia.Server.Models` | All HTTP API DTOs |
| `HttpStoreJsonContext` | `HttpMemoryStore` (private) | Remote store proxying |
All contexts use `CamelCase` naming and `WhenWritingNull` ignore condition for trim/AOT-safe serialization.

---

## Critical Pitfalls

1. **Encoding namespace ambiguity**: `Scrinia.Core.Encoding` shadows `System.Text.Encoding`. Always use `System.Text.Encoding.UTF8.GetBytes()`.
2. **ConsoleAppFramework v5**: Source-gen CLI. `[Argument]` needs `using ConsoleAppFramework;`. XML doc aliases must NOT duplicate auto-generated option names.
3. **MCP tools class**: Must be `sealed class` (non-static), no constructor, no DI. `WithTools<T>()` requirement.
4. **Test isolation**: `TestHelpers.StoreScope` redirects workspace, store dir, ephemeral store, and `SessionBudget` via AsyncLocal overrides.
5. **Trimming**: `PublishTrimmed=true` requires source-gen JSON contexts. Without source-gen, trimmed binary silently fails.
6. **InternalsVisibleTo**: Core uses assembly name `scri` (not `Scrinia`) for the CLI project.
7. **Plugin hooks vs MCP**: Hooks fire only for HTTP API calls, not MCP tool calls.
