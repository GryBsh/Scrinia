# scrinia — Persistent Memory for LLMs

[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)

A CLI tool and MCP server that gives LLMs persistent, portable memory. Store knowledge as compressed NMP/2 (Named Memory Protocol) artifacts, organize by topic, search with BM25 + semantic hybrid scoring, track context budgets, and share via bundles. Built for Claude Code, Copilot, Cursor, Codex, and any MCP-compatible agent.

## What it does

Scrinia compresses text into dense NMP/2 (Named Memory Protocol v2) artifacts (Brotli + URL-safe Base64) and manages them as named memories in a local `.scrinia/` store. LLMs call scrinia via MCP to remember findings, retrieve past knowledge, and share context across projects — with zero infrastructure, no API keys, and no cloud dependencies. Optional embeddings plugin adds semantic vector search (ONNX/DirectML) alongside BM25 for hybrid scoring.

## Compression benchmarks

NMP/2 encoding via the chunked encoder (the production path) across representative benchmark datasets:

| File | Original | Artifact | Ratio | Chunks | Bits/Token |
|---|---|---|---|---|---|
| facts.txt (prose corpus) | 1019.6 KB | 609.4 KB | 0.598x | 132 | 53.54 |
| humaneval_sample.txt (code) | 10.8 KB | 4.8 KB | 0.448x | 1 | 71.48 |
| gsm8k_sample.txt (math) | 12.9 KB | 6.7 KB | 0.517x | 1 | 61.98 |
| infinitebench_qa.txt (QA) | 10.0 KB | 5.6 KB | 0.559x | 1 | 57.20 |
| mmlu_sample.txt (exam) | 7.8 KB | 4.8 KB | 0.615x | 1 | 52.04 |
| quality_article.txt (article) | 11.9 KB | 6.0 KB | 0.509x | 1 | 62.85 |
| **Total** | **1.0 MB** | **637.3 KB** | **0.594x** | | **53.87** |

Large documents are automatically chunked (~8 KB target) for independent retrieval — agents read only the chunks they need. Across all content types, NMP/2 achieves 52–72 bits/token, packing roughly 2x more information per token than raw text.

## How scrinia compares

The agent memory landscape includes cloud-hosted services (Mem0, Zep/Graphiti), framework-specific solutions (LangMem), and built-in agent memory (Claude Code's CLAUDE.md, Cursor's .cursorrules). scrinia takes a different approach.

**What makes scrinia unique:**

- **Zero infrastructure** — no API keys, no databases, no cloud services. Just a binary that runs locally.
- **Lossless compression** — every byte survives the round trip. Mem0 and Zep store summaries (lossy). The agent stores raw markdown (no compression). scrinia compresses without losing information.
- **Chunk-addressable retrieval** — large documents split into independently decodable chunks. Read just the part you need instead of loading everything into context.
- **BM25 + semantic hybrid search** — content-aware search with auto-extracted keywords, plus optional ONNX-powered semantic vector search (DirectML/CUDA/CPU) for cosine similarity scoring — no cloud API calls needed.
- **Session lifecycle tooling** — `guide()` teaches agents best practices, `budget()` tracks context consumption, `reflect()` prompts end-of-session knowledge capture. No other memory tool does this.
- **Portable bundles** — export topics as .scrinia-bundle files and import them in other projects or share with teammates.

**How it stacks up:**

| Capability | scrinia | Mem0 | Zep/Graphiti | server-memory | Claude Code CLAUDE.md |
|---|---|---|---|---|---|
| Infrastructure | None | Cloud API | Neo4j + API | None | None |
| Compression | Lossless (Brotli) | None (lossy summaries) | None | None | None |
| Content search | BM25 + field + semantic | Embedding vectors | Knowledge graph | Exact name only | None (flat file) |
| Auto-indexing | Keywords + TF + embeddings | Embeddings | Entity extraction | None | None |
| Chunked retrieval | Yes | No | No | No | No |
| Version history | Auto-archive on overwrite | No | Temporal edges | No | Git only |
| Staleness tracking | ReviewAfter/ReviewWhen | No | Temporal decay | No | No |
| Budget tracking | Per-memory token counts | No | No | No | No |
| Cross-project sharing | .scrinia-bundle export/import | Cloud sync | No | No | No |
| Semantic search | Yes (local ONNX, optional) | Yes (cloud API) | Yes (graph + embeddings) | No | No |

