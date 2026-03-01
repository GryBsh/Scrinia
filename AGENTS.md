# AGENTS.md — scrinia LLM Memory (Named Memory Protocol)

Licensed under BSD-3-Clause. Copyright (c) 2026 Nick Daniels.

This file is for AI coding agents (Claude Code, Cursor, Copilot, etc.). It describes what scrinia is, how the codebase is structured, what patterns to follow, and common pitfalls to avoid.

**If scrinia is available as an MCP server in your session, call `guide()` once at the start of your session and follow its guidance.** The guide covers ephemeral memories, topic organization, agent-directed chunking strategies, keywords, review conditions, budget tracking, and session-end reflection. Use scrinia's memory tools proactively to persist knowledge as you work — it's what they're built for.

## What scrinia Does

scrinia gives LLMs persistent, portable memory. It compresses text into NMP/2 (Named Memory Protocol v2) artifacts (Brotli + URL-safe Base64), stores them as named memories in a local `.scrinia/` directory, and exposes everything via MCP so agents can remember findings, search past knowledge, and share context across projects.

## Project Layout

```
E:/source/repos/Scrinia/
  src/
    Scrinia.Core/                 <- shared class library (net10.0, BSD-3-Clause)
      Encoding/
        IEncodingStrategy.cs      <- core abstractions (EncodingOptions, EncodingResult, ArtifactMetadata)
        Nmp2Strategy.cs           <- NMP/2 encoder/decoder (always Brotli + URL-safe Base64)
        Nmp2ChunkedEncoder.cs     <- multi-chunk support (agent-directed chunking only, append-as-chunk)
      Models/
        ArtifactEntry.cs          <- v3 index entry record (keywords, TF, review, ChunkEntries)
        ChunkEntry.cs             <- per-chunk index data (keywords, TF, preview)
        EphemeralEntry.cs         <- in-memory entry record
        ScopedArtifact.cs         <- (scope, entry) pair for search/list
        IndexFile.cs              <- v3 index.json schema
      Search/
        IMemorySearcher.cs        <- WeightedFieldScorer + BM25 hybrid search + result types
        TextAnalysis.cs           <- tokenizer, term frequency, keyword extraction/merge
        Bm25Scorer.cs             <- BM25 scoring (k1=1.5, b=0.75, IDF formula)
      IMemoryStore.cs             <- interface for local/remote store dispatch
      IStorageBackend.cs          <- factory interface for creating IMemoryStore instances
      FilesystemBackend.cs        <- default IStorageBackend (creates FileMemoryStore)
      FileMemoryStore.cs          <- instance-based IMemoryStore impl (filesystem-backed)
      MemoryNaming.cs             <- static naming utils (StripEphemeralPrefix, FormatScopeLabel)
      MemoryStoreContext.cs       <- AsyncLocal indirection: MCP tools read Current to dispatch
      SessionBudget.cs            <- per-session token consumption tracking (AsyncLocal)
    Scrinia.Mcp/                  <- shared MCP tools library (net10.0 classlib, refs Core)
      ScriniaMcpTools.cs          <- 17 MCP tools (sealed class, no constructor, no DI injection)
    Scrinia/                      <- CLI + MCP server (net10.0 exe, AssemblyName: scri)
      Program.cs                  <- entry point (6 lines, ConsoleAppFramework v5)
      Commands/
        ScriniaCommands.cs        <- all 9 CLI commands as public methods (ConsoleAppFramework source-gen)
        WorkspaceSetup.cs         <- shared --workspace-root configuration helper
      Mcp/
        ScriniaArtifactStore.cs   <- memory store (local, topic, ephemeral scopes; static class)
    Scrinia.Server/               <- HTTP API server (net10.0 web, refs Core + Mcp)
      Program.cs                  <- entry point (ASP.NET Core minimal API)
      Auth/
        ApiKeyStore.cs            <- SQLite-backed API key store (SHA-256 hashed)
        ApiKeyAuthHandler.cs      <- Bearer token auth handler
        WorkspaceContext.cs       <- per-request context (user, stores, permissions)
      Endpoints/
        MemoryEndpoints.cs        <- REST routes for memory CRUD, search, export/import
        KeyEndpoints.cs           <- API key management routes
        HealthEndpoints.cs        <- Kubernetes-style health probes (/health, /health/live, /health/ready)
      Services/
        StoreManager.cs           <- multi-store factory + cache (name → IMemoryStore via IStorageBackend)
        MemoryOrchestrator.cs     <- business logic for memory operations
        BundleService.cs          <- export/import bundle streaming
      Models/
        ApiDtos.cs                <- request/response records (source-gen JSON)
        ServerJsonContext.cs      <- System.Text.Json source-gen context
      Middleware/
        RequestTimingMiddleware.cs <- structured request timing logs (method, path, status, elapsed ms)
      Dockerfile                  <- multi-stage Docker build (includes Node.js web UI build)
    Scrinia.Plugin.Abstractions/  <- plugin interface library (net10.0 classlib)
      IScriniaPlugin.cs           <- plugin lifecycle interface
      ScriniaPluginBase.cs        <- convenience base class
      IMemoryOperationHook.cs     <- before/after hooks
      HookContexts.cs             <- 6 context classes
    Scrinia.Plugin.Embeddings/    <- semantic search via vector embeddings plugin (net10.0 classlib)
      EmbeddingsPlugin.cs         <- server: IScriniaPlugin + ISearchScoreContributor + IMemoryEventSink + IMemoryOperationHook
      EmbeddingOptions.cs         <- Config POCO (Provider, Hardware, SemanticWeight)
      IEmbeddingProvider.cs       <- Provider abstraction
      EmbeddingProviderFactory.cs <- Factory (onnx/ollama/openai/none)
      VectorStore.cs              <- Per-scope binary vector storage (SVF1 format)
      VectorIndex.cs              <- SIMD cosine similarity + flat-scan search
      Models/VectorEntry.cs       <- (Name, ChunkIndex?, Vector) record
      Onnx/                       <- ModelManager, HardwareDetector, BertTokenizer, OnnxInferenceSession
      Providers/                  <- OnnxEmbeddingProvider, OllamaEmbeddingProvider, OpenAiEmbeddingProvider
    Scrinia.Plugin.Embeddings.Cli/ <- child-process CLI plugin exe (stdin/stdout JSON protocol)
      Program.cs                  <- Protocol loop, dispatches to VectorStore/EmbeddingProvider
      Protocol.cs                 <- Request/response DTOs + source-gen JSON context
    Scrinia.AppHost/              <- .NET Aspire AppHost (orchestrates Scrinia.Server)
      Program.cs                  <- Aspire entry point
  tests/
    Scrinia.Tests/                <- xunit + FluentAssertions, 310 tests
      TestHelpers.cs              <- StoreScope (test isolation), embedded resource helpers
      TestData/                   <- 6 embedded resource corpora
    Scrinia.Server.Tests/         <- xunit + FluentAssertions + WebApplicationFactory, 53 tests
      ScriniaServerFactory.cs     <- test factory (temp data dir, test API keys)
    Scrinia.Plugin.Embeddings.Tests/ <- xunit + FluentAssertions, 28 tests (4 skipped without ONNX model)
      VectorIndexTests.cs, VectorStoreTests.cs, HybridScorerTests.cs, BertTokenizerTests.cs, OnnxEmbeddingProviderTests.cs, EmbeddingsPluginCliTests.cs
  web/                            <- React + Vite + Tailwind CSS SPA
    src/api/                      <- typed API client and TypeScript DTOs
    src/pages/                    <- Login, Dashboard, MemoryBrowser, MemoryDetail, KeyManagement
    src/components/               <- Layout, MemoryList, MemoryContent, ChunkViewer, SearchBar
    vite.config.ts                <- build to src/Scrinia.Server/wwwroot, dev proxy to :5000
  LICENSE                         <- BSD-3-Clause
  NMP_SPEC.md                    <- NMP/2 format specification
  AGENTS.md                      <- this file
  docker-compose.yml             <- one-command server deployment
  docs/
    ARCHITECTURE.md               <- comprehensive architecture document
  .github/workflows/ci.yml       <- CI build + test on push/PR
  .github/workflows/release.yml  <- release builds (CLI, server, Docker image)
```

