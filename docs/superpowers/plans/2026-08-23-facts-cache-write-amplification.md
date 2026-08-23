---
status: not-started
phase: 1
updated: 2026-08-23
---

# Facts Cache Write-Amplification Reduction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Defer persistent `facts.json` writes until the existing lifecycle drain, coalesce generations per sidecar path, and preserve immediate in-process fact correctness while reducing full-file write amplification.

**Architecture:** `FileMemoryStore.GetEntityFactStore()` remains the authoritative in-process cache builder and continues to derive from current index entries whenever a sidecar is absent or invalid. It publishes the current generation immediately, records one newest pending serialized payload per persistent sidecar, and exposes that pending work through a drain participant registered with `BackgroundWork`; `BackgroundWork.DrainAsync` remains the single bounded shutdown/host/test flush boundary. Event ordering is corrected at the existing `WorkspaceSetup` installations so maintenance runs before entity typing, while ephemeral mutations only invalidate the merged in-memory generation and never enqueue a sidecar.

**Tech Stack:** C# / .NET 10, xUnit, FluentAssertions, existing `Scrinia.Core` filesystem store, local stdio `scri serve` and one-shot CLI lifecycle, existing MCP/plugin event sinks.

## Global Constraints

- Keep `facts.json`’s existing serialization, sidecar location, validity token inputs, and `EntityFactDerivation` rules unchanged.
- Do not synchronously persist `facts.json` during a facts query, mutation, MCP tool response, CLI subcommand, event, or test teardown.
- Keep at most one pending full serialized payload per sidecar path; newer generations replace older queued generations.
- Treat fact sidecars as derived restart accelerators, never as durability boundaries or authoritative state.
- Preserve immediate in-process freshness, filtered-query behavior, cross-scope aggregation, entity query semantics, and vector best-effort behavior.
- A failed or canceled flush remains dirty and retryable; a timed-out drain must never claim derived-cache durability.
- Ephemeral memories participate in generation invalidation and in-memory facts but never queue persistent sidecar writes.
- The only server surface covered by this plan is the local stdio `scri serve` process plus one-shot commands using the same process-level drain.
- Do not add an HTTP/REST server, remote store, remote CLI mode, web UI, append log, facts database, partial-file patching, or new sidecar format.
- Acceptance uses byte-counting file instrumentation and reports `facts.json` paths separately from append-only `vectors.bin` bytes; mtime-only measurements are insufficient.
- Do not add per-test `BackgroundWork` drains; use explicit drain tests only where the behavior under test requires the lifecycle boundary.

## Goal

Implement the approved design in `docs/superpowers/specs/2026-08-23-facts-cache-write-amplification-design.md` without changing source-of-truth artifact/index durability or facts query semantics.

## Context & Decisions

| Decision | Rationale | Source |
|---|---|---|
| Defer all normal sidecar writes to the existing `BackgroundWork.DrainAsync` lifecycle boundary | The measured `N=46` corpus produced approximately 65x write amplification because a small delta rewrote the full sidecar immediately; the sidecar is rebuildable | `docs/superpowers/specs/2026-08-23-facts-cache-write-amplification-design.md`, sections “Context and measured root cause” and “Decision” |
| Coalesce by sidecar path and retain the newest `(Content, Generation)` pair | Intermediate generations are superseded and must not be written | Approved spec, “Decision” and “Generation, failure, and cancellation semantics” |
| Register facts flushing with `BackgroundWork` rather than adding a second shutdown mechanism | CLI, host, and explicit test drains must share one bounded lifecycle contract, including work fired during a drain | Approved spec, “Lifecycle integration” |
| Run `MaintenanceEventSink` before `EntityTypingEventHandler` at every equivalent installation site | Maintenance can invalidate facts before typing reads the vocabulary, avoiding a superseded build | Approved spec, “MCP and plugin event pipeline” |
| Invalidate ephemeral forgets even though they have no sidecar | The merged cross-scope view includes ephemerals, so a forgotten entity must disappear immediately | Approved spec, “Ephemeral memories and forget invalidation” |

## File Map

Files to modify or create during implementation:

