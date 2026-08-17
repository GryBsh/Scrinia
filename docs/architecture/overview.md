# Architecture Overview

Scrinia is structured as a set of .NET 10 projects with a shared core library, multiple deployment frontends, and an extensible plugin system.

## System Diagram

```
                         MCP Clients (Claude Code, Cursor, Copilot)
                                    |
                             stdio transport
                                    |
                  +---------------------------------------------+
                  |              scri (CLI + MCP)                |
                  |  ScriniaCommands | MCP Tools (12)            |
                  |                  | guide + verbs             |
                  +--------+------------+------------------------+
                           |            |
              +------------+            +-----+
              |                               |
   +----------v-----------+    +--------------v--------------+
   |    Scrinia.Core       |    | scri-plugin-embeddings (MCP) |
   |  IMemoryStore         |    |  (optional Vulkan GPU accel) |
   |  FileMemoryStore      |    +------------------------------+
   |  NMP/2 Encoding       |
   |  BM25 + Search        |
   |  Embeddings (Model2Vec |
   |  + API providers)     |
   +----------+------------+
              |
   +----------v-----------------------------------------------------+
   |                      Scrinium (HTTP API)                        |
   |  REST Endpoints | API Key Auth | Multi-Store | Web UI | MCP/HTTP |
   +--------+-------------------+-----------------------------------+
            |                   |
   +--------v--------+  +------v-------+
   |  SQLite (keys)  |  |  Filesystem  |
   +-----------------+  |  (.scrinia/) |
                        +--------------+
```

## Solution Structure

```
Scrinia.sln (11 projects)
  src/
    Scrinia.Core/                    Shared library: encoding, models, search, storage
    Scrinia.Mcp/                     MCP tool definitions (12 per-verb tools)
    Scrinia/                         CLI executable (AssemblyName: scri)
    Scrinia.Server/                  ASP.NET Core HTTP API + Web UI
    Scrinia.AppHost/                 .NET Aspire orchestration
    Scrinia.Plugin.Abstractions/     Plugin interfaces for server
    Scrinia.Plugin.Embeddings/       Optional Vulkan GPU acceleration (LLamaSharp)
    Scrinia.Plugin.Embeddings.Cli/   CLI plugin executable (Vulkan MCP server)
  tests/
    Scrinia.Tests/                   648 tests (CLI + MCP + Core + Embeddings)
    Scrinia.Server.Tests/            61 server tests (HTTP API)
    Scrinia.Merge.Tests/             18 merge driver tests
    Scrinia.Plugin.Embeddings.Tests/ 12 tests (Vulkan plugin CLI + benchmarks)
  web/                               React 19 + Vite + Tailwind CSS SPA
```

## Dependency Graph

```
Scrinia.Core  <--  Scrinia.Mcp  <--  Scrinia (CLI)
     ^                  ^                  |
     |                  |          McpPluginHost (MCP client)
     |                  |                  |
     |                  |          scri-plugin-embeddings (child process)
     |                  |
     +--- Scrinia.Server (refs Core + Mcp + Abstractions)
     |
     +--- Scrinia.Plugin.Abstractions (refs Core, FrameworkRef AspNetCore)
     |
     +--- Scrinia.Plugin.Embeddings (refs Core + Abstractions)
```

