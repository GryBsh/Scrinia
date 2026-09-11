# Architecture Overview

Scrinia is a .NET 10 CLI and MCP application with a shared Core library, filesystem-backed
memories, and optional out-of-process plugins.

## System diagram

```
MCP clients
    │ stdio
    ▼
scri (CLI + MCP tools)
    ├── Scrinia.Core ── .scrinia/ filesystem
    │     ├── FileMemoryStore and NMP/2 encoding
    │     ├── BM25/search and entity facts
    │     └── embeddings, vectors, and reranking
    └── optional scri-plugin-embeddings child process
          └── MCP stdio, Vulkan provider
```

## Solution structure

```
src/
  Scrinia.Core/                  storage, encoding, search, embeddings
  Scrinia.Mcp/                   MCP memory tools and response envelopes
  Scrinia/                       CLI executable (AssemblyName: scri)
  Scrinia.Plugin.Abstractions/  published plugin contracts
  Scrinia.Plugin.Embeddings/    Vulkan provider and plugin implementation
  Scrinia.Plugin.Embeddings.Cli/standalone embeddings MCP executable
tests/                            Core, CLI/MCP, plugin, merge, and benchmark suites
```

## Dependency boundaries

`Scrinia.Core` owns the storage contract, NMP/2 encoding, search, entity facts, embedding providers,
`MemoryVectorIndexer`, `VectorStore`, and `EmbeddingReindexer`. It has no web-host dependency.

`Scrinia.Mcp` depends on Core and defines the public memory tools. `scri` configures the workspace,
hosts MCP over stdio, and supervises an optional plugin child process. Plugin executables communicate
over MCP; they are not dynamically loaded class libraries.

`Scrinia.Plugin.Abstractions` contains the published plugin identity and operation/event contracts.
The embeddings implementation references Core and uses `MemoryVectorIndexer` and `VectorStore`
rather than duplicating the indexing policy.

## Shared server (Core/MCP/CLI direction)

The shared-memory surface is the second MCP server, exposed by `scri shared serve` alongside the
local `scri serve`. It is governed by the same Core/MCP/CLI boundary as the local surface, extended
so that every shared abstraction lives in Core and the executable owns only host adapters:

- `Scrinia.Core` owns all shared configuration, registry, base identity, runtime contract, snapshot,
  fingerprint, classifier, and operation-result abstractions consumed by the shared surface
  (`IHomeConfigStore`/`HomeConfigStore`, `IWorkspaceConfig`/`WorkspaceConfig`, `ISharedBaseRegistry`/
  `SharedBaseRegistry`, `ISharedBaseRuntimeFactory`/`SharedBaseRuntimeFactory`, `ISharedBaseRuntime`/
  `SharedBaseRuntime`, `SharedClassifier`, `SharedManifest`, the shared DTOs, and the `SharedServer`
  selector/orchestration surface).
- `Scrinia.Mcp` references Core only and implements the shared handlers and envelope serialization;
  it never references the executable `Scrinia`.
- The executable contains host adapters only: the `SharedServeHostAdapter` composition root for
  `scri shared serve` resolves the Core services and owns command/transport startup; the local
  `ScriniaMcpTools` registration and local store path remain unchanged.

`scri shared serve` starts only with at least one enabled base and exposes exactly eleven prefixed
tools: `shared_bases`, `shared_search`, `shared_list`, `shared_recall`, `shared_facts`,
`shared_remember`, `shared_append`, `shared_forget`, `shared_compact`, `shared_link`, and
`shared_refresh`. Local `scri serve` remains unchanged and disjoint — the two surfaces register
disjoint tool sets, and no scalar base name appears before canonical-name syntax and enabled-membership
validation. The shared surface is prompt-isolated and process-free: no shared input carries a prompt,
correlation, or session field, and no shared handler launches a process (Git guidance is print-only).

Phase 4 consumes these final contracts and cannot alter the shared response wording; this architecture
is the exit gate that Phase 4 reads.

## Deployment

The CLI reads and writes a local `.scrinia/` workspace. Built-in semantic search uses Model2Vec or
one of the Core HTTP providers in-process. The optional Vulkan plugin keeps native GPU/model
dependencies in a child process and communicates through MCP stdio. Workspace sharing is handled by
Git-backed workspaces use ordinary source-control workflows; Scrinia does not install or run a merge driver.

## Core abstractions

| Abstraction | Purpose |
|---|---|
| `IMemoryStore` | Storage and scope contract |
| `IEncodingStrategy` | NMP/2 compression format |
| `IEmbeddingProvider` | Embedding generation |
| `VectorStore` | Signed per-scope vector persistence |
| `ISearchScoreContributor` | Supplemental search scores |
| `IMemoryEventSink` | Store/append/forget notifications |
| `IScriniaPlugin` | Plugin identity |
| `IMemoryOperationHook` | Operation lifecycle contract |

## Dispatch and trimming

The CLI sets the `Default` halves of `MemoryStoreContext`, `SearchContributorContext`, and
`MemoryEventSinkContext` because `AsyncLocal` does not cross the generic-host MCP handler boundary.
`Current` remains available for isolated async flows and tests.

The CLI is trimmed and single-file. Source-generated JSON contexts, source-generated CLI commands,
and child-process plugin discovery avoid reflection and dynamic assembly loading.

All writes go through the MCP tools and `FileMemoryStore`. Store, append, and forget dispatch their
event hooks once; search is read-only and fires no write sinks.

See [CLI Architecture](cli.md), [Core Architecture](core.md), and [Embeddings Architecture](embeddings.md).