- Modify `src/Scrinia.Core/BackgroundWork.cs`: add the explicit drain-participant contract and integrate participant flushes into the existing bounded drain; preserve counted detached work, work fired during drain, live tokens after timeout, and fault isolation.
- Modify `src/Scrinia.Core/FileMemoryStore.cs`: replace immediate `QueueFactSidecarWrite` persistence and the store-owned retry worker with generation-aware pending payload scheduling, a drain participant, disposal/inert behavior, and ephemeral invalidation coverage.
- Modify `src/Scrinia.Core/EntityTypingEventHandler.cs` only if needed to preserve current-generation reads and failure isolation after ordering changes; do not alter entity classification semantics.
- Modify `src/Scrinia.Core/MaintenanceEventSink.cs` only if needed to preserve authoritative-first forget/update behavior and the existing `wasDeleted` value.
- Modify `src/Scrinia/Commands/WorkspaceSetup.cs`: order `MaintenanceEventSink` before `EntityTypingEventHandler` in both embedding installations and keep all fallback/plugin installations equivalent.
- Modify `src/Scrinia/Program.cs`: retain the single `BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10))` call and update only timeout wording if needed so it describes incomplete derived work rather than durable memory loss.
- Modify `tests/Scrinia.Tests/BackgroundWorkTests.cs`: cover participant registration, drain inclusion, timeout token replacement, cancellation, and disposed/inert participants.
- Modify `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`: convert immediate-write assumptions to deferred-write assertions and add coalescing, generation, failure, cancellation, disposal, and multi-scope coverage.
- Modify `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs` and `tests/Scrinia.Tests/EphemeralLifecycleTests.cs`: cover immediate ephemeral remember/append/update/forget facts and missing-forget behavior.
- Modify `tests/Scrinia.Tests/EntityTypingWiringTests.cs` and `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs`: verify maintenance-before-typing ordering and event failure isolation.
- Create `tests/Scrinia.Tests/FileWriteCounter.cs` as the focused byte-counting test helper, using the existing `TestHelpers.StoreScope` workspace pattern; instrument writes by path and byte count rather than mtimes, and exclude `vectors.bin` from facts rewrite totals.
- Modify relevant CLI lifecycle tests under `tests/Scrinia.Tests/` for one-shot shutdown, local `scri serve` shutdown wiring, and no stale process-wide flush after store disposal, using existing command/test harnesses rather than a new server surface.

## Phase 1: Drain Contract and Store Scheduling [PENDING]

### Task 1.1: Define the drain participant interface and registration lifecycle

**Files:**
- Modify: `src/Scrinia.Core/BackgroundWork.cs`
- Test: `tests/Scrinia.Tests/BackgroundWorkTests.cs`

**Interfaces:**
- Add an internal participant contract with an exact shape equivalent to:

```csharp
internal interface IBackgroundWorkParticipant
{
    Task FlushAsync(CancellationToken cancellationToken);
    bool IsDisposed { get; }
}
```

- Add an internal registration handle with an exact shape equivalent to:

```csharp
internal interface IBackgroundWorkRegistration : IDisposable { }
```

- Add an internal registration method with an exact shape equivalent to:

```csharp
internal static IBackgroundWorkRegistration Register(IBackgroundWorkParticipant participant);
```

- The participant must be counted in the same drain accounting as detached work. Registration must be inert after disposal, and registration/unregistration must be safe while `DrainAsync` is polling.

- [ ] **Step 1: Write failing participant tests** in `tests/Scrinia.Tests/BackgroundWorkTests.cs` for: a registered participant is flushed by `DrainAsync`; work registered during a drain is included; a timed-out participant receives cancellation; work registered after timeout gets a fresh live token; and disposing a registration prevents later flushes.
- [ ] **Step 2: Run the focused tests to verify they fail**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~BackgroundWorkTests --no-restore
Expected: FAIL because participant registration and flush integration are not implemented.
```

- [ ] **Step 3: Implement the minimal participant registry** in `BackgroundWork`, preserving `Fire(Func<CancellationToken, Task>, string)`, `InFlightCount`, fault logging, and the existing polling rule. Snapshot participants only for one iteration; re-check registry and in-flight state so work registered during drain is included.
- [ ] **Step 4: Implement timeout behavior** so `DrainAsync` swaps `_shutdown` for a fresh `CancellationTokenSource` before canceling the expired source, waits the existing two-second unwind grace period, and returns `false` when the original budget expires or participant work remains.
- [ ] **Step 5: Run the focused tests again** and require PASS for all existing and new `BackgroundWorkTests`.

### Task 1.2: Make `FileMemoryStore` expose one coalesced flush participant

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`