## Core Abstractions

All encoding, model, and search types live in `Scrinia.Core` (namespaces: `Scrinia.Core.Encoding`, `Scrinia.Core.Models`, `Scrinia.Core.Search`). The CLI/MCP project (`Scrinia`) and tests reference Core via `<ProjectReference>`.

### `IEncodingStrategy`

Only one implementation exists: `Nmp2Strategy`. Namespace: `Scrinia.Core.Encoding`.

```csharp
public interface IEncodingStrategy
{
    string StrategyId { get; }           // "nmp/2"
    string Description { get; }
    EncodingResult Encode(ReadOnlySpan<byte> input, EncodingOptions options);
    byte[] Decode(string artifact);
    bool CanDecode(string artifact);
    ArtifactMetadata ParseHeader(string artifact);
}
```

Key records:
```csharp
record EncodingOptions(int CharsPerLine = 76);  // Base64 chars per line

record EncodingResult(string Artifact, long OriginalBytes, long ArtifactChars,
                      int EstimatedTokens, double BitsPerToken, string StrategyId);

record ArtifactMetadata(string StrategyId, int OriginalBytes, uint? Crc32);
```

### `Nmp2ChunkedEncoder`

The entry point for encoding text. No auto-chunking — single-element content always produces a single chunk. Multi-chunk only via explicit multiple elements or AppendChunk.

