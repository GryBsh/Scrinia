# Architecture Overview

Scrinia is structured as a set of .NET 10 projects with a shared core library, one frontend, and an extensible plugin system.

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
      +-------v------+
      |  Filesystem  |
      |  (.scrinia/) |
      +--------------+
```

## Solution Structure

```
Scrinia.sln (14 projects)
  src/
    Scrinia.Core/                    Shared library: encoding, models, search, storage
    Scrinia.Mcp/                     MCP tool definitions (12 per-verb tools)
    Scrinia/                         CLI executable (AssemblyName: scri)
    Scrinia.Merge/                   Git merge driver (AOT; no ProjectReferences, by constraint)
    Scrinia.Plugin.Abstractions/     Plugin interfaces
    Scrinia.Plugin.Embeddings/       Optional Vulkan GPU acceleration (LLamaSharp)
    Scrinia.Plugin.Embeddings.Cli/   CLI plugin executable (Vulkan MCP server)
  benchmarks/
    Scrinia.Retrieval.Benchmarks/    Retrieval-quality harness (LoCoMo, LongMemEval)
  tests/
    Scrinia.Tests/                   CLI + MCP + Core + Embeddings
    Scrinia.TestDoubles/             Shared hand-written doubles (no mocking framework)
    Scrinia.Merge.Tests/             Merge driver, including the forced-duplication parity suites
    Scrinia.Plugin.Embeddings.Tests/ Vulkan plugin CLI
    Scrinia.Retrieval.Benchmarks.Tests/  Harness and judge
    Scrinia.Benchmarks/              BenchmarkDotNet micro-benchmarks
```

## Dependency Graph

```
Scrinia.Core  <--  Scrinia.Mcp  <--  Scrinia (CLI)
     ^                                     |
     |                             McpPluginHost (MCP client)
     |                                     |
     |                             scri-plugin-embeddings (child process)
     |
     +--- Scrinia.Plugin.Abstractions (refs Core, FrameworkRef AspNetCore)
     |
     +--- Scrinia.Plugin.Embeddings (refs Core + Abstractions)

Scrinia.Merge stands alone: no ProjectReferences at all, so it can publish AOT.
```

Key constraints:
- **Core** has no ASP.NET or native dependencies (only `System.IO.Hashing`). Includes built-in embeddings (Model2Vec, API providers, VectorStore, HybridReranker).
- **Mcp** depends only on Core and `ModelContextProtocol`. Defines the MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`) and `facts` — each with its own JSON schema, plus `ScriniaMcpJsonContext` for trimming safety
- **CLI** is trimmed and single-file; plugins run as separate processes
- **Plugin.Abstractions** still uses `FrameworkReference` for ASP.NET (no package dependency). That
  reference exists for the DI and endpoint-mapping half of `IScriniaPlugin`, which the deleted
  server was the only host for. Nothing left in the repository calls `ConfigureServices` or
  `MapEndpoints`; the reference and those members are kept because the plugin ABI is published, not
  because anything consumes them.

## Deployment

One mode: the CLI (`scri`) runs as both a command-line tool and an MCP server over stdio. It reads
and writes directly to a `.scrinia/` workspace on the local filesystem.

Semantic search is built-in via Model2Vec (in-process, zero native deps). An optional Vulkan GPU
plugin can override the built-in provider, running as a child process via MCP over stdio.

Sharing a workspace between people is a git question, not a server one — `.scrinia/` is committed
like any other directory, and the merge driver resolves what conflicts. See
[Multi-User Setup](../multi-user-setup.md).

There were two other modes until they were removed: an ASP.NET Core HTTP API with API-key auth,
multi-store routing and a React web UI, and a `scri serve --remote` that proxied the same MCP tool
surface to it. Both are gone. What they cost while they existed is recorded under
[Known Divergences](#known-divergences-carried-forward).

See [CLI Architecture](cli.md).

## Core Abstractions

| Abstraction | Purpose | Location |
|-------------|---------|----------|
| `IMemoryStore` | The whole storage contract, filesystem layout included | Core |
| `IEncodingStrategy` | NMP/2 compression format | Core |
| `ISearchScoreContributor` | Plugin hook for supplemental search scores | Core |
| `IMemoryEventSink` | Plugin hook for store/append/forget events | Core |
| `IEmbeddingProvider` | Vector embedding generation | Core.Embeddings |
| `IScriniaPlugin` | Plugin lifecycle | Plugin.Abstractions |
| `IMemoryOperationHook` | Before/after hooks around store/append/forget | Plugin.Abstractions |

## Key Design Decisions

### AsyncLocal-Based Dispatch

Scrinia routes operations to the store and plugins through `AsyncLocal<T>` contexts with a `Default`
static fallback:

- `MemoryStoreContext.Current` -- store resolution
- `SearchContributorContext.Current` / `.Default` -- plugin search scoring
- `MemoryEventSinkContext.Current` / `.Default` -- plugin event hooks

The CLI sets `.Default` at startup, since `AsyncLocal` does not propagate through the generic host
to MCP handler threads. The `.Current` half is what a host serving concurrent callers would set per
call; the server did, and nothing does now. Kept rather than collapsed to a static: the fallback
pair is also how tests install a store for one async flow without disturbing another.

### Trimming and AOT Safety

The CLI publishes as a trimmed single-file executable. This requires:
- Source-generated JSON contexts for all serialized types
- ConsoleAppFramework v5 (source-gen CLI, zero reflection)
- No dynamic assembly loading (plugins run as child processes)

### One Write Path

Every write arrives through the MCP tool surface in this process and goes to `FileMemoryStore`.
Store, append and forget fire `IMemoryEventSink` via `MemoryEventSinkContext.Default`; search
scoring reaches plugins through `ISearchScoreContributor` via `SearchContributorContext.Default`.

This section used to describe three doors — the CLI's MCP stdio, the server's MCP endpoint, and the
server's REST API — and the fault line it was written to explain was REST-versus-MCP *inside the
single server process*, not CLI-versus-server. Two of the three doors are gone. Two things it
established are still worth carrying:

- **`PluginPipeline` wraps only `StoreAsync`, `AppendAsync` and `ForgetAsync`.** `SearchAsync` fires
  no sinks, by design.
- **A component registered as both an `IMemoryOperationHook` and an `IMemoryEventSink` must fire
  exactly once.** `EventSinkOperationHook` enforced that by excluding any sink that was also a hook.
  With one door the double-fire cannot happen today, but the rule is what a second door would have
  to honour.

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
- **The store-parity divergences went with the remote store, and so did the suite that found
  them.** `HttpMemoryStore` never applied the topic-name-to-scope mapping `FileMemoryStore` does,
  and `PagedListingConformanceTests` documented that rather than asserting it away. Both are
  deleted. The lesson that outlived them: that suite covered only the *unfiltered* listing case
  until late, and **every** store-parity defect it eventually found was in the filtered case. A
  conformance suite that exercises the default arguments is testing the case least likely to be
  wrong.

## Further Reading

- **[Core Architecture](core.md)** -- IMemoryStore, FileMemoryStore, NMP/2 encoding, search algorithms, models
- **[CLI Architecture](cli.md)** -- Workspace discovery, commands, MCP tools, plugin host
- **[Embeddings Architecture](embeddings.md)** -- Providers, vector store, hybrid scoring