**Interfaces:**
- `FileMemoryStore` owns one registration for its lifetime and unregisters/disposes it when the store is disposed.
- Keep `internal bool HasDirtyFactCache` as the diagnostic assertion surface.
- Replace the store-owned worker entry point with an internal participant method equivalent to:

```csharp
private Task FlushPendingFactWritesAsync(CancellationToken cancellationToken);
```

- Keep the existing pending value concept, with exact semantics equivalent to:

```csharp
private sealed record PendingFactWrite(string Content, long Generation);
private readonly Dictionary<string, PendingFactWrite> _queuedFactWrites;
```

- [ ] **Step 1: Add failing tests** asserting that the first unfiltered cache build returns correct facts, writes zero `facts.json` bytes before drain, sets `HasDirtyFactCache == true`, and that one successful drain writes the final payload once and clears dirty state.
- [ ] **Step 2: Run the focused tests to verify the old behavior fails**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~EntityFactStorePersistenceTests --no-restore
Expected: FAIL on the pre-drain byte-count and deferred-dirty assertions because the current path calls `TryWriteAllText` immediately.
```

- [ ] **Step 3: Remove synchronous persistence** from `QueueFactSidecarWrite`; serialize and replace `_queuedFactWrites[path]` under `_factStoreLock`, set dirty state, and register/arm exactly one participant without starting a separate per-sidecar or per-query worker.
- [ ] **Step 4: Implement `FlushPendingFactWritesAsync`** to dequeue one path at a time, write its captured content, and on failure or `OperationCanceledException` requeue that exact payload unless a newer payload for the same path already exists. Decrement in-flight accounting in every exit path through the `BackgroundWork` contract.
- [ ] **Step 5: Clear state only under all four approved conditions:** the written payload was the pending payload, no newer payload exists for that path, no pending path remains, and the written generation equals `_factStoreGeneration`. Otherwise leave dirty state set.
- [ ] **Step 6: Run the focused persistence tests** and require PASS for deferred scheduling, final payload, and dirty-state behavior.

### Task 1.3: Make generation publication and coalescing race-safe

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`

**Interfaces:**
- Preserve `public EntityFactStore GetEntityFactStore()`.
- Preserve `private long _factStoreGeneration` as the invalidation authority.
- Preserve the rule that a build captures `gen`, returns its result to its caller, and publishes `_factStore` only when `_factStoreGeneration == gen`.

- [ ] **Step 1: Add failing tests** for mutation during a build, newer queued payload replacing an older payload, successful write with another path pending, repeated mutations before one drain, and multiple scopes before one drain.
- [ ] **Step 2: Run only those tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactStorePersistenceTests.GetEntityFactStore|FullyQualifiedName~EntityFactStorePersistenceTests.DirtyFactFlush" --no-restore
Expected: FAIL where intermediate generations are written or stale generation state clears dirty state.
```

- [ ] **Step 3: Keep `GetEntityFactStore`’s build outside `_factStoreLock`** and enqueue a serialized payload only after derivation detects a missing/stale persistent sidecar; never enqueue from a filtered derivation.
- [ ] **Step 4: Compare generations under `_factStoreLock`** before publishing or clearing state; ensure a newer queued payload cannot be overwritten by an older build or canceled attempt.
- [ ] **Step 5: Verify deterministic coalescing** by comparing the drained final sidecar bytes with a direct rebuild from final authoritative entries.
- [ ] **Step 6: Run the focused generation/coalescing tests** and require PASS.

## Phase 2: Failure, Cancellation, Disposal, and Ephemeral Semantics [PENDING]

### Task 2.1: Preserve failed and canceled payloads for fresh-token retry

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `src/Scrinia.Core/BackgroundWork.cs` only if the participant contract needs an explicit requeue outcome
- Test: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`
- Test: `tests/Scrinia.Tests/BackgroundWorkTests.cs`