**When to use scrinia:** You want reliable, portable, zero-dependency memory that doesn't lose information. Ideal for developer tools, CI pipelines, air-gapped environments, and any workflow where you need to carry knowledge without cloud services.

**When to use alternatives:** You need complex entity-relationship reasoning over massive knowledge graphs (Graphiti), and don't mind the infrastructure cost.

## Install

**CLI (local MCP server):**

Build from source:

```bash
git clone https://github.com/nickd-scrinia/scrinia
cd scrinia

# Quick build
dotnet build

# Publish trimmed single-file binary
.\publish.ps1 -OutputDir ./dist -Platform win-x64

# With embeddings plugin (adds semantic search)
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

**HTTP API server (Docker):**

```bash
docker compose up -d
docker compose logs   # shows bootstrap API key
```

**Aspire (development):**

```bash
dotnet run --project src/Scrinia.AppHost
```

## Quick start

```bash
# Store a file as a named memory
scri store session-notes ./notes.md

# Store under a topic with keywords and review conditions
scri store api:auth-flow ./auth.md --keywords oauth,jwt --review-when "when auth system changes"

# List all memories (shows ~tokens, staleness markers)
scri list

# Search memories (BM25 + semantic hybrid)
scri search "auth"

# Display memory content
scri show api:auth-flow

# Export a topic for sharing
scri export api

# Bundle raw files into a .scrinia-bundle (no memorizing)
scri bundle docs *.md

# Import a bundle
scri import ./export-20260226.scrinia-bundle

# Delete a memory
scri forget api:auth-flow
```

## CLI commands

| Command | Description |
|---|---|
| `scri serve` | Start the MCP server (stdio transport) |
| `scri list [--scopes]` | List stored memories with ~tokens and review markers |
| `scri search <query> [--scopes] [--limit]` | BM25 + semantic hybrid search |
| `scri store <name> [file] [-d] [-t] [-k] [--review-after] [--review-when]` | Store a file as a named memory |
| `scri show <name> [-o]` | Display memory content |
| `scri forget <name>` | Delete a stored memory |
| `scri export <topics> [-o]` | Export topics to a .scrinia-bundle |
| `scri import <path> [--topics] [--overwrite]` | Import from a .scrinia-bundle |
| `scri bundle <topic> <files> [-o] [-d] [-t]` | Bundle raw files into a .scrinia-bundle |

All commands accept `--workspace-root <PATH>` to override the workspace directory (defaults to cwd).

## Naming convention

| Pattern | Scope | Storage path |
|---|---|---|
| `subject` | Local | `<workspace>/.scrinia/store/subject.nmp2` |
| `topic:subject` | Local topic | `<workspace>/.scrinia/topics/topic/subject.nmp2` |
| `~subject` | Ephemeral | In-memory only (dies with process) |

## MCP server

### Stdio transport (CLI)

Add to Claude Code (`.mcp.json` in your project root or `~/.claude/`):

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve"],
      "transport": "stdio"
    }
  }
}
```

### HTTP transport (server)

When running `Scrinia.Server`, MCP is available over HTTP at `/mcp`:

```json
{
  "mcpServers": {
    "scrinia": {
      "url": "http://localhost:8080/mcp?store=default",
      "headers": {
        "Authorization": "Bearer YOUR_API_KEY"
      }
    }
  }
}
```

The HTTP transport supports the MCP Streamable HTTP specification, including JSON-RPC over POST and server-sent events for notifications.

### MCP tools (17)

