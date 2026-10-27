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
For providers that discover dimensions, the first valid response establishes the dimension
atomically; malformed batches cannot establish it. After circuit cooldown, only one request
holds the half-open trial slot, which is released on every outcome, including cancellation.

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

Each persistent scope mutation takes the instance scope gate and an exclusive file lease, then
rereads the authoritative scope file before applying its change. Upserts, removals, replacements,
and migration publication share that ownership boundary. A mutation atomically replaces the
complete file before publishing its local cache, so an already-warm writer cannot overwrite a
peer's additions or resurrect its deletions from an old snapshot.

Persistent reads reload the scope under the file lease, so an already-warm instance observes
peer replacements and deletions on its next read. A failed freshness check does not serve an
earlier snapshot. Derived vectors remain outside the authoritative memory bundle transaction
and its rollback manifest.

Persistent mutation rereads and rewrites the complete scope file, so cost grows with scope size.
Serialization and its final byte-array copy require additional complete-payload memory. Exclusive
reads serialize with writers. Existing signature quarantine, unsupported-format refusal,
and bounded retry behavior remain; synchronous read/move retries have cooperative-cancellation
limits.

Windows and Ubuntu Linux process tests cover independent writers, cancellation/reuse, and
migration interleaving. macOS has classifier contract coverage only, with no executed host gate.

Ephemeral memories are never reindexed from disk. Their vectors live only for the store instance.

## Search flow

Keyword search remains the baseline. When embeddings are available, `HybridReranker` embeds the
query, scores the same bounded eligible lexical snapshot supplied to the other contributors,
and combines scores with the configured ranking strategy. Missing or unavailable embeddings degrade to keyword search;
they do not make memory writes fail. Scope, excluded-topic, path/address, and attribute filters
precede candidate K; the final result limit remains separate.

The unchanged Windows benchmark lanes measured single-memory synthetic writes at 0.5262–0.6727
times the prior elapsed time, with allocations at 0.5129–1.0043 times the prior values. These
short-iteration measurements have broad intervals and do not establish a statistically certain
speedup. The separate growing 49-fact lane measured BM25 at 2,089.1 → 2,126.1 ms and Model2Vec at
2,338.1 → 2,703.0 ms (15.6% higher). That lane reports no allocations. Full-scope read/write work
and temporary buffers remain explicit costs of writer coherence.

## Reindexing

`EmbeddingReindexer.ReindexAsync` walks persistent entries from `IMemoryStore`, reconstructs each
artifact's content array, and indexes each memory atomically. It reports total, embedded, skipped,
and failed counts while continuing past individual artifact failures. `scri reindex` invokes the
same Core path for an explicit rebuild. Plugin-backed reindexing uses
`IEmbeddingIndexCommand` through `ReindexViaCommandAsync`. Only an explicit `Indexed` outcome
increments the embedded count; unavailable or failed commands increment the failed count.
Best-effort write event sinks cannot acknowledge an administrative rebuild.

## Plugin protocol

The release payload installs only through explicit `scri setup`, with an adjacent manifest or
`--embeddings-source <extracted-release-directory>`. Setup copies verified bytes into
`SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`, preserves extracted source files, and
selects the workspace plugin name/version. A checksum establishes consistency, not publisher
identity. Windows/Linux use Vulkan; macOS uses CPU/Metal. `serve` never installs a bundle.

Setup success confirms installation/configuration/path selection, not native loading or inference.
Later configuration or model failures retain the installation; retry after correcting the reported
problem. Model2Vec and GGUF downloads use the network and live below `SCRINIA_HOME/models`;
optional HTTP providers use their configured endpoint. See [installation](../installation.md#embeddings-plugin-installation).

The optional executable exposes `index`, `search`, `upsert` (compatibility), `remove`, and `status`
through MCP stdio. `index` is the preferred operation because it preserves Core's element/window
layout and atomic replacement semantics. The host owns retries at the process boundary. See
[Plugin Authoring](../plugin-authoring.md) for discovery, configuration, and testing.