Key constraints:
- **Core** has no ASP.NET or native dependencies (only `System.IO.Hashing`). Includes built-in embeddings (Model2Vec, API providers, VectorStore, HybridReranker).
- **Mcp** depends only on Core and `ModelContextProtocol`. Defines the MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`) and `facts` — each with its own JSON schema, plus `ScriniaMcpJsonContext` for trimming safety
- **CLI** is trimmed and single-file; plugins run as separate processes
- **Server** references everything except the CLI plugin executable
- **Plugin.Abstractions** uses `FrameworkReference` for ASP.NET (no package dependency)

## Three Deployment Modes

### 1. CLI + MCP (Local)

The CLI (`scri`) runs as both a command-line tool and an MCP server. It reads and writes directly to a `.scrinia/` workspace on the local filesystem.

Semantic search is built-in via Model2Vec (in-process, zero native deps). An optional Vulkan GPU plugin can override the built-in provider, running as a child process via MCP over stdio.

See [CLI Architecture](cli.md).

### 2. HTTP API Server

The server provides multi-user access with API key authentication, a REST API, MCP over HTTP, and a web UI. Each API key is scoped to specific stores and permissions.

Semantic search is built-in. The optional Vulkan embeddings plugin can run in-process as a loaded DLL for GPU acceleration.

See [Server Architecture](server.md).

### 3. Remote CLI

The CLI can proxy all operations to a remote Scrinia.Server via `--remote`. This gives MCP clients (like Claude Code) a local stdio interface backed by a shared server.

## Core Abstractions

| Abstraction | Purpose | Location |
|-------------|---------|----------|
| `IMemoryStore` | Storage contract every store (local or remote) can honour | Core |
| `ILocalMemoryStore` | Extends `IMemoryStore` with the 7 members that need a local filesystem; `FileMemoryStore` implements it, `HttpMemoryStore` does not | Core |
| `IEncodingStrategy` | NMP/2 compression format | Core |
| `ISearchScoreContributor` | Plugin hook for supplemental search scores | Core |
| `IMemoryEventSink` | Plugin hook for store/append/forget events | Core |
| `IStorageBackend` | Factory for creating `IMemoryStore` instances | Core |
| `IEmbeddingProvider` | Vector embedding generation | Core.Embeddings |
| `IScriniaPlugin` | Server plugin lifecycle | Plugin.Abstractions |
| `IMemoryOperationHook` | Server-side before/after hooks | Plugin.Abstractions |

## Key Design Decisions

### AsyncLocal-Based Dispatch

Both the CLI and server need to route operations to the correct store and plugins, but via different mechanisms (static singletons vs. per-request DI). Scrinia uses `AsyncLocal<T>` contexts with a `Default` static fallback:

- `MemoryStoreContext.Current` -- per-request store resolution
- `SearchContributorContext.Current` / `.Default` -- plugin search scoring
- `MemoryEventSinkContext.Current` / `.Default` -- plugin event hooks

The CLI sets `.Default` at startup (since AsyncLocal doesn't propagate through the generic host to MCP handler threads). The server sets `.Current` per-request.

### Trimming and AOT Safety

The CLI publishes as a trimmed single-file executable. This requires:
- Source-generated JSON contexts for all serialized types
- ConsoleAppFramework v5 (source-gen CLI, zero reflection)
- No dynamic assembly loading (plugins run as child processes)

### Three Write Paths

There are three doors a write can arrive through, not two — the server hosts an MCP endpoint
(`Program.cs:~290`) alongside its REST API, so the CLI-versus-server framing this section used to
use couldn't express the real fault line, which was REST-versus-MCP *inside the single server
process*:

| Path | Event Hooks | Search Scoring |
|------|-------------|----------------|
| CLI (MCP stdio) | `IMemoryEventSink` via `MemoryEventSinkContext.Default` | `ISearchScoreContributor` via `SearchContributorContext.Default` |
| Server — MCP endpoint | `IMemoryEventSink` via `MemoryEventSinkContext.Current` (set per-session, `Program.cs:316`) | `ISearchScoreContributor` via `SearchContributorContext.Current` |
| Server — REST API | `IMemoryOperationHook` via `PluginPipeline`, including `EventSinkOperationHook` (`Program.cs:106`) | `ISearchScoreContributor` via `SearchContributorContext` |

The server's two doors share one DI-registered sink set (`IMemoryEventSink` registrations in
`Program.cs`, including `MaintenanceEventSink` at `:101`, registered unconditionally rather than
nested inside the embeddings block). Its MCP endpoint reads that set directly into
`MemoryEventSinkContext.Current` per session. Its REST door reads the same set through
`EventSinkOperationHook`, an `IMemoryOperationHook` adapter that runs each sink from inside
`PluginPipeline` instead. Different mechanism, same membership: **every store, append, and forget
fires the same sink set whichever door it arrived at.** `PluginPipeline` only wraps those three
operations (`StoreAsync`, `AppendAsync`, `ForgetAsync`); `SearchAsync` fires none by design, and it
is the only mutation-adjacent method it exposes.

Four REST routes bypass `PluginPipeline` entirely and fire no sinks on the REST side:
`PATCH /memories/{name}`, `POST /memories/{name}/copy`, `POST /memories/{name}/copy-from-client`,
and `POST /import`. Each has an MCP counterpart that also fires nothing (`link`'s `UpdateMeta` for
PATCH, the local-only `Copy` for copy, no MCP tool at all for copy-from-client and import), so the
two doors still agree — they just agree on firing nothing, not on firing the shared set. Treat the
"same sink set" guarantee above as scoped to store/append/forget; do not extend it to every write
by enumeration from this paragraph alone.

The split into two mechanisms is deliberate and still necessary — it is what stops a component
that implements both `IMemoryEventSink` and `IMemoryOperationHook` from firing twice for the same
REST write. `EventSinkOperationHook` enforces this by excluding any sink that is also an
`IMemoryOperationHook` (such a sink already runs on the REST path in its own right). So the
guarantee that holds across all three paths is: **a component registered as both a hook and a sink
still fires exactly once.**

## Further Reading

- **[Core Architecture](core.md)** -- IMemoryStore, FileMemoryStore, NMP/2 encoding, search algorithms, models
- **[CLI Architecture](cli.md)** -- Workspace discovery, commands, MCP tools, plugin host
- **[Server Architecture](server.md)** -- Startup, middleware, auth, multi-store, plugin loading
- **[Embeddings Architecture](embeddings.md)** -- Providers, vector store, hybrid scoring