| Tool | Description |
|---|---|
| `guide()` | Session playbook — ephemeral, topics, chunking, keywords, review, budget |
| `encode(content)` | Compress text into an NMP/2 artifact |
| `chunk_count(artifactOrName)` | Count independently decodable chunks |
| `get_chunk(artifactOrName, chunkIndex)` | Decode one chunk (1-based) |
| `show(artifactOrName)` | Unpack an NMP/2 artifact to original text |
| `store(content, name, ...)` | Compress and persist with keywords, tags, review conditions |
| `list(scopes?)` | List stored memories with ~tokens and review markers |
| `search(query, scopes?, limit?)` | BM25 + weighted field hybrid search |
| `copy(nameOrUri, destination, overwrite?)` | Copy between scopes (ephemeral promotion) |
| `forget(nameOrUri)` | Delete a stored memory |
| `export(topics[], filename?)` | Export topics to a .scrinia-bundle |
| `import(bundlePath, topics?, overwrite?)` | Import from a .scrinia-bundle |
| `append(content, name)` | Incremental capture — append as new chunk or create |
| `reflect()` | Session-end knowledge persistence checklist |
| `ingest()` | Full knowledge capture — 5-phase protocol for thorough memory ingestion |
| `budget()` | Per-memory token consumption breakdown |
| `kt()` | Knowledge transfer — briefing of all persistent memories |

## Running tests

```bash
# CLI + MCP tests (310 tests)
cd tests/Scrinia.Tests && dotnet test

# Server API tests (53 tests)
cd tests/Scrinia.Server.Tests && dotnet test

# Embeddings plugin tests (28 tests, 4 skipped without ONNX model)
cd tests/Scrinia.Plugin.Embeddings.Tests && dotnet test
```

391 tests across 3 suites (310 + 53 + 28), all passing.

## Project structure

```
src/
  Scrinia.Core/                   Shared class library (net10.0)
    Encoding/                     IEncodingStrategy, Nmp2Strategy, Nmp2ChunkedEncoder
    Models/                       ArtifactEntry, ChunkEntry, EphemeralEntry, ScopedArtifact, IndexFile
    Search/                       WeightedFieldScorer, BM25, TextAnalysis, ISearchScoreContributor
    IMemoryStore.cs               Interface seam for local vs remote store
    IMemoryEventSink.cs           Event notifications (store/append/forget) for plugins
    SessionBudget.cs              Per-session token consumption tracking

  Scrinia.Mcp/                    Shared MCP tools library (net10.0)
    ScriniaMcpTools.cs            17 MCP tools (sealed class, no constructor, no DI)

  Scrinia/                        CLI + MCP server (net10.0, AssemblyName: scri)
    Program.cs                    Entry point (ConsoleAppFramework v5)
    Commands/
      ScriniaCommands.cs          9 CLI commands as public methods
      WorkspaceSetup.cs           Workspace root discovery + plugin loading
    Mcp/
      ScriniaArtifactStore.cs     Memory store (local, topic, ephemeral scopes; v3 index)
    Services/
      PluginProcessHost.cs        Child-process plugin manager (stdin/stdout JSON)

  Scrinia.Plugin.Abstractions/    Plugin SDK (net10.0, IScriniaPlugin, IMemoryOperationHook)

  Scrinia.Plugin.Embeddings/      Semantic vector search plugin (net10.0 classlib)
    VectorStore.cs                SVF1 binary format vector storage
    VectorIndex.cs                SIMD-accelerated cosine similarity search
    IEmbeddingProvider.cs         Provider interface (ONNX, Ollama, OpenAI, Null)
    Onnx/                         all-MiniLM-L6-v2 (384-dim), BertTokenizer, DirectML/CUDA/CPU

  Scrinia.Plugin.Embeddings.Cli/  Child-process CLI plugin (net10.0 exe)
    Program.cs                    stdin/stdout JSON protocol loop for CLI embeddings

  Scrinia.Server/                 HTTP API server (net10.0 web)
    Auth/                         API key store (SQLite), auth handler, RequestContext
    Endpoints/                    Memory, key management, and health endpoints
    Services/                     StoreManager, MemoryOrchestrator, BundleService, PluginLoader
    Models/                       Request/response DTOs, source-gen JSON context
    Middleware/                   Request timing structured logs
    Dockerfile                    Multi-stage Docker build (includes web UI)

  Scrinia.AppHost/                .NET Aspire AppHost (orchestrates Scrinia.Server)

web/                              React + Vite + Tailwind SPA
  src/api/                        Typed API client and TypeScript DTOs
  src/pages/                      Login, Dashboard, MemoryBrowser, MemoryDetail, KeyManagement
  src/components/                 Layout, MemoryList, MemoryContent, ChunkViewer, SearchBar

tests/
  Scrinia.Tests/                  310 tests (xunit + FluentAssertions)
  Scrinia.Server.Tests/           53 tests (xunit + WebApplicationFactory)
  Scrinia.Plugin.Embeddings.Tests/ 28 tests (xunit + FluentAssertions)

LICENSE                           BSD-3-Clause
NMP_SPEC.md                      NMP/2 format specification
```

