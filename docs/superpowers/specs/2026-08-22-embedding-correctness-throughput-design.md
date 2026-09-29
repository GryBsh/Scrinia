# Embedding Correctness and Throughput — No-Deferral Stream Design

**Date:** 2026-08-22  
**Status:** Approved design; implementation intentionally deferred to a subsequent plan  
**Scope:** H1, H2, M8, and the embedding, event, facts, CLI, MCP, and plugin correctness issues discovered while tracing them

## Decision in one sentence

Make `ListScoped()` the source of canonical non-ephemeral scope enumeration, give `VectorStore` safe explicit-scope probe/batch operations for vector-file health, make it the owner of compact replacement, make CLI and MCP surfaces converge below their public entry points, and make batch embedding the provider contract so core, plugin, event, and reindex paths share one correct and bounded pipeline.

## Goals

- Preserve vector correctness across store, append, forget, compact, reindex, CLI, MCP, and plugin paths.
- Enumerate canonical nested memory scopes from the memory store rather than reverse-decoding arbitrary vector directories.
- Collapse whole-memory vector replacement to one store-owned compact operation, reducing lock, open, probe, and append amplification.
- Make batch embedding the primary provider operation while retaining compatible single-input behavior for query embedding and providers that require a bounded fallback.
- Migrate every HTTP embedding provider to one batch-first resilience and response-validation model.
- Define event ordering so artifact, index, facts, and vector state are never observed in an impossible partial order.
- Keep provider failures non-destructive: a failed or short batch must not delete a memory's existing vectors.
- Make cancellation distinguishable from provider failure and ensure retries never duplicate semantic work or hide cancellation.
- Treat every embedding, event, or facts-path defect discovered during implementation as in scope unless the user explicitly approves deferral.

## Non-goals

This design does not take ownership of unrelated H3, H4, or H5 findings. It does not redesign the chunk/key layout already established by `MemoryVectorIndexer`, replace the hybrid-ranking rubric, add an approximate vector index, change provider model selection, or introduce an HTTP configuration surface. Vectors remain derived data; missing, stale, or damaged vector files remain recoverable by reindexing from memory artifacts.

## Current root causes

### H1 — compact replacement is not yet the complete correctness boundary

`VectorStore` already exposes `ReplaceMemoryVectorsAsync`, and `MemoryVectorIndexer.IndexAsync` already uses it. The remaining H1 boundary is that replacement append, retry, and compaction behavior is still entangled with the private `CompactAsync` threshold path, while callers and tests can still reason in terms of the older `RemoveAsync` plus per-vector `UpsertAsync` contract. The replacement operation must be the explicit, tested ownership boundary and preserve one-lock, one-probe, one-writer behavior. A retry after a partial append must not publish or append an unsafe second replacement, and compaction must not turn failed persistence into in-memory success.

The CLI memory commands in `src/Scrinia/Commands/ScriniaCommands.Memory.cs` call internal `ScriniaMcpTools` implementations directly (`Remember`, `Append`, `Compact`, and related methods), rather than relying on a public MCP facade call. Rules placed only in `src/Scrinia.Mcp/MemoryTools.cs` are therefore insufficient. Compact and indexing rules belong below both surfaces, at the store and event boundaries.

### H2 — nested scope discovery is duplicated and lossy

`VectorStore.GetFilePath` delegates to `ScopeToRelativeDir`, which maps separators to nested directories. Current discovery in `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs` enumerates only immediate directories beneath the embeddings root and cannot reliably account for nested scopes, legacy flat paths, or unrelated directories. A scope such as `local:topic/subtopic` can exist in the memory store without being included in stale-file probing or reindex planning.

`EmbeddingReindexer.ReindexIfStaleAsync` must enumerate canonical non-ephemeral scopes from `IMemoryStore.ListScoped()`. It must not enumerate vector directories, reverse-decode relative paths, or infer scopes from files that have no current memory. `VectorStore` instead exposes a safe explicit-scope probe/batch API. That API reuses the exact-scope `GetVectors` behavior, including locking, legacy migration, signature validation, and stale quarantine, so callers can inspect known scopes without owning path encoding or migration logic.

### M8 — batching is optional at the abstraction and incomplete in HTTP transport

`IEmbeddingProvider.EmbedBatchAsync` in `src/Scrinia.Core/Embeddings/IEmbeddingProvider.cs` is a default sequential loop over `EmbedAsync`. That preserves source compatibility but does not make batching a performance contract. `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs` owns retry, circuit-breaker, cancellation, error logging, and response parsing only for one text, so the five HTTP providers inherit single-input transport semantics.

The affected provider classes are:

