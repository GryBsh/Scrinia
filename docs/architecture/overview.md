# Architecture Overview

Scrinia is structured as a set of .NET 10 projects with a shared core library, multiple deployment frontends, and an extensible plugin system.

## System Diagram

```
                         MCP Clients (Claude Code, Cursor, Copilot)
                                    |
                             stdio transport
                                    |
                  +----------------------------------+
                  |          scri (CLI + MCP)         |
                  |  ScriniaCommands | ScriniaMcpTools |
                  +--------+------------+------------+
                           |            |
              +------------+            +-----+
              |                               |
   +----------v-----------+    +--------------v--------------+
   |    Scrinia.Core       |    | scri-plugin-embeddings (MCP) |
   |  IMemoryStore         |    |  VectorStore | HnswIndex    |
   |  FileMemoryStore      |    |  EmbeddingProviders          |
   |  NMP/2 Encoding       |    +------------------------------+
   |  BM25 + Search        |
   +----------+------------+
              |
   +----------v-----------------------------------------------------+
   |                    Scrinia.Server (HTTP API)                    |
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
    Scrinia.Mcp/                     MCP tool definitions (17 tools)
    Scrinia/                         CLI executable (AssemblyName: scri)
    Scrinia.Server/                  ASP.NET Core HTTP API + Web UI
    Scrinia.AppHost/                 .NET Aspire orchestration
    Scrinia.Plugin.Abstractions/     Plugin interfaces for server
    Scrinia.Plugin.Embeddings/       Embeddings plugin (shared logic)
    Scrinia.Plugin.Embeddings.Cli/   CLI plugin executable (MCP server)
  tests/
    Scrinia.Tests/                   325 tests (CLI + MCP + Core)
    Scrinia.Server.Tests/            53 tests (HTTP API)
    Scrinia.Plugin.Embeddings.Tests/ 59 tests (embeddings)
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
- **Core** has no ASP.NET dependencies (only `System.IO.Hashing`)
- **Mcp** depends only on Core and `ModelContextProtocol`
- **CLI** is trimmed and single-file; plugins run as separate processes
- **Server** references everything except the CLI plugin executable
- **Plugin.Abstractions** uses `FrameworkReference` for ASP.NET (no package dependency)

## Three Deployment Modes

### 1. CLI + MCP (Local)

The CLI (`scri`) runs as both a command-line tool and an MCP server. It reads and writes directly to a `.scrinia/` workspace on the local filesystem.

The embeddings plugin runs as a child process communicating via MCP over stdio. The CLI is the MCP client; the plugin is the MCP server.

See [CLI Architecture](cli.md).

### 2. HTTP API Server

The server provides multi-user access with API key authentication, a REST API, MCP over HTTP, and a web UI. Each API key is scoped to specific stores and permissions.

The embeddings plugin runs in-process as a loaded DLL.

See [Server Architecture](server.md).

### 3. Remote CLI

The CLI can proxy all operations to a remote Scrinia.Server via `--remote`. This gives MCP clients (like Claude Code) a local stdio interface backed by a shared server.

## Core Abstractions

| Abstraction | Purpose | Location |
|-------------|---------|----------|
| `IMemoryStore` | 27-method interface for all memory operations | Core |
| `IEncodingStrategy` | NMP/2 compression format | Core |
| `ISearchScoreContributor` | Plugin hook for supplemental search scores | Core |
| `IMemoryEventSink` | Plugin hook for store/append/forget events | Core |
| `IStorageBackend` | Factory for creating `IMemoryStore` instances | Core |
| `IEmbeddingProvider` | Vector embedding generation | Plugin.Embeddings |
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

### Two Plugin Code Paths

The CLI and server use different hook mechanisms to avoid double-firing:

| Path | Event Hooks | Search Scoring |
|------|-------------|----------------|
| CLI (MCP stdio) | `IMemoryEventSink` via `MemoryEventSinkContext` | `ISearchScoreContributor` via `SearchContributorContext` |
| Server (REST API) | `IMemoryOperationHook` via `PluginPipeline` | `ISearchScoreContributor` via `SearchContributorContext` |

A plugin implementing both interfaces fires only once per operation.

## Further Reading

- **[Core Architecture](core.md)** -- IMemoryStore, FileMemoryStore, NMP/2 encoding, search algorithms, models
- **[CLI Architecture](cli.md)** -- Workspace discovery, commands, MCP tools, plugin host
- **[Server Architecture](server.md)** -- Startup, middleware, auth, multi-store, plugin loading
- **[Embeddings Architecture](embeddings.md)** -- Providers, vector store, HNSW, hybrid scoring