## HTTP API server

The server provides REST API access to scrinia's memory stores for multi-user and remote scenarios. The CLI remains the primary interface for local use.

### Running

```bash
# Development (with hot-reload web UI)
cd web && npm run dev &                      # Vite dev server on :5173
dotnet run --project src/Scrinia.Server      # API on :5000

# Production
cd web && npm run build                      # builds to Scrinia.Server/wwwroot/
dotnet run --project src/Scrinia.Server      # serves UI + API on :5000

# Docker
docker compose up -d
```

On first startup, a bootstrap admin API key is written to `BOOTSTRAP_KEY` in the data directory. Read it, then delete the file. Use it to create additional keys.

### Web UI

The web UI is available at the server root (`/`) and provides:
- Login with API key
- Dashboard with health status and store overview
- Memory browser with search and scope filtering
- Memory detail view with chunk navigation
- API key management (create, list, revoke)

### Authentication

**API keys** (built-in): Bearer token authentication. Keys are scoped to specific stores and granular permissions. On first startup, a bootstrap admin key is written to `BOOTSTRAP_KEY` in the data directory.

### Granular permissions

| Permission | Description |
|---|---|
| `read` | View memory content and chunks |
| `search` | Search and list memories |
| `store` | Create/overwrite memories |
| `append` | Append chunks to memories |
| `forget` | Delete memories |
| `copy` | Copy between scopes |
| `export` | Export topic bundles |
| `import` | Import bundles |
| `manage_keys` | API key CRUD |

### API key management

```bash
# Create a key (requires manage_keys permission)
curl -X POST http://localhost:8080/api/v1/keys \
  -H "Authorization: Bearer $BOOTSTRAP_KEY" \
  -H "Content-Type: application/json" \
  -d '{"userId": "alice", "stores": ["default"], "permissions": ["manage_keys"]}'

# List keys
curl http://localhost:8080/api/v1/keys -H "Authorization: Bearer $KEY"

# Revoke a key
curl -X DELETE http://localhost:8080/api/v1/keys/{id} -H "Authorization: Bearer $KEY"
```

### Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health`, `/health/live`, `/health/ready` | None | Health probes |
| GET | `/openapi/v1.json` | None | OpenAPI specification |
| GET | `/scalar/v1` | None | Interactive API explorer (Scalar) |
| POST | `/mcp?store={store}` | Bearer | MCP Streamable HTTP endpoint |
| POST | `/api/v1/stores/{store}/memories` | Bearer | Store a memory |
| GET | `/api/v1/stores/{store}/memories` | Bearer | List memories |
| GET | `/api/v1/stores/{store}/memories/{name}` | Bearer | Show memory content |
| DELETE | `/api/v1/stores/{store}/memories/{name}` | Bearer | Delete memory |
| POST | `/api/v1/stores/{store}/memories/{name}/append` | Bearer | Append chunk |
| POST | `/api/v1/stores/{store}/memories/{name}/copy` | Bearer | Copy memory |
| GET | `/api/v1/stores/{store}/memories/{name}/chunks/{i}` | Bearer | Get chunk |
| GET | `/api/v1/stores/{store}/search?q=...` | Bearer | Search |
| POST | `/api/v1/stores/{store}/export` | Bearer | Export topics |
| POST | `/api/v1/stores/{store}/import` | Bearer | Import bundle |
| POST | `/api/v1/keys` | manage_keys | Create API key |
| GET | `/api/v1/keys` | manage_keys | List keys |
| DELETE | `/api/v1/keys/{id}` | manage_keys | Revoke key |
### Deployment

