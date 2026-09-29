# IMP-04 EntityFactMaterializer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract entity-fact construction into one internal stateful owner without changing facts
results, persistence, storage format, or public APIs.

**Architecture:** `EntityFactMaterializer` owns the merged `EntityFactStore` cache, generation,
invalidation and dirty state, sidecar queue/coalescing/retry/flush, background participant and its
registration, and disposal. `FileMemoryStore` owns only scope/index/recovery integration and
persisted-facts fast-path validation. It passes selected artifacts and validated persisted results
to the materializer, invokes invalidation after mutations, and calls `Dispose()`; it retains no
second registration or lifecycle state. `EntityFactDerivation` remains the façade.

**Refs:** `docs/superpowers/specs/2026-08-27-imp-04-entity-fact-materializer-design.md`;
`src/Scrinia.Core/Search/EntityFactDerivation.cs`;
`src/Scrinia.Core/Search/EntityFactSidecar.cs`;
`src/Scrinia.Core/FileMemoryStore.cs`.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, `Scrinia.Core.Search`, existing
`ScopedArtifact`, `MemoryEntities`, `EntityFactStore`, `EntityFactSidecar`, and background-work
types.

## Global Constraints

- Modify no public API, storage format, sidecar path convention, or serialization contract.
- The collaborator is `internal sealed class EntityFactMaterializer : IDisposable` and owns all
  derived-fact cache and sidecar lifecycle state.
- Use exactly the constructor, `Materialize` boundary, records, delegates, properties, and methods
  specified in the companion design.
- `Materialize` accepts `IReadOnlyList<ScopedArtifact>` plus
  `IReadOnlyDictionary<string, PersistedFactResult>`. A dictionary entry is usable only because
  `FileMemoryStore` has already performed sidecar parsing, format/version validation, token
  validation, and recovery gating; an absent key means derive that scope.
- Do not catch or wrap derivation exceptions, and do not turn an unreadable host result into an
  empty fact store.
- Do not leave cache, generation, dirty queue, participant registration, flush, or disposal policy
  duplicated in `FileMemoryStore`.
- **Ref:** companion design for the exact boundary and failure/compatibility constraints.

---

## Dependencies / TDD order

- Task 1 characterizes current behavior without introducing the new boundary.
- Task 2 defines the exact stateful collaborator API, writes its contract tests first, then
  implements the collaborator and derivation delegation.
- Task 3 updates store integration only after the collaborator contract is green.
- Task 3 must complete before IMP-01, release/version documentation, and final integration
  verification.
- Each task has a complete file list and commit list below. No task may reference a seam introduced
  by a later task.

### Task 1: Characterize the existing transformation

**Files:**

- Create: `tests/Scrinia.Tests/EntityFactMaterializerTests.cs`
- Read: `src/Scrinia.Core/Search/EntityFactDerivation.cs`
- Read: `src/Scrinia.Core/Search/EntityFactStore.cs`

**Interfaces:**

- Consumes: existing `EntityFactDerivation.Derive(ScopedArtifact)` and
  `BuildStore(IEnumerable<ScopedArtifact>)` behavior.
- Produces: executable expectations for typed attributes, legacy `type:value` keywords, duplicate
  values, dates, co-occurrences, empty input, stable ordering, and selected scope membership.

- [ ] **Step 1: Write characterization tests** with `ArtifactEntry`/`ScopedArtifact` fixtures and
  assert exact entities, facts, timelines, spans, memory membership, neighbors, and ordering.
- [ ] **Step 2: Run the focused tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactMaterializerTests"`

Expected: PASS against the current implementation. Correct characterization mismatches before
continuing; do not add lifecycle or failure-injection tests in this task.

- [ ] **Step 3: Commit only the characterization file.**

```bash
git add tests/Scrinia.Tests/EntityFactMaterializerTests.cs
git commit -m "test: characterize entity fact materialization"
```

### Task 2: Define and implement the stateful materializer

**Files:**

- Create: `src/Scrinia.Core/Search/EntityFactMaterializer.cs`
- Modify: `src/Scrinia.Core/Search/EntityFactDerivation.cs`
- Modify: `tests/Scrinia.Tests/EntityFactMaterializerTests.cs`
- Read: `src/Scrinia.Core/Search/EntityFactSidecar.cs`
- Read: `src/Scrinia.Core/BackgroundWork.cs`

**Interfaces:**

- Constructor: `EntityFactMaterializerSource source`,
  `Func<string, string?> resolveSidecarPath`, `Func<string, string?> readSidecar`,
  `FactSidecarWriter writeSidecar`, and
  `Func<IBackgroundWorkParticipant, IBackgroundWorkRegistration> registerParticipant`.
- Materialization: `Materialize(IReadOnlyList<ScopedArtifact> items,
  IReadOnlyDictionary<string, PersistedFactResult> persistedFacts) : EntityFactStore`.
- Lifecycle: `CachedStore`, `Generation`, `IsDirty`, `DirtyQueue`, `FlushParticipant`, `Invalidate`,
  `FlushAsync(CancellationToken)`, and idempotent `Dispose()`.
- Supporting types: `PersistedFactResult(string Token, IReadOnlyList<MemoryEntities> Memories)`,
  `PendingFactWrite(string Content, long Generation)`, `EntityFactMaterializerSource`, and
  `FactSidecarWriter`, each defined exactly once and internal.
- Derivation: the existing one-argument façade delegates to the production materializer; any
  internal test seam invokes the supplied source without changing public APIs.