```csharp
// Single-chunk: always produces one chunk regardless of size
string artifact = Nmp2ChunkedEncoder.Encode(text);

// Agent-directed chunking: each element → one independently decodable chunk
// 1 element → single-chunk format, 2+ → multi-chunk format
string artifact = Nmp2ChunkedEncoder.EncodeChunks(["## Auth\n...", "## Users\n..."]);

// Append a new chunk without re-encoding existing chunks (surgical append)
// Single-chunk → promotes to multi-chunk; multi-chunk → appends new section
string updated = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, "new chunk text");

// Chunk access
int count = Nmp2ChunkedEncoder.GetChunkCount(artifact);
string chunkText = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkIndex); // 1-based
```

Key internals:
- `EncodeMultiChunkFromParts(string[])` — shared multi-chunk encoding (CRC32 over concatenated bytes)
- `AppendCompressedChunk(sb, bytes, number)` — appends one `##CHUNK:N` section to a StringBuilder
- `ExtractRawChunkSections(artifact, count)` — extracts existing chunk sections verbatim for surgical append

### `ScriniaArtifactStore`

Static class in `Scrinia.Mcp`. Data records (`ArtifactEntry`, `ChunkEntry`, `EphemeralEntry`, `ScopedArtifact`, `IndexFile`) live in `Scrinia.Core.Models`.

Manages three scopes of memory:

| Pattern | Scope | Storage |
|---|---|---|
| `subject` | local | `<workspace>/.scrinia/store/subject.nmp2` |
| `topic:subject` | local-topic | `<workspace>/.scrinia/topics/topic/subject.nmp2` |
| `~subject` | ephemeral | In-memory `ConcurrentDictionary` (dies with process) |

Key methods:
- `Configure(workspaceRoot)` — must be called before any store operations
- `ParseQualifiedName(name)` — returns `(scope, subject)` tuple
- `ResolveArtifactAsync(nameOrArtifact)` — inline NMP/2 -> file:// -> ~ephemeral -> qualified name
- `ListScoped(scopes?)` — returns all entries across requested scopes
- `SearchAll(query, scopes?, limit)` — polymorphic results (entries + topics)
- `ArchiveVersion(subject, scope)` — copies .nmp2 to `versions/` subdirectory before overwrite

Index file v3 fields (`Scrinia.Core.Models.ArtifactEntry`):
```csharp
record ArtifactEntry(
    string Name, string Uri, long OriginalBytes, int ChunkCount,
    DateTimeOffset CreatedAt, string Description,
    string[]? Tags = null, string? ContentPreview = null,
    string[]? Keywords = null,                      // agent + auto-extracted
    Dictionary<string, int>? TermFrequencies = null, // BM25 TF data
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? ReviewAfter = null,             // date-based staleness
    string? ReviewWhen = null,                      // condition-based staleness
    ChunkEntry[]? ChunkEntries = null);             // per-chunk indexing
```

Ephemeral entries mirror Keywords, TermFrequencies, and UpdatedAt (no review fields).

### `ScriniaMcpTools`

17 MCP tools exposed via `[McpServerTool(Name = "snake_case")]`:

| MCP name | Method | Description |
|---|---|---|
| `guide` | Guide() | Session playbook: ephemeral, topics, chunking, keywords, review, budget |
| `encode` | Encode() | Compress text into NMP/2 artifact; 1 element = single-chunk, N = agent-directed |
| `chunk_count` | ChunkCount() | Count chunks in an artifact |
| `get_chunk` | GetChunk() | Decode one chunk (1-based); records budget |
| `show` | Show() | Unpack artifact to original text; records budget |
| `store` | Store() | Compress + persist; content[] for agent-directed chunking, keywords, review |
| `list` | List() | Formatted table with ~tokens column and review markers |
| `search` | Search() | BM25 + weighted field hybrid search with ~tokens; returns entry/chunk/topic results |
| `copy` | Copy() | Copy between scopes (ephemeral promotion supported) |
| `forget` | Forget() | Delete memory |
| `export` | Export() | Topic -> .scrinia-bundle |
| `import` | Import() | .scrinia-bundle -> topic |
| `append` | Append() | Append content as a new independently retrievable chunk |
| `reflect` | Reflect() | Session-end knowledge persistence checklist |
| `ingest` | Ingest() | Full knowledge capture — 5-phase protocol for thorough memory ingestion |
| `budget` | Budget() | Per-memory token consumption breakdown |
| `kt` | Kt() | Knowledge transfer — briefing of all persistent memories |