**Reverse proxy (nginx):**

```nginx
server {
    listen 443 ssl;
    server_name scrinia.example.com;

    ssl_certificate /etc/letsencrypt/live/scrinia.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/scrinia.example.com/privkey.pem;

    location / {
        proxy_pass http://localhost:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;  # required for SSE
    }
}
```

**Reverse proxy (Caddy):**

```
scrinia.example.com {
    reverse_proxy localhost:8080
}
```

### Plugin system

Scrinia supports plugins in both the server and CLI:

**Server plugins** — drop DLLs into the plugins directory. Each plugin is loaded in an isolated `AssemblyLoadContext`. Plugins can register DI services, middleware, HTTP endpoints under `/api/v1/plugins/`, and hook into store/append/forget operations.

**CLI plugins** — self-contained executables in the `plugins/` directory next to `scri`. The CLI discovers `scri-plugin-*` executables and launches them as child processes, communicating via newline-delimited JSON on stdin/stdout. Auto-restart up to 3 times on crash, then graceful degradation.

Plugin authors reference `Scrinia.Plugin.Abstractions` for server plugins. CLI plugins implement the stdin/stdout JSON protocol (see `Scrinia.Plugin.Embeddings.Cli` for a reference implementation).

### Embeddings plugin

The embeddings plugin adds semantic vector search alongside BM25 for hybrid scoring. It uses ONNX Runtime with the `all-MiniLM-L6-v2` model (384 dimensions, ~87 MB) and supports DirectML (GPU), CUDA, and CPU hardware acceleration. The model is auto-downloaded on first use.

**How it works:**
- On `store`/`append`, the plugin embeds the text and stores the vector in `.scrinia/embeddings/`
- On `search`, the query is embedded and cosine similarity scores are combined with BM25 scores
- The ONNX model is cached globally (`%LOCALAPPDATA%/scrinia/plugins/models/`); vectors are per-workspace

**Publishing with embeddings:**

```bash
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

**Configuration (environment variables):**

| Variable | Default | Description |
|---|---|---|
| `SCRINIA_EMBEDDINGS_PROVIDER` | `onnx` | Provider: `onnx`, `ollama`, `openai`, `none` |
| `SCRINIA_EMBEDDINGS_HARDWARE` | `auto` | ONNX hardware: `auto`, `directml`, `cuda`, `cpu` |
| `SCRINIA_EMBEDDINGS_SEMANTICWEIGHT` | `50.0` | Weight for semantic scores in hybrid search |

### Security

The server includes production hardening out of the box:

- **HTTPS/HSTS** — enforced in production environments
- **Security headers** — `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, strict referrer policy
- **Request size limits** — 10 MB body default, 50 MB for bundle imports
- **Input validation** — memory names capped at 256 characters, content at 5 MB per element
- **Rate limiting** — 100 requests/minute sliding window on API endpoints
- **Path traversal protection** — names sanitized to prevent directory escape
- **Error sanitization** — unhandled exceptions return generic 500 responses (no stack traces)
- **Graceful shutdown** — clean resource disposal on `SIGTERM`

### Configuration

| Setting | Env var | Default |
|---|---|---|
| `Scrinia:DataDir` | `Scrinia__DataDir` | `{LocalAppData}/scrinia-server` |
| `Scrinia:Stores:{name}` | `Scrinia__Stores__{name}` | `{DataDir}/stores/{name}` |
| `Scrinia:PluginsDir` | `Scrinia__PluginsDir` | `{DataDir}/plugins` |
| `Scrinia:CorsOrigins` | `Scrinia__CorsOrigins__0` etc. | `[]` (allows all origins) |