**Interfaces:**
- `FlushPendingFactWritesAsync(CancellationToken)` must not convert a failed write into success.
- A canceled attempt must requeue the dequeued payload, preserve `_factStoreFlushFailed` and `_factStoreDirty`, decrement its in-flight count, and allow a later drain to retry under the fresh `_shutdown.Token`.

- [ ] **Step 1: Add failing tests** for unavailable sidecar directory/file, repair followed by a fresh drain, timeout during retry, new work after timed-out drain, and cancellation while a newer payload is coalesced in flight.
- [ ] **Step 2: Run the failure/cancellation tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactStorePersistenceTests.DirtyFactFlush|FullyQualifiedName~BackgroundWorkTests.DrainAsync" --no-restore
Expected: FAIL if a canceled payload is lost, in-flight count stays nonzero, or a later write receives the expired token.
```

- [ ] **Step 3: Implement failure retention** by leaving the newest queued payload in `_queuedFactWrites`, setting `_factStoreFlushFailed`, and allowing the participant to return or retry without blocking shutdown forever.
- [ ] **Step 4: Implement cancellation requeue** with path/generation comparison: restore the dequeued payload only when no newer payload already occupies that path; never replace a newer payload with an older canceled attempt.
- [ ] **Step 5: Verify dirty-state transitions** after failed, timed-out, repaired, and successfully retried drains.
- [ ] **Step 6: Run the focused tests** and require PASS.

### Task 2.2: Cover disposal and ephemeral invalidation

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactCacheInvalidationTests.cs`
- Test: `tests/Scrinia.Tests/EphemeralLifecycleTests.cs`
- Test: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`

**Interfaces:**
- `RememberEphemeral(string key, EphemeralEntry entry)` increments generation and invalidates the merged view.
- `TryUpdateEphemeral(string key, Func<EphemeralEntry, EphemeralEntry?> mutate)` retains its compare-and-swap behavior and invalidates only after a successful replacement.
- `ForgetEphemeral(string key)` invalidates only when `TryRemove` returns `true`; a missing forget preserves the existing not-found result and does not spuriously advance generation.
- The store’s disposal path unregisters or makes its drain participant inert; a later process-wide `BackgroundWork.DrainAsync` must not flush a closed store.

- [ ] **Step 1: Add failing tests** for ephemeral remember, append/update, warm-cache forget, filtered and unfiltered facts after forget, missing ephemeral forget, persistent forget, and disposed store flush registration.
- [ ] **Step 2: Run the focused tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityFactCacheInvalidationTests|FullyQualifiedName~EphemeralLifecycleTests|FullyQualifiedName~EntityFactStorePersistenceTests" --no-restore
Expected: FAIL where forgotten ephemeral entities remain visible or ephemeral operations queue `facts.json` writes.
```

- [ ] **Step 3: Add the invalidation tail** to `RememberEphemeral` and successful `ForgetEphemeral`; retain the existing successful-update invalidation in `TryUpdateEphemeral` and do not route updates through `RememberEphemeral`.
- [ ] **Step 4: Keep ephemeral derivation memory-only** in `GetEntityFactStore`; never call `GetStoreDirForScope` successfully for the ephemeral scope and never enqueue a sidecar for it.
- [ ] **Step 5: Register and dispose the store participant** using the store’s existing disposal implementation; ensure disposal does not mutate process-wide `BackgroundWork` state after the store is closed.
- [ ] **Step 6: Run the focused tests** and require PASS.

## Phase 3: Event Ordering and Lifecycle Wiring [PENDING]

### Task 3.1: Put maintenance before entity typing at every installation site

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:391-394`
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:437-440`
- Test: `tests/Scrinia.Tests/EntityTypingWiringTests.cs`
- Test: `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs`

**Interfaces:**
- Preserve `MemoryEventSinkContext.InstallDefault(...)` and the existing `IMemoryEventSink` callbacks.
- The equivalent embedding installations must pass sinks in this order:

```csharp
MemoryEventSinkContext.InstallDefault(
    eventHandler,
    new MaintenanceEventSink(),
    new EntityTypingEventHandler(provider));
```