### Search: BM25 + Weighted Field + Semantic Scoring

All search types in `Scrinia.Core.Search`. Hybrid scoring: `finalScore = weightedFieldScore + bm25Score * 5.0 + supplementalScore`

Supplemental scores come from `ISearchScoreContributor` plugins (e.g., embeddings). When no contributor is registered, `supplementalScore = 0` (legacy behavior). Score keys: `"{scope}|{name}"` for entries, `"{scope}|{name}|{chunkIndex}"` for chunks.

- `TextAnalysis`: tokenizer (stop words, >= 2 chars), TF computation, keyword extraction (top 25), keyword merge (agent-first, cap 30)
- `Bm25Scorer`: k1=1.5, b=0.75, IDF formula, corpus stats computed per search
- Keywords: agent-provided + auto-extracted, merged (agent first), boosted in TF (+3)
- Entries without TF data (v2 index) get bm25Score=0 (graceful degradation)

Entry scoring (per term, max wins):

| Match type | Score |
|---|---|
| Exact name match | 100 |
| Tag exact match | 50 |
| Keyword exact match | 40 |
| Name starts with | 30 |
| Name contains | 20 |
| Tag contains | 15 |
| Keyword contains | 12 |
| Description contains | 10 |
| Content preview contains | 5 |

### `FileMemoryStore`

Instance-based `IMemoryStore` implementation in `Scrinia.Core`. Takes `workspaceRoot` in constructor. Used by the server (`StoreManager` creates one per named store) and tests. The CLI still uses the static `ScriniaArtifactStore`.

Key safety features:
- **Path traversal protection**: `SanitizeName` strips `..`, `/`, `\` sequences and applies `Path.GetFileName()` as final safety net.
- **Index file locking**: `ConcurrentDictionary<string, SemaphoreSlim>` provides per-scope mutual exclusion for `LoadIndex`/`SaveIndex`/`Upsert`/`Remove`. Internal `LoadIndexUnsafe`/`SaveIndexUnsafe` methods exist for lock-already-held paths.

### `MemoryStoreContext`

AsyncLocal indirection in `Scrinia.Core`. MCP tools read `MemoryStoreContext.Current` to dispatch to the active `IMemoryStore`. Set per-request by the server middleware and per-session by the CLI.

### `SessionBudget`

In `Scrinia.Core`. Tracks chars loaded via `show()` and `get_chunk()` per session.

- `RecordAccess(memoryName, charsLoaded)` — accumulates across multiple accesses
- `TotalCharsLoaded` / `EstimatedTokensLoaded` — session totals
- `Breakdown` — per-memory `(Chars, EstTokens)` dictionary
- Uses `AsyncLocal` override for test isolation

## NMP/2 Format (Named Memory Protocol v2)

See [NMP_SPEC.md](NMP_SPEC.md) for the complete specification. Key points:

- Always Brotli-compressed, URL-safe Base64 encoded
- Header: `NMP/2 {N}B CRC32:{hex} BR+B64 [C:{k}]`
- Single-chunk: one `Encode()` call, any size
- Multi-chunk: `##CHUNK:1` sections via `EncodeChunks(string[])` or `AppendChunk()`, independently decodable
- CRC32 over original bytes, `##PAD:0-2`, `NMP/END` sentinel

## Bundle Format (.scrinia-bundle)

Standard zip containing:
- `manifest.json` — `{ version: 1, exported, topics[], totalEntries }`
- `topics/{name}/index.json` — entry metadata
- `topics/{name}/{subject}.nmp2` — artifact files

Created by `scri export` (from stored memories) or `scri bundle` (from raw files).

## scrinia Guide (MCP tool output)

The `guide()` tool returns this playbook. Agents should call it once per session.

### Ephemeral memories (~name)
Use `~` prefix for in-session working state that shouldn't persist:
- `store(content, "~scratch")` — temporary notes, intermediate results
- `store(content, "~plan")` — current task plan you're iterating on
- Dies when the process exits — no cleanup needed
- Promote to persistent: `copy("~scratch", "notes:my-finding")`

### Topic organization (topic:subject)
Group related memories by topic for easy discovery:
- `store(content, "api:auth-flow")` — API topic, auth-flow entry
- `store(content, "arch:decisions")` — architecture decisions
- `list(scopes="api")` — list only the api topic
- `search("auth", scopes="api")` — search within a topic
Topics appear automatically when you use the colon syntax.

### Keywords and search
Memories are automatically indexed with content keywords for BM25 search.
You can also provide explicit keywords for better discoverability:
- `store(content, "api:auth", keywords=["oauth", "jwt", "bearer"])`
- Agent keywords are prioritized over auto-extracted ones
- Search finds content even when names/descriptions don't match the query

