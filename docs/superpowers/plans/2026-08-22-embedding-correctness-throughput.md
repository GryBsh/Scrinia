# Embedding Correctness and Throughput Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make vector replacement, nested-scope discovery, batch embedding, event ordering, facts invalidation, dirty flushing, CLI/MCP behavior, and plugin behavior correct and bounded across the live local architecture.

**Architecture:** `MemoryVectorIndexer` remains the semantic indexing policy. `EmbeddingReindexer` enumerates canonical non-ephemeral scopes from `IMemoryStore.ListScoped()`, while `VectorStore` owns explicit-scope vector probing, migration/quarantine/locking behavior, and full-memory replacement persistence. CLI, MCP, core event handling, in-process plugin, out-of-process plugin, and reindex paths call that policy; HTTP embedding providers share batch-first resilience without introducing an HTTP application surface.

**Tech Stack:** C# / .NET 10.0 preview, local filesystem stores, MCP tool and plugin contracts, `HttpClient`, xUnit, FluentAssertions, and the existing core/plugin test projects.

**Spec:** `docs/superpowers/specs/2026-08-22-embedding-correctness-throughput-design.md`

## Global Constraints

- Keep the stream limited to H1, H2, M8, and directly discovered embedding, event, facts, CLI, MCP, and plugin defects.
- Do not add or reference an HTTP application, remote mode, web UI, retired application-layer paths, or the retired built-in embedding service.
- Use `src/Scrinia/Services/CoreEmbeddingEventHandler.cs` and `src/Scrinia/Services/McpPluginHost.cs` for the CLI-owned service paths.
- `VectorStore.ReplaceMemoryVectorsAsync` is the sole full-memory replacement boundary; do not implement replacement as public `RemoveAsync` followed by repeated `UpsertAsync` calls.
- `EmbeddingReindexer.ReindexIfStaleAsync` and related reindex paths enumerate canonical non-ephemeral scopes from `IMemoryStore.ListScoped()`; they must not discover or reverse-decode arbitrary vector directories.
- `VectorStore` exposes a safe explicit-scope probe/batch API that reuses exact-scope `GetVectors` behavior for migration, signature validation, stale quarantine, and locking; do not add a manifest.
- `MemoryVectorIndexer` owns chunking, caps, batch construction, count validation, and retry; `VectorStore` owns persistence and discovery.
- `IEmbeddingProvider.EmbedBatchAsync` preserves input order and returns exactly one vector per input or `null`; empty input performs no network call.
- Cancellation propagates as `OperationCanceledException`; provider or persistence failure never deletes existing vectors.
- Dirty markers clear only after successful derived-state rebuild or durable retry recording.
- New tests belong in `tests/Scrinia.Tests` or `tests/Scrinia.Plugin.Embeddings.Tests`.
- Do not run tests or commit as part of this documentation update; implementation workers must run the stated verification commands when executing this plan.

---

## File Map

**Modify:**

