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
  Scrinia.Merge/                 standalone Git merge driver
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

## Deployment

The CLI reads and writes a local `.scrinia/` workspace. Built-in semantic search uses Model2Vec or
one of the Core HTTP providers in-process. The optional Vulkan plugin keeps native GPU/model
dependencies in a child process and communicates through MCP stdio. Workspace sharing is handled by
Git and the Scrinia merge driver.

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