- [ ] **Step 1: Add a failing wiring test** that observes one event and proves maintenance’s metadata mutation occurs before entity typing calls `GetEntityFactStore`.
- [ ] **Step 2: Add failing handler-failure tests** proving a sink failure does not hide the authoritative artifact/index mutation, invalidate the current facts view incorrectly, or prevent unrelated handlers and drain accounting from completing.
- [ ] **Step 3: Run the focused wiring tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EntityTypingWiringTests|FullyQualifiedName~EntityTypingEventHandlerTests" --no-restore
Expected: FAIL until both equivalent `WorkspaceSetup` lists use maintenance before entity typing and failure assertions are covered.
```

- [ ] **Step 4: Change both installation lists** and leave fallback installations with `MaintenanceEventSink` as their sole sink.
- [ ] **Step 5: Confirm event sequence** remains authoritative persist/index first, generation invalidation, one event publication, maintenance, entity typing, final coalesced payload scheduling, and downstream best-effort vector work.
- [ ] **Step 6: Run the focused tests** and require PASS.

### Task 3.2: Preserve one-shot and `scri serve` drain behavior

**Files:**
- Modify: `src/Scrinia/Program.cs:21-29` only if warning wording needs the approved derived-work clarification
- Inspect/modify: `src/Scrinia/Commands/ScriniaCommands.Serve.cs` only if its existing process lifecycle bypasses the program-level drain; do not add a second drain mechanism
- Test: relevant CLI lifecycle tests under `tests/Scrinia.Tests/`

**Interfaces:**
- Keep the program-level call exact in contract:

```csharp
await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
```

- The same bounded participant flush must cover one-shot command exit and local stdio `scri serve` shutdown.

- [ ] **Step 1: Add failing lifecycle tests** for one-shot exit, `scri serve` shutdown, work registered during drain, and timeout warning wording.
- [ ] **Step 2: Run the lifecycle tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Cli|FullyQualifiedName~Serve|FullyQualifiedName~BackgroundWork" --no-restore
Expected: FAIL if pending facts are not flushed before a successful bounded exit or if timeout output implies durable memory loss.
```

- [ ] **Step 3: Verify the existing single drain owns facts and event work** without adding per-command synchronous writes, a second shutdown mechanism, or a per-test drain.
- [ ] **Step 4: Update the timeout warning only as needed** to say that incomplete derived/background work may require a later rebuild, while not suggesting authoritative memory data was lost.
- [ ] **Step 5: Run the lifecycle tests** and require PASS.

## Phase 4: Byte-Counted Acceptance and Compatibility [PENDING]

### Task 4.1: Add byte-counted facts-cache regression coverage

**Files:**
- Create: `tests/Scrinia.Tests/FileWriteCounter.cs`
- Modify: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`

**Interfaces:**
- The test instrumentation must expose an exact path/byte record equivalent to:

```csharp
public sealed record FileWriteMeasurement(string Path, long Bytes);
public sealed class FileWriteCounter
{
    public IReadOnlyList<FileWriteMeasurement> Writes { get; }
    public long BytesFor(string fileName) { /* exact path-based sum */ }
}
```

- Use the repository-copy-style corpus at `N=46` memories for the amplification regression; do not count `vectors.bin` as a facts rewrite.

- [ ] **Step 1: Add failing byte-count tests** for zero pre-drain `facts.json` bytes, one final replacement per changed sidecar after drain, ten mutations collapsing to one final payload per scope, unchanged valid scopes receiving no write, filtered derivations receiving no sidecar write, and ephemeral remember/forget receiving zero sidecar bytes.
- [ ] **Step 2: Run the byte-count tests**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~EntityFactStorePersistenceTests --no-restore
Expected: FAIL against immediate full-file writes or any instrumentation that counts append-only `vectors.bin` as a facts rewrite.
```

- [ ] **Step 3: Instrument file writes by exact path and byte delta** at the test seam already used by the store workspace; report `facts.json` and `vectors.bin` separately.
- [ ] **Step 4: Assert exact acceptance limits:** pre-drain facts bytes `== 0`; successful drain has at most one complete final payload per changed sidecar; final bytes equal deterministic rebuild serialization; successful drain leaves no pending payloads and `HasDirtyFactCache == false`; failure/cancellation leaves the newest payload queued and dirty.
- [ ] **Step 5: Run the byte-count tests** and require PASS.