- `src/Scrinia.Core/Embeddings/VectorStore.cs` — replacement transaction and explicit-scope probe/batch API.
- `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs` — enumerate `ListScoped()`, filter canonical non-ephemeral scopes, and use explicit probes for stale checks and complete-content reindex.
- `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs` — batch validation and one replacement call.
- `src/Scrinia.Core/Embeddings/IEmbeddingProvider.cs` — primary batch contract and compatibility behavior.
- `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs` — shared batch retry, circuit, parsing, normalization, and validation.
- `src/Scrinia.Core/Embeddings/Providers/OpenAiEmbeddingProvider.cs` — ordered batch wire format.
- `src/Scrinia.Core/Embeddings/Providers/VoyageAiEmbeddingProvider.cs` — ordered batch wire format.
- `src/Scrinia.Core/Embeddings/Providers/AzureAiEmbeddingProvider.cs` — Azure v1 and classic batch behavior.
- `src/Scrinia.Core/Embeddings/Providers/GoogleGeminiEmbeddingProvider.cs` — bounded batch adapter where required.
- `src/Scrinia.Core/Embeddings/Providers/OllamaEmbeddingProvider.cs` — bounded batch adapter where required.
- `src/Scrinia/Services/CoreEmbeddingEventHandler.cs` — complete-content event indexing and deletion rules.
- `src/Scrinia/Services/McpPluginHost.cs` — negotiated `index` preference and legacy fallback warning.
- `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — CLI parity coverage and lower-level path usage where needed.
- `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` — MCP/store event ordering and compact delegation.
- `src/Scrinia.Mcp/MemoryTools.Facts.cs` — facts cache selection and generation ordering.
- `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` — plugin-local indexer ownership.
- `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs` — full-content `index` contract and compatibility operations.
- `src/Scrinia/Commands/WorkspaceSetup.cs` — model-switch reindex discovery.

**Test in existing files and neighboring test fixtures:**

- `tests/Scrinia.Tests/VectorStoreConcurrencyTests.cs`
- `tests/Scrinia.Tests/VectorStoreTornAppendTests.cs`
- `tests/Scrinia.Tests/VectorStoreScopeIsolationTests.cs`
- `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerTests.cs`
- `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerNamespacedScopeTests.cs`
- `tests/Scrinia.Tests/Embeddings/ResilientEmbeddingProviderTests.cs`
- `tests/Scrinia.Tests/CoreEmbeddingEventHandlerAppendTests.cs`
- `tests/Scrinia.Tests/CoreEmbeddingEventHandlerEphemeralAppendTests.cs`
- `tests/Scrinia.Tests/ConsolidateReembedTests.cs`
- `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs`
- `tests/Scrinia.Tests/FactsQueryTests.cs`
- `tests/Scrinia.Tests/FactsToolTests.cs`
- `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs`
- `tests/Scrinia.Plugin.Embeddings.Tests/IndexerParityTests.cs`
- `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsToolsTests.cs`
- `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginIncrementalEmbedTests.cs`
- `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginReindexTests.cs`

---

### Task 1: Make replacement and explicit-scope probing correct

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/VectorStore.cs`
- Modify: `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs`
- Test: existing vector-store and reindex test files listed in the File Map.

**Interfaces:**
- Consumes: `ScopeToRelativeDir`, `MigrateLegacyScopeFileIfPresent`, signature quarantine, and `ReplaceMemoryVectorsAsync`.
- Produces: a safe explicit-scope probe/batch operation consumed by `EmbeddingReindexer.ProbeStaleScopes` and model-switch reindex, with canonical scope enumeration remaining in `IMemoryStore.ListScoped()`.

- [ ] Verify the replacement path loads the authoritative scope once, constructs the complete immutable replacement snapshot before publication, persists deletes then adds under one scope lock, and makes compaction decisions inside the same operation.
- [ ] Remove any internal replacement use of public `RemoveAsync` and `UpsertAsync`; retain those APIs for single-entry callers.
- [ ] Implement safe explicit-scope probe and batch-probe operations that delegate to exact-scope `GetVectors` behavior, including scope locking, legacy flat migration, signature validation, stale quarantine, and absent-vector reporting.
- [ ] Change `EmbeddingReindexer.ReindexIfStaleAsync`, `ProbeStaleScopes`, and model-switch reindex callers to enumerate canonical non-ephemeral scopes from `IMemoryStore.ListScoped()`, de-duplicate them, and pass them explicitly to `VectorStore`; do not enumerate or reverse-decode vector directories and do not add a manifest.
- [ ] Add tests for zero/one/many/shrinking replacement, stale-window removal, unrelated names, immutable reader snapshots, one logical persistence batch, compaction replay, torn append recovery, nested scopes, duplicate scopes, scopes absent from the current memory listing, legacy flat migration, stale quarantine, signature mismatch, lock/temp handling, and collision behavior.
- [ ] Verify with `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~VectorStore|FullyQualifiedName~EmbeddingReindexer"` and `dotnet build Scrinia.sln`.

