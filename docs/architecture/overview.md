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

### MCP Response Envelope and Truncation

Every MCP tool returns a hand-built YAML envelope (`McpResponse`, `Scrinia.Mcp/McpResponse.cs`)
with a fixed field set: `status`, `action`, `path`, `error`/`errorCode`, `instruction`,
`actionNeeded`, `info`, `followUp`, and `content`. `ResponseBuilder.Success`/`Warning` are the only
two factories that pass their `content` through `Truncate` (`McpResponse.cs:198`), which caps it at
`MaxResponseChars`(8192) minus a 200-char YAML overhead allowance — 7992 chars. **Only `content:`
is capped.** `actionNeeded:`, `info:` and `followUp:` have never been bounded by this mechanism;
`ResponseBuilder.Error` doesn't route through `Truncate` at all.

`guide()` is the one caller exempt from the cap (`ResponseBuilder.SuccessUntruncated`,
`McpResponse.cs:124`; Task 10, T10-A). The embedded guide document is bounded by construction
(fixed at build time, not shaped by a caller's query), and had grown past the cap — 8046 chars
against a 7992-char budget — so truncation was silently dropping its last few dozen characters on
the most-called tool on the surface. The document's size is instead pinned at test time
(`tests/Scrinia.Tests/Mcp/GuideSizeTests.cs`), so "bounded by construction" stays a checked fact.
When `search`, `recall` or `list` does truncate, the `followUp` remedy names that tool's own
parameters (`ResponseBuilder.WithTruncationAdvice`) rather than a generic "narrow with search, or
recall a chunk" line that made no sense coming from, say, `list`.

**Decision (Task 10, T10-C): the uncapped `info`/`actionNeeded`/`followUp` fields are correct as
they are — do not add a `WithInfo`-only cap.** Evidence, from Task 8's review: `restore`'s
`followUp` was already unbounded (one entry per `/patterns/` index entry and per `/agent/*.md`
file) before this branch touched it; nothing on the `info:`/`actionNeeded:`/`followUp:` path can
report that it was cut, unlike `content:`'s `ContentTruncated` flag, so a cap on just one of these
fields would silently drop data with no signal that anything was lost — strictly worse than the
current unbounded state. If this is ever addressed, it belongs at the envelope level as one
whole-response character budget with one truncation signal covering every field, not a second
cap bolted onto `info:` alone.

### Known Divergences Carried Forward

Recorded here so neither is mistaken for an oversight or "fixed" without a deliberate decision:

- **The address-surface reflection gate keys on parameter names.** The fixture that pins which MCP
  tools take an address (`tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs:76`) matches
  `[McpServerTool]` methods by parameter name — `path`, `artifactOrName`, `nameOrUri`,
  `destination`, `source` — not by any structural marker of "this parameter is an address". A
  future tool taking an address under a different parameter name would slip past this gate
  silently. Accepted at the time (Task 5): a `MemoryAddress` wrapper type was considered and
  rejected, since three of the five address-taking parameters are not pure addresses (`recall`'s
  `artifactOrName` also accepts inline NMP/2 text; `forget`'s `nameOrUri` also accepts `file://`
  URIs), so wrapping them uniformly would misrepresent them. What actually pins the ephemeral-
  alias rule is the behavioural round-trip tests per surface, not this reflection gate — the gate
  only catches an added tool going completely unreviewed. Re-verified current as of Task 10.
- **`HttpMemoryStore` never applies the topic-name-to-scope mapping `FileMemoryStore` does.**
  `ResolveReadScopes(["t00"])` returns `["t00"]` verbatim on the remote store, where the local
  store maps it to `["local:t00"]` (`FileMemoryStore`'s `NormalizeScopeFilters`). Pre-existing,
  predates this branch. Documented rather than silently asserted away by
  `PagedListingConformanceTests.ResolveReadScopes_FilteredByTopicName_StoresDeliberatelyDiverge`.
  Candidate for a later branch — note that the local/remote conformance suite covered only the
  *unfiltered* listing case until Task 9 added filtered-case coverage; every store-parity defect
  found on this branch was in the filtered case, so the same may be true of divergences not yet
  found.

## Further Reading

- **[Core Architecture](core.md)** -- IMemoryStore, FileMemoryStore, NMP/2 encoding, search algorithms, models
- **[CLI Architecture](cli.md)** -- Workspace discovery, commands, MCP tools, plugin host
- **[Server Architecture](server.md)** -- Startup, middleware, auth, multi-store, plugin loading
- **[Embeddings Architecture](embeddings.md)** -- Providers, vector store, hybrid scoring