### Agent-directed chunking
No auto-chunking — single-element content always produces a single chunk, regardless of size.
Multi-chunk only via explicit multiple elements or `append()`.

Use chunked access to stay within context limits:
1. `chunk_count("api:large-doc")` — how many chunks?
2. `get_chunk("api:large-doc", 1)` — read just the first chunk
3. Process chunk-by-chunk instead of loading everything at once

#### Maximize context with pre-chunked storage
You control how content is split — organize by semantic boundaries:
- `store(["## Auth\n...", "## Users\n...", "## Billing\n..."], "api:endpoints")`
- Each element becomes one independently retrievable chunk
- Each chunk is individually indexed (keywords, TF, preview) for chunk-level search
- `search("oauth")` returns `chunk` results pointing to specific chunks — call `get_chunk(N)` directly

#### Strategies for effective chunking
- **One concept per chunk**: split by function, endpoint, topic, or section header
- **Self-contained chunks**: each chunk should make sense on its own without context from other chunks
- **Use budget() to learn**: check which memories consume the most tokens, then re-store with finer-grained chunks
- **Journal pattern**: use `append(entry, "log")` to build a log where each entry is independently addressable — read only recent entries instead of the whole history
- **Chunk size sweet spot**: aim for 2K-8K chars per chunk — small enough to be selective, large enough to carry meaningful context

### Incremental capture with append
Build up memories incrementally without recomposing the full document:
- `append("New finding here", "session-notes")` — always adds as a new independently retrievable chunk
- Creates the memory if it doesn't exist yet
- Each appended chunk gets its own index entry (keywords, TF, preview) for chunk-level search
- Great for session journals, running logs, and incremental notes

### Context compression
When you gather large amounts of information during research:
1. Summarize your findings into a concise document
2. `store(summary, "topic:finding-name")` — persist for future sessions
3. Later: `search("finding")` -> `show("topic:finding-name")` to recall
This lets you carry knowledge across sessions without re-researching.

### Version history
When you overwrite an existing memory, the previous version is archived:
- Stored in `versions/` subdirectory with timestamp suffix
- No manual action needed — happens automatically on store/append

### Review conditions
Flag memories that may become stale:
- `store(content, "api:endpoints", reviewAfter="2026-06-01")` — date-based
- `store(content, "auth:flow", reviewWhen="when auth system changes")` — condition-based
- `list()` shows `[stale]` or `[review?]` markers

### Budget tracking
Monitor how much context you're consuming:
- `budget()` — shows per-memory chars/tokens loaded via show()/get_chunk()
- Helps decide when to use chunked retrieval vs. full show()

### Session-end reflection
Call `reflect()` at the end of a session for a checklist of knowledge to persist.

### Cross-project sharing
Export topics as portable .scrinia-bundle files:
1. `export(["api", "arch"])` — creates a .scrinia-bundle in .scrinia/exports/
2. Copy the bundle to another project
3. `import("path/to/bundle.scrinia-bundle")` — restores all topics
Useful for sharing team conventions, API patterns, or onboarding knowledge.

### When to store vs. not store
**Store:** stable patterns, architectural decisions, API conventions,
solutions to recurring problems, project-specific knowledge.
**Don't store:** session-specific state (use ~ephemeral instead).

## Known Pitfalls

### ConsoleAppFramework v5 source-gen CLI