### Task 2: Enforce the batch-first core indexer contract

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/IEmbeddingProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/MemoryVectorIndexer.cs`
- Test: `tests/Scrinia.Tests/Embeddings/EmbeddingOptionsGuardrailTests.cs`, `tests/Scrinia.Tests/ConsolidateReembedTests.cs`, and neighboring embedding tests.

**Interfaces:**
- Consumes: `IEmbeddingProvider.EmbedBatchAsync`, `TextChunker.SliceWindows`, `EmbeddingOptions.MaxChunksPerMemory`, and `VectorStore.ReplaceMemoryVectorsAsync`.
- Produces: one ordered batch result and one non-destructive replacement attempt per memory.

- [ ] Keep `EmbedAsync` for query and compatibility callers, but document and test batch order, empty-input behavior, exact result count, and sequential fallback semantics.
- [ ] Ensure the indexer builds the complete two-level chunk plan, applies the total-memory cap, calls batch embedding once or through explicit provider sub-batches, validates count/dimensions before persistence, and calls replacement exactly once.
- [ ] Preserve old vectors on unavailable providers, null/short/wrong-dimension results, malformed results, cancellation, and failed persistence.
- [ ] Add tests for long windows, blank elements, cap truncation, shrinking replacement, cancellation, null/short batches, dimension mismatch, and provider-call count.
- [ ] Verify with `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Embedding|FullyQualifiedName~ConsolidateReembed"` and `dotnet build Scrinia.sln`.

### Task 3: Migrate the shared resilient HTTP provider layer

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/Providers/ResilientEmbeddingProvider.cs`
- Modify: all five provider files listed in the spec.
- Test: `tests/Scrinia.Tests/Embeddings/ResilientEmbeddingProviderTests.cs` and provider-focused neighboring tests.

**Interfaces:**
- Consumes: ordered text batches and provider-specific request/response schemas.
- Produces: one shared batch transport seam with retry, circuit, cancellation, truncation, normalization, dimension, count, and order validation.

- [ ] Extract batch request/parse seams from the single-input resilient path while retaining query embedding compatibility.
- [ ] Apply `RetryPolicy`, `TransientDetector`, `CircuitBreaker`, `HttpErrorBody.Truncate`, response disposal, and cancellation consistently to every batch and sub-batch.
- [ ] Treat transient infrastructure failures as breaker failures, 4xx input failures as non-breaker provider failures, and cancellation as propagated cancellation.
- [ ] Add bounded ordered sub-batch splitting for provider request limits; concatenate only after every sub-batch succeeds.
- [ ] Add shared tests for call counts, retry, breaker transitions, 4xx behavior, truncation, cancellation at request/retry/parse/split stages, malformed payloads, reordered/short/null vectors, dimensions, and all-or-nothing failure.
- [ ] Verify with `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ResilientEmbeddingProvider"` and `dotnet build Scrinia.sln`.

### Task 4: Migrate every provider wire format

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/Providers/OpenAiEmbeddingProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/Providers/VoyageAiEmbeddingProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/Providers/AzureAiEmbeddingProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/Providers/GoogleGeminiEmbeddingProvider.cs`
- Modify: `src/Scrinia.Core/Embeddings/Providers/OllamaEmbeddingProvider.cs`
- Test: provider request/response tests in `tests/Scrinia.Tests/Embeddings/`.

**Interfaces:**
- Consumes: the shared resilient batch seam from Task 3.
- Produces: explicit ordered batch transport or an explicit bounded single-input adapter for each provider.

- [ ] Make OpenAI, Voyage AI, and Azure v1 send ordered input collections and parse ordered results.
- [ ] Preserve Azure classic deployment, API-version URL construction, and `api-key` authentication while adding ordered batch behavior.
- [ ] Preserve Gemini content schema and `outputDimensionality`; use a bounded adapter if the configured API cannot accept multiple contents.
- [ ] Preserve Ollama endpoint/model behavior; use a bounded adapter if the deployed API is single-input.
- [ ] Add one transport-call-count and order/dimension/normalization test per provider, plus provider-specific error and request-limit cases.
- [ ] Verify with `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Embedding"` and `dotnet build Scrinia.sln`.

### Task 5: Unify core events, CLI, MCP, and facts ordering

