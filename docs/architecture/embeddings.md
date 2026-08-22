# Embeddings Architecture

Scrinia embeddings are an optional retrieval layer over the filesystem memory store. The current
architecture has two deployment choices: an in-process Core provider, or an optional standalone
MCP plugin for heavyweight native inference.

## Components

```
scri CLI + MCP tools
  ├─ Scrinia.Core
  │    ├─ IEmbeddingProvider + resilient API providers
  │    ├─ Model2Vec provider and model manager
  │    ├─ MemoryVectorIndexer + TextChunker
  │    ├─ VectorStore (signed SVF3 files)
  │    └─ HybridReranker
  └─ optional scri-plugin-embeddings child process
       └─ VulkanEmbeddingProvider + EmbeddingsTools over MCP stdio
```

`Scrinia.Core` has no web-server dependency. The CLI configures the active provider and installs
the corresponding search contributor and event sink. The optional plugin keeps GPU/native model
dependencies outside the CLI process; `McpPluginHost` communicates with it over stdio.

## Provider contract

`IEmbeddingProvider` exposes `IsAvailable`, `Dimensions`, `Signature`, `EmbedAsync`, and
`EmbedBatchAsync`. Provider signatures identify the model/provider combination. A vector response
whose length disagrees with the declared provider dimension, or with earlier vectors from the same
provider, is rejected before indexing. Batch results are also checked for count, null/empty values,
finite values, and consistent dimensions by `MemoryVectorIndexer`.

HTTP providers derive from `ResilientEmbeddingProvider`, which owns retry and circuit-breaker
behavior. Four-hundred-series input failures do not trip the circuit; cancellation from the caller
propagates. Parse and shape failures return no vector and leave existing stored vectors untouched.

## Indexing and replacement

`MemoryVectorIndexer` reconstructs a memory's content elements, slices windows with
`TextChunker`, embeds the windows in batches, validates the complete result, and calls
`VectorStore.ReplaceMemoryVectorsAsync`. Validation happens before replacement, so malformed
provider output cannot erase a previous memory snapshot.

`VectorStore` stores persistent scopes under `.scrinia/embeddings/` using signed SVF3
files. Signatures detect provider or embedding-option changes; stale files are quarantined and
`EmbeddingReindexer` rebuilds persistent memories through the same indexer. Full-memory rewrites
write a complete temporary file and atomically publish it only after the new bytes are complete.
An interrupted or failed rewrite therefore leaves the previous durable snapshot readable.

Ephemeral memories are never reindexed from disk. Their vectors live only for the store instance.

## Search flow

Keyword search remains the baseline. When embeddings are available, `HybridReranker` embeds the
query, reads vectors for the keyword candidate scopes, computes vector scores, and combines them
with the configured ranking strategy. Missing or unavailable embeddings degrade to keyword search;
they do not make memory writes fail.

## Reindexing

`EmbeddingReindexer.ReindexAsync` walks persistent entries from `IMemoryStore`, reconstructs each
artifact's content array, and indexes each memory atomically. It reports total, embedded, skipped,
and failed counts while continuing past individual artifact failures. `scri reindex` invokes the
same Core path for an explicit rebuild. Plugin-backed reindexing uses the plugin event sink and
prefers the plugin's whole-memory `index` tool.

## Plugin protocol

The optional executable exposes `index`, `search`, `upsert` (compatibility), `remove`, and `status`
through MCP stdio. `index` is the preferred operation because it preserves Core's element/window
layout and atomic replacement semantics. The host owns retries at the process boundary. See
[Plugin Authoring](../plugin-authoring.md) for discovery, configuration, and testing.