- [ ] **Step 1: Add contract tests before implementation** named
  `EntityFactMaterializer_CachesBuiltStoreUntilInvalidated`,
  `EntityFactMaterializer_UsesValidatedPersistedFactsPerScope`,
  `EntityFactMaterializer_TracksGenerationAndDirtyQueue`,
  `EntityFactMaterializer_FlushParticipantDrainsQueuedSidecar`,
  `EntityFactMaterializer_PropagatesOriginalSourceException`, and
  `EntityFactMaterializer_DisposeIsIdempotent`. Tests may reference only the exact boundary above.
- [ ] **Step 2: Define the exact internal types and members** before implementing behavior or
  writing the failure assertions.
- [ ] **Step 3: Run the focused tests** and record the expected red contract tests while the
  collaborator is incomplete; characterization tests must remain green.
- [ ] **Step 4: Implement materialization** by grouping supplied entries by scope, selecting an
  admitted `PersistedFactResult` when present, deriving absent scopes through `source`, preserving
  existing normalization/order/distinct/entityless behavior, selecting `CachedStore` only for the
  current canonical input and generation, and publishing only a complete result. A filtered input
  must not replace the canonical merged cache.
- [ ] **Step 5: Implement lifecycle ownership**: generation-safe cache publication; invalidation;
  path-keyed generation-aware queueing; participant registration; flush retry/cancellation rules;
  and idempotent disposal of the materializer-owned registration.
- [ ] **Step 6: Complete `EntityFactDerivation` delegation** without changing exception identity or
  observable fact results.
- [ ] **Step 7: Run focused and existing fact tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactMaterializerTests|FullyQualifiedName~EntityFactDerivationTests|FullyQualifiedName~EntityFactStore"`

Expected: PASS with unchanged fact values, ordering, ambiguity, exception identity, cache and
generation behavior, queue/flush behavior, and idempotent disposal.

- [ ] **Step 8: Commit only Task 2 files.** Do not include `FileMemoryStore.cs`.

```bash
git add src/Scrinia.Core/Search/EntityFactMaterializer.cs src/Scrinia.Core/Search/EntityFactDerivation.cs tests/Scrinia.Tests/EntityFactMaterializerTests.cs
git commit -m "refactor: extract entity fact materializer"
```

### Task 3: Integrate the host and prove full/filtered boundaries

**Files:**

- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/EntityFactMaterializerTests.cs`
- Read: `src/Scrinia.Core/IMemoryStore.cs`
- Read: `src/Scrinia.Mcp/MemoryTools.Facts.cs`
- Read: `src/Scrinia.Core/Search/SearchPipeline.cs`

**Interfaces:**

- Consumes: existing scope/index selection, recovery barriers, sidecar path resolution, sidecar
  parsing/token validation, and `EntityFactMaterializer.Materialize`.
- Produces: host calls that pass selected entries plus only validated per-scope
  `PersistedFactResult` values; mutation paths call materializer invalidation; store disposal calls
  materializer disposal.

- [ ] **Step 1: Replace store-owned lifecycle state with one materializer field.** Pass the existing
  narrow source, sidecar resolver, safe reader, writer, and participant-registration delegates to
  the materializer constructor. Remove the store's duplicate fact participant, registration, cache,
  generation, dirty queue, and flush ownership. This step modifies only
  `src/Scrinia.Core/FileMemoryStore.cs` and `tests/Scrinia.Tests/EntityFactMaterializerTests.cs`.
- [ ] **Step 2: Preserve the persisted-facts fast path.** After existing recovery gates and scope
  resolution, have `FileMemoryStore` obtain, parse, version-check, and token-check each sidecar;
  pass admitted results keyed by scope to `Materialize`. Do not let the materializer decide whether
  an unreadable or invalid sidecar is valid.
- [ ] **Step 3: Forward selected entries and mutations.** Full and filtered paths pass exactly the
  resolver-selected entries; `Upsert`/`Remove` invalidation calls the materializer; no store-local
  cache, generation, dirty marker, queue, or flush remains.
- [ ] **Step 4: Wire disposal.** The store calls `EntityFactMaterializer.Dispose()` from its
  existing disposal path. The materializer, not the store, disposes its registration; disposal is
  safe if invoked more than once by surrounding teardown.
- [ ] **Step 5: Add integration tests** for local topics, excluded topics, ephemeral entries,
  legacy entries, empty stores, validated sidecar reuse, invalid/missing sidecar rebuild, and the
  absence of new storage formats or changed result shape.
- [ ] **Step 6: Run the complete fact suite.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFact"`

Expected: PASS with existing entities/profile/intersect, persistence, invalidation, legacy,
filtered, ephemeral, derivation, background flush, and disposal behavior unchanged.

- [ ] **Step 7: Commit only Task 3 files.** Do not amend the materializer commit.

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/EntityFactMaterializerTests.cs
git commit -m "refactor: route store facts through materializer"
```

## Self-review

- Every task lists its complete Files, Interfaces, TDD order, and commit list.
- The spec and plan use one stateful owner and one exact boundary throughout.
- Persisted-facts validation/recovery gating is explicitly host-owned; cache selection, generation,
  invalidation, queueing, flushing, registration, and disposal are explicitly materializer-owned.
- No task asks `FileMemoryStore` to retain or dispose the materializer's registration.
- No deferred decision marker or unspecified implementation step appears.
- No public API, storage format, serialization, sidecar path convention, or write-timing change is
  proposed.