**Files:**
- Modify: `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/MemoryTools.Facts.cs`
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs`
- Test: event, CLI, facts, and cache-invalidation files in the File Map.

**Interfaces:**
- Consumes: durable artifact/index mutation, `MemoryVectorIndexer`, `IMemoryEventSink`, `GetEntityFactStore`, `EntityFactDerivation.BuildStore`, and generation invalidation.
- Produces: one complete-content indexing event after durable memory state, correct facts generation, and equivalent CLI/MCP outcomes.

- [ ] Ensure store and append publish one event with complete post-operation content, and forget removes vectors only when `wasDeleted` is true.
- [ ] Keep compact and indexing rules below both the direct CLI internals and MCP tool entry points; do not assume the CLI calls a public MCP facade.
- [ ] Establish the order artifact/entry persistence → facts invalidation → one event publication → vector indexing, without rolling back durable memory/facts state on vector failure.
- [ ] Ensure filtered and unfiltered facts select the correct store and never publish a cache from an incomplete listing.
- [ ] Add generation-based dirty tracking that clears only after successful rebuild or recorded retry, and coalesces newest content per `(scope, subject)` without reordering different subjects.
- [ ] Add parity tests for remember, append, compact, forget, metadata promotion, provider failure, dirty coalescing, and duplicate event registration.
- [ ] Verify with `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CoreEmbeddingEventHandler|FullyQualifiedName~Facts|FullyQualifiedName~CliSurfaceConformance|FullyQualifiedName~EntityFactCache"` and `dotnet build Scrinia.sln`.

### Task 6: Converge in-process and out-of-process plugin ownership

**Files:**
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`
- Modify: `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`
- Modify: `src/Scrinia/Services/McpPluginHost.cs`
- Modify: `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs`
- Test: plugin tests in `tests/Scrinia.Plugin.Embeddings.Tests/` listed in the File Map.

**Interfaces:**
- Consumes: `MemoryVectorIndexer`, complete content arrays, negotiated `index`, and legacy `upsert` capability.
- Produces: identical vector keys and replacement semantics for core and plugin-owned indexing, with an explicit compatibility warning for old plugins.

- [ ] Route plugin store, append, forget, and reindex paths through plugin-local `MemoryVectorIndexer` and remove duplicate window/replace logic.
- [ ] Make `EmbeddingsTools.index` accept complete content and preserve `upsert`, `search`, `status`, and `remove` compatibility behavior.
- [ ] Make `McpPluginHost.OnStoredAsync` prefer `index`, keep the legacy fallback, and emit one reduced-guarantee warning per connection.
- [ ] Keep `ReindexViaSinkAsync` as a driver only and ensure regular reindex reconstructs `Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks`.
- [ ] Add tests for negotiated index, legacy fallback, warning deduplication, plugin/core vector parity, nested windows, forget semantics, reindex failures, cancellation, and model-switch discovery.
- [ ] Verify with `dotnet test tests/Scrinia.Plugin.Embeddings.Tests` and `dotnet build Scrinia.sln`.

### Task 7: Run the complete no-deferral verification matrix

**Files:**
- Modify: any test files identified by Tasks 1–6; no unrelated source files.

- [ ] Review every changed source path for a second instance of each defect class: arbitrary vector-directory enumeration, remove-plus-upsert replacement, sequential provider transport, duplicate event publication, stale facts generation, and premature dirty clearing.
- [ ] Add a test for every enumerated instance before closing the stream; do not defer an in-scope instance without written user approval.
- [ ] Run `dotnet test tests/Scrinia.Tests`.
- [ ] Run `dotnet test tests/Scrinia.Plugin.Embeddings.Tests`.
- [ ] Run `dotnet build Scrinia.sln`.
- [ ] Confirm the vector, provider, event, facts, CLI/MCP, plugin, reindex, cancellation, and dirty-flush acceptance criteria from the spec are all represented by passing tests.

## Self-review checklist

- [ ] Every path in this plan exists in the current tree or is an explicitly modified test file already present in the File Map.
- [ ] Search this plan and the spec for retired application-layer paths, remote mode, web UI, the retired built-in embedding service, `TODO`, `TBD`, and placeholder wording; expected result is no active architecture/path reference.
- [ ] Confirm the plan covers H1/H2/M8, all five HTTP providers, CLI/MCP/plugin ownership, facts/event ordering, dirty flush, cancellation, and comprehensive tests.
- [ ] Confirm no step contradicts the non-destructive replacement rule or the no-deferral acceptance criterion.