### Task 4.2: Run the complete verification matrix

**Files:**
- No production file changes; update tests only if a matrix case is still uncovered.

- [ ] **Step 1: Run the focused core suite**:

```text
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore
Expected: PASS.
```

- [ ] **Step 2: Run the full solution suite**:

```text
dotnet test Scrinia.sln --no-restore
Expected: PASS.
```

- [ ] **Step 3: Review the complete matrix** against the approved spec: scheduling, generation, failure, cancellation, one-shot lifecycle, local stdio serve shutdown, drain-time registration, disposed store, maintenance-before-typing ordering, vector failure isolation, ephemeral invalidation, unfiltered/filtered query semantics, token round trip, deterministic payload, and byte-counted amplification.
- [ ] **Step 4: Verify no source or test path mentions removed server concepts** and no acceptance claim relies on mtime-only observation.

## No-Deferral Constraints

- Do not retain the current synchronous `TryWriteAllText(path, content)` call in `QueueFactSidecarWrite`; the pre-drain `facts.json` byte count must be zero.
- Do not implement an append log, partial sidecar patch, alternate cache database, or background write triggered merely by a stale-sidecar discovery.
- Do not weaken generation invalidation to avoid scheduling; every authoritative persistent or ephemeral fact-changing mutation still increments generation and clears the merged view.
- Do not allow an older build, failed attempt, or canceled attempt to overwrite a newer queued `(Content, Generation)` pair.
- Do not clear dirty state until the exact four-condition success rule is satisfied.
- Do not discard a canceled payload, leave in-flight accounting positive, reuse an expired shutdown token, or let an unavailable filesystem block shutdown indefinitely.
- Do not write a sidecar for ephemeral data, and do not let `ForgetEphemeral` leave a warm stale entity visible.
- Do not reorder only one embedding installation; both equivalent `WorkspaceSetup` lists must place maintenance before entity typing.
- Do not add synchronous flushes to MCP responses, CLI commands, event callbacks, or test teardown.
- Do not count `vectors.bin` append bytes as a `facts.json` rewrite in the acceptance report.

## Self-Review

### Spec coverage

- Root cause and measured `N=46` / approximately 65x amplification: Context & Decisions and Task 4.1.
- Deferred coalescing and one pending payload per path: Tasks 1.2 and 1.3.
- Generation snapshot/publication rules: Task 1.3.
- Failure, cancellation, fresh token, retry, dirty state, and bounded drain: Tasks 1.1, 2.1, and 3.2.
- Lifecycle integration and disposed-store behavior: Tasks 1.1, 2.2, and 3.2.
- Maintenance-before-typing event ordering and vector best effort: Task 3.1.
- Ephemeral remember/update/forget invalidation and no sidecar writes: Task 2.2 and Task 4.1.
- Preserved token, serialization, cross-scope, filtered-query, and query semantics: Tasks 1.3 and 4.2.
- Non-goals and no-deferral constraints: Global Constraints and No-Deferral Constraints.
- Byte-counted acceptance and full test matrix: Phase 4.

### Placeholder scan

No `TBD`, `TODO`, “implement later”, “appropriate error handling”, or unscoped “write tests” steps remain. Every task names live paths, methods, interfaces, tests, commands, and expected outcomes.

### Type/signature consistency

The plan consistently uses `BackgroundWork.Register(IBackgroundWorkParticipant)`, `IBackgroundWorkParticipant.FlushAsync(CancellationToken)`, `FileMemoryStore.GetEntityFactStore()`, `FileMemoryStore.HasDirtyFactCache`, `PendingFactWrite(string Content, long Generation)`, `RememberEphemeral`, `TryUpdateEphemeral`, and `ForgetEphemeral`. No task relies on a removed server path or an interface introduced under a different name elsewhere in the plan.

### Contradiction check

The plan defers persistence without deferring facts visibility, preserves authoritative artifact/index writes before event publication, keeps filtered derivation sidecar-free, and treats timeout as incomplete derived work rather than data loss. It does not claim immediate sidecar durability or require a drain for ordinary in-process correctness.

## Implementation Handoff

This document is a plan only. It does not authorize source/test edits, test execution, or commits in the documentation task that created it.