- `src/Scrinia.Core/Embeddings/Providers/OpenAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/VoyageAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/AzureAiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/GoogleGeminiEmbeddingProvider.cs`
- `src/Scrinia.Core/Embeddings/Providers/OllamaEmbeddingProvider.cs`

Each provider must either send a genuine ordered batch or use an explicit, bounded compatibility adapter when its external API is single-input. No provider may silently inherit the sequential fallback after migration.

## Architecture and ownership

### `MemoryVectorIndexer` remains semantic indexing policy

`MemoryVectorIndexer.IndexAsync` continues to own availability checks, `TextChunker.SliceWindows`, `EmbeddingOptions.MaxChunksPerMemory`, two-level stored chunk indices, batch construction, vector-count validation, and retry policy. It produces a complete replacement plan and passes it to one `VectorStore.ReplaceMemoryVectorsAsync` call. It must not implement file enumeration, compaction thresholds, or provider-specific HTTP behavior.

`MemoryVectorIndexer.RemoveAsync` remains the deletion path for `OnForgottenAsync`; it must not be the first half of replacement when a replacement plan exists.

### `VectorStore` owns storage transactions and exact-scope vector access

`VectorStore.ReplaceMemoryVectorsAsync` is the storage transaction boundary for replacing one memory. It owns the per-scope semaphore, authoritative load, removal of prior entries for the name, insertion of replacement entries, operation counters, compaction decision, and one persistence pass. Its copy-on-write publication remains atomic to readers, and the SVF3 format does not gain another vector-entry field.

`VectorStore` owns exact-scope vector access and storage transactions. Its explicit-scope probe/batch API is the only way reindex and stale checks inspect vector files: it acquires the same scope lock as `GetVectors`, runs legacy migration, validates signatures, quarantines stale data, and returns health/results for the requested canonical scopes. It does not discover scopes by scanning or reverse-decoding arbitrary directories, and it does not add a manifest.

### CLI, MCP, core event, and plugin ownership