XML doc aliases (e.g. `-d,`) must NOT duplicate the auto-generated option name from the parameter (e.g. don't put `--workspace-root,` in doc for `workspaceRoot` param). `[Argument]` needs `using ConsoleAppFramework;`. `app.Add<T>()` (no args) registers methods as root subcommands.

### Non-static MCP tools class

`WithTools<T>()` requires a non-static type:
```csharp
[McpServerToolType]
public sealed class ScriniaMcpTools { ... }  // correct — no constructor, no DI
```

### System.Text.Encoding.UTF8 ambiguity

The `Scrinia.Core.Encoding` namespace shadows `System.Text.Encoding`. Always fully qualify:
```csharp
System.Text.Encoding.UTF8.GetBytes(text)  // correct
Encoding.UTF8.GetBytes(text)              // ambiguous — resolves to Scrinia.Core.Encoding
```

### ScriniaMcpTools internals

`ScriniaMcpTools` lives in `Scrinia.Mcp` (shared library). Sealed with no constructor (no DI injection). CLI commands instantiate it directly: `new ScriniaMcpTools()`. `FormatBytes` and `BundleJsonOptions` are `public static`. `BundleIndex`, `BundleManifest`, and `BundleJsonContext` are `public`.

### Test isolation

Tests use `TestHelpers.StoreScope` to isolate store operations:
```csharp
using var scope = new TestHelpers.StoreScope();
// scope.WorkspaceDir = temp workspace root
// scope.TempDir = temp .scrinia/store/ directory
// Ephemeral store + SessionBudget also isolated via AsyncLocal overrides
```

### Workspace root discovery

`WorkspaceSetup.Configure(null)` walks up the directory tree from CWD looking for `.scrinia/` (like git finds `.git/`). If found, its parent becomes the workspace root. If not found, falls back to CWD. This makes `scri serve` work regardless of which directory the MCP client launches from.

### Index serialization

`_jsonOptions` uses `DefaultIgnoreCondition = WhenWritingNull` to keep index files lean. v2 indexes load cleanly (null fields).

## Running Tests

```bash
# CLI + MCP tests (310 tests)
cd E:\source\repos\Scrinia\tests\Scrinia.Tests
dotnet test

# Server API tests (53 tests)
cd E:\source\repos\Scrinia\tests\Scrinia.Server.Tests
dotnet test

# Embeddings plugin tests (28 tests, 4 skipped without ONNX model)
cd E:\source\repos\Scrinia\tests\Scrinia.Plugin.Embeddings.Tests
dotnet test
```

Expected: 391 tests total (310 + 53 + 28), 4 skipped (ONNX model download required).

Test corpora (6 embedded resources): `TestHelpers.AllTestDataFiles()` returns all as `(name, content)` pairs. Individual loaders: `LoadFactsText()`, `LoadHumanEvalText()`, `LoadGsm8kText()`, `LoadInfiniteBenchText()`, `LoadMmluText()`, `LoadQualityArticleText()`.

## HTTP API Server

The server (`Scrinia.Server`) provides REST API access to scrinia's memory stores. It uses ASP.NET Core minimal APIs with SQLite-backed API key authentication.

### Architecture

- **Multi-store**: Multiple named stores, each backed by an `IMemoryStore` created by the configured `IStorageBackend`. Stores are configured via `Scrinia:Stores:{name}` settings. Default backend: `FilesystemBackend` (creates `FileMemoryStore` instances). Plugins can replace the backend via `IServiceCollection`.
- **API key auth**: Bearer token authentication. Keys are SHA-256 hashed in SQLite (`HashKeyBytes` internal for timing-safe comparison). Each key is scoped to specific stores and granular permissions. Bootstrap key written to `BOOTSTRAP_KEY` file on first run (never logged).
- **Granular permissions**: Permission types enforced per-endpoint: `read`, `search`, `store`, `append`, `forget`, `copy`, `export`, `import`, `manage_keys`.
- **Per-request context**: `RequestContext` resolves the authenticated user, their accessible stores, permissions, store access levels, and the active store from the route.
- **Health probes**: `/health/live` (liveness), `/health/ready` (readiness with SQLite + store checks), `/health` (alias for ready). All unauthenticated.

### Running

```bash
# Development (with hot-reload web UI)
cd web && npm run dev &     # Vite dev server on :5173
dotnet run --project src/Scrinia.Server  # API on :5000

# Production
cd web && npm run build     # builds to src/Scrinia.Server/wwwroot/
dotnet run --project src/Scrinia.Server  # serves UI + API on :5000

# Docker
docker compose up -d
docker compose logs   # shows bootstrap API key
```

### Server Infrastructure

- **Rate limiting**: Sliding window (100 req/min, 6 segments). Applied to `/api/v1/` and `/api/v1/keys` groups. NOT applied to `/health/*` or `/mcp`. Returns 429 on overflow.
- **CORS**: Configurable origins from `Scrinia:CorsOrigins` array. Empty array = allow all origins.
- **OpenAPI**: Spec at `/openapi/v1.json`, interactive explorer at `/scalar/v1` (Scalar).
- **Request timing**: Structured log per request (method, path, status code, elapsed ms).
- **Global exception handler**: `UseExceptionHandler` — sanitizes unhandled exceptions to `{ "error": "An internal error occurred." }` (500). Placed first in pipeline.
- **HTTPS/HSTS**: `UseHsts()` + `UseHttpsRedirection()` — enforced in production only (skipped in Development).
- **Security headers**: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-XSS-Protection: 0`.
- **Request size limits**: Kestrel `MaxRequestBodySize = 10 MB`, `FormOptions.MultipartBodyLengthLimit = 50 MB` (for bundle imports).
- **Input validation**: Memory name max 256 chars, content element max 5 MB. Enforced in `StoreMemory` and `AppendMemory` endpoints.
- **Graceful shutdown**: `app.RunAsync()` with `ApplicationStopping` hook to dispose `ApiKeyStore` (SQLite connection).
- **Middleware order**: `UseExceptionHandler` → HSTS/HTTPS (prod) → security headers → `UseDefaultFiles` + `UseStaticFiles` → `RequestTimingMiddleware` → `UseCors` → `UseRateLimiter` → `UseAuthentication` → `UseAuthorization` → custom `RequestContext` → plugin middleware → endpoints → `MapFallbackToFile("index.html")`.

### Plugin System

The server supports drop-in plugins via `Scrinia.Plugin.Abstractions`. Plugin DLLs placed in `{dataDir}/plugins/` are auto-loaded at startup.

**Plugin interface** (`Scrinia.Plugin.Abstractions.IScriniaPlugin`):
- `Name`, `Version`, `Order` (lower runs first, default 0)
- `ConfigureServices(IServiceCollection, IConfiguration)` — register DI services
- `ConfigureMiddleware(IApplicationBuilder)` — add middleware (runs after auth, before endpoints)
- `MapEndpoints(IEndpointRouteBuilder)` — add HTTP routes

**Storage backend extensibility** (`Scrinia.Core.IStorageBackend`):
- `BackendId` — identifier for diagnostics (e.g. "filesystem", "postgresql")
- `CreateStore(storeName, storePath)` — factory method returning `IMemoryStore`
- Default: `FilesystemBackend` creates `FileMemoryStore` instances with `Directory.CreateDirectory`
- Plugins override by registering a replacement `IStorageBackend` singleton in `ConfigureServices`
- `StoreManager` resolves the backend from DI at construction time (deferred via factory delegate)
- `MemoryNaming` (static, `Scrinia.Core`) provides `StripEphemeralPrefix` and `FormatScopeLabel` without store dependency

**Convenience base class**: `ScriniaPluginBase` — abstract class with virtual empty defaults for all methods.

**Memory operation hooks** (`IMemoryOperationHook`):
- `OnBeforeStoreAsync` / `OnAfterStoreAsync` — intercept store operations
- `OnBeforeAppendAsync` / `OnAfterAppendAsync` — intercept append operations
- `OnBeforeForgetAsync` / `OnAfterForgetAsync` — intercept forget operations
- Before-hooks can set `Cancel = true` + `CancelReason` to abort (returns 409 Conflict)
- All methods have default `Task.CompletedTask` implementations

**Hook context classes** (all sealed, `required init` props):
- `BeforeStoreContext`: Store, Name, Content[], Description, Tags, Keywords, Cancel, CancelReason
- `AfterStoreContext`: Store, Name, QualifiedName, ChunkCount, OriginalBytes, **Content[]**
- `BeforeAppendContext`: Store, Name, Content, Cancel, CancelReason
- `AfterAppendContext`: Store, Name, ChunkCount, OriginalBytes, **Content**
- `BeforeForgetContext`: Store, Name, Cancel, CancelReason
- `AfterForgetContext`: Store, Name, WasDeleted

**PluginLoader** (`Scrinia.Server/Services/PluginLoader.cs`):
- Scans `{pluginsDir}/*.dll`, creates isolated `AssemblyLoadContext` per DLL
- Custom ALC falls back to Default for shared assemblies (Scrinia.*, Microsoft.*, System.*)
- Instantiates `IScriniaPlugin` implementations via parameterless constructor
- Sorted by `Order`; logs each loaded plugin; logs+skips failures

**PluginPipeline** (`Scrinia.Server/Services/PluginPipeline.cs`):
- Singleton wrapping `MemoryOrchestrator` with before/after hook invocation
- Methods: `StoreAsync`, `AppendAsync`, `ForgetAsync`
- Pattern: before hooks → MemoryOrchestrator → after hooks
- If `Cancel=true` → throws `OperationCanceledException` (endpoints catch → 409)
- Zero overhead when no hooks registered (`_hooks.Length == 0` short-circuit)

**Two code paths, no double-firing**: REST uses `IMemoryOperationHook` (via `PluginPipeline`). MCP uses `IMemoryEventSink` (via `MemoryEventSinkContext`). A plugin implements both interfaces but only one fires per code path. Both paths share `ISearchScoreContributor` for search.

**Core extensibility interfaces** (in `Scrinia.Core`, AsyncLocal-based):
- `ISearchScoreContributor` + `SearchContributorContext` — supplemental search scores (both REST + MCP)
- `IMemoryEventSink` + `MemoryEventSinkContext` — store/append/forget event notifications (MCP path only)

**CLI plugin loading** (`Scrinia/Services/PluginProcessHost.cs`):
- Discovers `{exeDir}/plugins/scri-plugin-*` executables (child-process architecture)
- Communicates via newline-delimited JSON on stdin/stdout (stderr forwarded for logging)
- `PluginProcessHost` implements both `ISearchScoreContributor` and `IMemoryEventSink`
- Sets `SearchContributorContext.Default` and `MemoryEventSinkContext.Default` on startup
- Auto-restarts crashed plugin up to 3 times, then degrades to BM25-only search
- No `TrimmerRootAssembly` entries needed — plugins run in their own .NET runtime

**Child-process protocol** (JSON, one object per line):
- `status` → provider info, availability, vector count
- `search` (query, scopes[]) → similarity scores keyed by `{scope}|{name}[|{chunkIndex}]`
- `upsert` (scope, name, chunkIndex?, text) → embed text and store vector
- `remove` (scope, name) → remove all vectors for a memory
- `embed` (text) → single embedding vector
- `embed_batch` (texts[]) → batch embeddings
- `shutdown` → graceful exit

**Writing a CLI plugin**:
1. Create a console app project referencing the embeddings/core library
2. Implement the stdin/stdout JSON protocol loop (see `Scrinia.Plugin.Embeddings.Cli/Program.cs`)
3. Publish self-contained (NOT trimmed) to `{exeDir}/plugins/scri-plugin-{name}`
4. For server-side: create a separate `{Name}Plugin : ScriniaPluginBase` class sharing internal types

### MCP over HTTP

MCP Streamable HTTP transport at `/mcp`, powered by `ModelContextProtocol.AspNetCore` 1.0.0.

- **Endpoint**: `POST /mcp?store={store}` — JSON-RPC request/response
- **Auth**: Bearer token (same API key auth as REST endpoints)
- **Store selection**: Query param `?store=default` resolves the `FileMemoryStore` for the session
- **Session context**: `PerSessionExecutionContext = true` ensures `MemoryStoreContext.Current` (AsyncLocal) persists across MCP tool calls within a session
- **Tools**: All 17 tools from `ScriniaMcpTools` (shared via `Scrinia.Mcp` library)

MCP client config (HTTP transport):
```json
{
  "mcpServers": {
    "scrinia": {
      "url": "http://localhost:5000/mcp?store=default",
      "headers": { "Authorization": "Bearer YOUR_API_KEY" }
    }
  }
}
```

### Web UI

React 19 + TypeScript + Vite + Tailwind CSS 4 + React Router 7 + TanStack Query SPA.

- **Build**: `cd web && npm run build` → outputs to `Scrinia.Server/wwwroot/`
- **Dev mode**: `cd web && npm run dev` (Vite on :5173, proxies API to :5000)
- **Pages**: Login (API key entry), Dashboard (health + store overview), Memory Browser (list/search with scope tabs), Memory Detail (content + chunks + delete), Key Management (create/list/revoke)
- **Static serving**: `UseDefaultFiles` + `UseStaticFiles` (no auth), `MapFallbackToFile("index.html")` for SPA routing (must be last route)
- **MSBuild integration**: `BuildWebUI` target runs `npm ci && npm run build` before dotnet build if `wwwroot/index.html` doesn't exist

### Configuration

| Setting | Env var | Default |
|---|---|---|
| `Scrinia:DataDir` | `Scrinia__DataDir` | `{LocalAppData}/scrinia-server` |
| `Scrinia:Stores:{name}` | `Scrinia__Stores__{name}` | `{DataDir}/stores/{name}` |
| `Scrinia:CorsOrigins` | `Scrinia__CorsOrigins__0` etc. | `[]` (allows all origins) |

### Key endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health`, `/health/live`, `/health/ready` | None | Health probes |
| GET | `/openapi/v1.json` | None | OpenAPI specification |
| GET | `/scalar/v1` | None | Interactive API explorer (Scalar) |
| POST | `/mcp?store={store}` | Bearer | MCP Streamable HTTP endpoint |
| POST | `/api/v1/stores/{store}/memories` | Bearer | Store a memory |
| GET | `/api/v1/stores/{store}/memories` | Bearer | List memories |
| GET | `/api/v1/stores/{store}/memories/{name}` | Bearer | Show memory |
| DELETE | `/api/v1/stores/{store}/memories/{name}` | Bearer | Delete memory |
| POST | `/api/v1/stores/{store}/memories/{name}/append` | Bearer | Append chunk |
| POST | `/api/v1/stores/{store}/memories/{name}/copy` | Bearer | Copy memory |
| GET | `/api/v1/stores/{store}/memories/{name}/chunks/{i}` | Bearer | Get chunk |
| GET | `/api/v1/stores/{store}/search?q=...` | Bearer | Search memories |
| POST | `/api/v1/stores/{store}/export` | Bearer | Export topics |
| POST | `/api/v1/stores/{store}/import` | Bearer | Import bundle |
| POST | `/api/v1/keys` | Bearer (manage_keys) | Create API key |
| GET | `/api/v1/keys` | Bearer (manage_keys) | List keys |
| DELETE | `/api/v1/keys/{id}` | Bearer (manage_keys) | Revoke key |