The MCP tools in `src/Scrinia.Mcp/MemoryTools.cs` and partial files, direct CLI calls in `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, core event handling in `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`, and the in-process plugin in `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` converge on the same `MemoryVectorIndexer` → `VectorStore` path.

The out-of-process plugin path negotiates `index` through `src/Scrinia/Services/McpPluginHost.cs` and `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`. `index` receives the complete content array and performs one batch-first indexing operation inside the plugin process. Legacy `upsert` remains a compatibility fallback and must identify its reduced guarantees.

## H1 — compact and surface unification

`ReplaceMemoryVectorsAsync` constructs the new live snapshot before publication, then persists one replacement batch under one scope lock. The persisted order is deletes for old entries followed by adds for new entries. The existing `>20%` deletes and `>=10` deletes compaction threshold remains unchanged unless separately approved.

The replacement operation must not call public `RemoveAsync` or `UpsertAsync` internally. A crash during append may leave a recoverable partial log; the next successful index or reindex repairs the memory. The CLI and MCP surfaces retain their current commands and response formats, while the event/index/store boundary supplies one lower-level path for both.

## H2 — canonical memory scopes with explicit vector probes

The reindex scope set comes from `IMemoryStore.ListScoped()`, filtered to canonical non-ephemeral scopes and de-duplicated deterministically. Vector probing is explicit and scoped to that set. It is safe before any scope has been loaded into `_scopeVectors`, cooperates with `MigrateLegacyScopeFileIfPresent` and signature quarantine, and never treats a vector directory without a current memory as a reindex target.

The contract is:

- enumerate canonical scopes from `IMemoryStore.ListScoped()` and exclude the literal `ephemeral` scope;
- de-duplicate duplicate scope rows before probing, with deterministic case-insensitive semantics;
- probe or batch-probe only the requested scopes through `VectorStore`'s explicit-scope API;
- reuse exact-scope `GetVectors` locking, legacy flat migration, signature validation, and stale quarantine;
- report stale/quarantined state for a requested scope without allowing it to delete or replace current vectors;
- allow a requested scope with no vector file to be reported absent and reindexed from current memory;
- ignore vector files for scopes absent from the current memory listing; they are not reverse-decoded or promoted into reindex scope;
- keep path encoding, sanitization, collision handling, and migration private to `VectorStore`; no new manifest is introduced.

`EmbeddingReindexer.ReindexAsync` obtains content from `IMemoryStore.ListScoped()`, excludes the literal `ephemeral` scope, and uses explicit vector probes only for vector-file health and stale detection. `ReindexIfStaleAsync` follows the same canonical scope list and never discovers scopes from the embeddings directory.

## M8 — batch-first provider API and HTTP migration

`IEmbeddingProvider.EmbedBatchAsync(IReadOnlyList<string>, CancellationToken)` becomes the primary operation. It preserves input order, returns one vector per input or `null`, and never returns a silently shortened array. Empty input returns an empty array without a network call. `EmbedAsync` remains for query embedding and compatibility; its default may delegate to a one-item batch, while the default batch implementation may remain a sequential fallback for third-party providers.

`MemoryVectorIndexer` validates count before replacement. A `null`, non-cancellation failure, wrong count, invalid dimension, or malformed response leaves existing vectors intact.

`ResilientEmbeddingProvider` owns shared batch transport behavior: circuit state, `RetryPolicy.ExecuteAsync`, `TransientDetector`, cancellation, response disposal, truncated error bodies via `HttpErrorBody.Truncate`, normalization via `VectorMath.L2Normalize`, observed dimensions, and ordered-result validation. Subclasses describe only their wire format.

OpenAI, Voyage AI, Azure AI, Google Gemini, and Ollama each migrate to batch equivalents. OpenAI, Voyage AI, and Azure v1 send ordered input collections; Azure classic retains deployment, API-version, and `api-key` behavior; Gemini retains its content schema and `outputDimensionality`; Ollama retains endpoint/model configuration. Where Gemini or Ollama exposes only single-input transport, the provider uses an explicit bounded adapter with the same failure semantics rather than pretending requests are batched.

Provider request limits split large input into ordered sub-batches. Every sub-batch uses the same retry and circuit rules, and results are concatenated only after all sub-batches succeed. `MaxChunksPerMemory` remains the memory-level cap.

## Events, plugin paths, reindex, and facts

- `CoreEmbeddingEventHandler.OnStoredAsync` and `OnAppendedAsync` use complete post-operation content and call `MemoryVectorIndexer.IndexAsync` once.
- `CoreEmbeddingEventHandler.OnForgottenAsync` calls `MemoryVectorIndexer.RemoveAsync` only when `wasDeleted` is true.
- `EmbeddingsPlugin` uses its plugin-local `MemoryVectorIndexer` for store, append, forget, and reindex; it does not reimplement windows or replacement loops.
- `EmbeddingReindexer.ReindexAsync` reconstructs content with `Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks`, indexes persistent memories, and uses `retryOnUnavailable: false` for bulk behavior.
- `EmbeddingReindexer.ReindexViaSinkAsync` remains a driver for plugin-owned reindexing, not a second indexing policy.
- `McpPluginHost.OnStoredAsync` prefers negotiated `index`; its `upsert` fallback remains for older plugins and cannot claim full nested-window correctness.
- `EmbeddingsTools` exposes full-content `index` and retains `upsert`, `search`, `status`, and `remove` compatibility behavior.
- `WorkspaceSetup.MaybeReindexAfterModelSwitch` and `EmbeddingReindexer.ReindexIfStaleAsync` enumerate canonical non-ephemeral scopes from `ListScoped()` and consume `VectorStore`'s explicit-scope probe/batch API.

Facts and event ordering is explicit: derive through `IMemoryStore.StoreAsync` or `AppendAsync`; persist the artifact and authoritative entry; invalidate or update the facts cache through the existing `FileMemoryStore` generation invalidation; publish the memory event once; then re-read complete content where required and index vectors. `src/Scrinia.Mcp/MemoryTools.Facts.cs` continues to use `FactsResponse` and `SelectFactStore`; unfiltered reads use `IMemoryStore.GetEntityFactStore`, and filtered reads use `EntityFactDerivation.BuildStore(store.ListScoped(...))`. Facts must never read a cache older than the artifact/index update, and vector failure must not roll back or hide durable memory/facts changes.

Dirty derived-state flush is generation-based, not update-dropping. A dirty marker clears only after the newest generation is rebuilt or failure is recorded for retry. Vector replacements coalesce per `(scope, subject)` only before work begins; the newest complete content wins, different subjects remain ordered, and failures preserve old vectors and dirty state. Facts caches publish only from complete source listings.

## Error, cancellation, and retry semantics

- Caller cancellation propagates as `OperationCanceledException` through providers, indexers, vector-store waits, and reindex loops.
- Transient HTTP failures, 429, 5xx, IO, and socket failures use `RetryPolicy` and count toward `CircuitBreaker` through `TransientDetector`.
- Non-transient 4xx errors return provider failure without opening the circuit and never delete existing vectors.
- Circuit-open results leave keyword search available and keep live notification work dirty for later retry.
- Vector-store lock/write exhaustion preserves the old snapshot and reports failure.
- Reindex is best effort per memory, but cancellation stops promptly and propagates.
- Batch count mismatch, `null`, invalid dimensions, malformed payload, reordered results, or failed sub-batch is all-or-nothing.

## Test matrix

Tests belong in the existing `tests/Scrinia.Tests` and `tests/Scrinia.Plugin.Embeddings.Tests` projects; no separate transport, UI, or endpoint test project is part of this stream.

### Vector storage and H1/H2

- Replacement covers zero, one, many, and shrinking vectors; removes stale higher windows; preserves unrelated names; and writes one logical batch.
- Readers observe either old or new immutable snapshots, never a gap.
- Nested replacement/reload uses one canonical file; `ListScoped()` supplies `local`, `local:topic`, and `local:topic/subtopic` to explicit-scope probes.
- Explicit-scope probes cover nested scopes, duplicate scope rows, scopes absent from the current memory listing, legacy flat migration, lock/temp handling, stale quarantine, signature mismatch, and collision cases without reverse-decoding vector directories.
- Compaction and replay preserve live vectors; mid-write failure preserves the published snapshot and a later index repairs it.
- Instrumented tests prove replacement does not regress into remove-plus-per-vector-upsert amplification.

### M8 providers

- Default compatibility preserves order; each HTTP provider has an expected batch wire-shape or explicitly bounded single-input adapter test.
- Every provider preserves order, dimensions, normalization, and configured model/endpoint behavior.
- Retry, circuit-open, 4xx, cancellation during request/delay/parse/splitting, truncated error bodies, malformed payloads, short/null results, reordered results, wrong dimensions, and failed sub-batches are covered.
- Request-size splitting returns one ordered result only after every sub-batch succeeds.

### Events, CLI/MCP, plugins, and reindex

- Store and append emit one indexing action with complete content; forget removes only deleted memories.
- CLI internals and MCP tools produce equivalent memory/vector/facts outcomes for remember, append, compact, forget, and failure cases.
- In-process plugin and core event paths produce the same vector key set for the same provider/options.
- Negotiated plugin `index` receives one full-content call; legacy plugins receive the compatibility sequence and warning.
- Reindex covers nested scopes, duplicate scopes, absent-current-memory scopes, missing vectors, legacy flat migration, stale quarantine/signatures, one failed memory, cancellation, and model-switch enumeration from `ListScoped()`.
- Duplicate event registration does not duplicate vector replacement.

### Facts and dirty flush

- Facts after store, append, compact, and metadata promotion observe the newest generation.
- Filtered and unfiltered facts use the correct cached or rebuilt store.
- Dirty flush coalesces repeated updates per subject without losing newest content, clearing failed work, or publishing an incomplete cache.

## Acceptance criteria

1. `EmbeddingReindexer` enumerates canonical non-ephemeral scopes from `IMemoryStore.ListScoped()`, while `VectorStore` owns explicit-scope vector probing and full-memory replacement persistence.
2. `MemoryVectorIndexer.IndexAsync` performs one batch-first provider call, or explicit bounded sub-batches, and one `ReplaceMemoryVectorsAsync` operation per memory.
3. All five HTTP providers are migrated or have explicitly tested bounded adapters with shared retry, cancellation, ordering, and validation semantics.
4. CLI, MCP, core event, in-process plugin, out-of-process plugin, and reindex paths converge on the same indexing policy.
5. Facts generation and event ordering are covered by tests; no stale-cache or duplicate-event path remains.
6. Dirty-flush coalescing reduces write amplification without clearing failed work or losing newer mutations.
7. Existing SVF3 files, legacy flat files, stale signatures, nested scopes, duplicate/absent scopes, and older plugins have defined migration behavior; no vector-directory reverse decoding or new manifest is required.
8. The complete core and plugin test matrix passes, including the build and repository gates required by `AGENTS.md`.
9. No embedding, event, or facts-path issue discovered during implementation may be parked or deferred without explicit user approval; every additional instance of an in-scope defect class must be enumerated, added to the plan and tests, and resolved in this stream.

## Self-review

- No obsolete application-layer, remote-mode, web-UI, or retired embedding-service path is part of this active design.
- All active source paths resolve to the current tree, including `src/Scrinia/Services/CoreEmbeddingEventHandler.cs` and `src/Scrinia/Services/McpPluginHost.cs`.
- No TODO, TBD, unresolved placeholder, or contradictory deferral remains.
- H1/H2/M8, CLI/MCP/plugin ownership, all five HTTP providers, event/facts ordering, dirty flush, cancellation, and comprehensive tests are explicitly covered.
