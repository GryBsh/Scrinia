---

# P5 — God-Store Seam Extraction: Maintenance Page-Service Cache — Design Spec

> **Phase:** P5 (god-store seam extraction — maintenance page-service cache)
> **Status:** Approved design — implementation is a separate, gitignored plan.
> **Parent specs:** P1–P4 (admission, lock, index-cache, and seam-extraction conventions). This phase extracts the maintenance page-service cache seam, the fifth seam family out of `FileMemoryStore`.

---

## Context

`FileMemoryStore` (5,126 lines) is a god store. P4 extracted the admission/lock and index-cache seams into `ScopeLockManager` and `ScopeIndexCache`. This phase extracts the maintenance page-service cache seam — the lazily-created, per-`(storeDirectory, scope)` `MaintenanceDiscoveryPageService` cache — into one small internal owning type `MaintenancePageServiceCache`, the direct maintenance analog of P4's `ScopeIndexCache`.

The maintenance page-service cache today lives in `FileMemoryStore.cs` as:

| Artifact | Location | Kind |
|---|---|---|
| `MaintenancePageServiceCreationBarrierForTest` (`Action?`) | :117 | test-only barrier property |
| `_maintenanceRuns` (`MaintenanceRunRegistry`) | :141 | collaborator (stays in store) |
| `_recoveryDiscovery` (`RecoveryDiscoveryCoordinator`) | :140 | collaborator (stays in store) |
| `_maintenancePages` (`ConcurrentDictionary<string, MaintenanceDiscoveryPageService>`, `StringComparer.Ordinal`) | :142 | backing field |
| `_maintenanceLifecycleGate` (`object`) | :143 | backing field |
| `_maintenanceDisposed` (`bool`) | :144 | backing field |
| `GetMaintenancePageService(scope, storeDirectory)` | :814 | producer primitive |
| `DiscoverRecoveryPage` / `ContinueRecoveryPage` / `DiscoverMaintenancePage` / `ContinueMaintenancePage` | :796–812 | passthrough delegators (stay in store) |

---

## Decision / Alternatives

**Decision: move the page-service cache state (`_maintenancePages`, `_maintenanceLifecycleGate`, `_maintenanceDisposed`, `MaintenancePageServiceCreationBarrierForTest`) and the `GetMaintenancePageService` producer into one new internal owning type `MaintenancePageServiceCache`, injecting `_recoveryDiscovery` and `_maintenanceRuns` as constructor collaborators. `FileMemoryStore` keeps exactly one `readonly` instance and delegates; the four discovery passthroughs and the recovery passthrough stay in store as thin delegators.**

Alternatives considered and rejected:

1. **Keep the cache inline, extract nothing.** Rejected: the god-store reduction is never completed; the maintenance cache stays entangled with the write path.
2. **Move the four discovery passthroughs into the cache too.** Rejected: they're thin delegators pinned by `B5MaintenanceDiscoveryTests` via `store.DiscoverMaintenancePage(...)`; moving them would re-point test call sites and split the maintenance region. Keep them in store.
3. **Extract the deletion layer (`ExecuteMaintenanceScopeAsync`/`UnderLock`/`ToPlannerCandidate`/`IsStillDeletable`/`TryDeleteBounded`).** Rejected: those methods call `WithExclusiveScopeAdmissionAsync` (P4's admission seam) and `RecoverScopeUnsafe` (recovery), so they're entangled with frozen seams; out of scope for P5.
4. **An interface (`IMaintenancePageServiceCache`).** Rejected: `FileMemoryStore` is the only owner and caller; no factory or DI surface needs a swap. The cache is internal implementation detail.

**Guiding rule — behavior-preserving, one clean pass.** Every moved line is byte-identical to its original semantics. No new public API, no new JSON context entry, no new recovery bypass. The four discovery passthroughs and the recovery passthrough keep their exact signatures and call sites; only the cache state and the `GetMaintenancePageService` producer move.

---

## Architecture + Components

One new internal type, one new file, in namespace `Scrinia.Core`. `FileMemoryStore` holds one `readonly` instance and delegates.

### `MaintenancePageServiceCache` — page-service cache ownership

**File:** `src/Scrinia.Core/MaintenancePageServiceCache.cs`
**Namespace:** `Scrinia.Core`
**Visibility:** `internal` (type) + `internal` (methods/property)

Owns the maintenance page-service cache state:

```csharp
// Fields (moved verbatim from FileMemoryStore):
internal Action? MaintenancePageServiceCreationBarrierForTest { get; set; }   // :117
private readonly ConcurrentDictionary<string, MaintenanceDiscoveryPageService> _maintenancePages = new(StringComparer.Ordinal); // :142
private readonly object _maintenanceLifecycleGate = new(); // :143
private bool _maintenanceDisposed; // :144
```

**Constructor collaborators (injected, not moved):**

```csharp
internal MaintenancePageServiceCache(RecoveryDiscoveryCoordinator recoveryDiscovery, MaintenanceRunRegistry maintenanceRuns)
{
    _recoveryDiscovery = recoveryDiscovery;
    _maintenanceRuns = maintenanceRuns;
}
```

**Moved primitive** — `GetMaintenancePageService(scope, storeDirectory)` (:814–834), body byte-identical, now using the injected `_recoveryDiscovery` and `_maintenanceRuns` instead of closure-over-store-fields. The `MaintenancePageServiceCreationBarrierForTest?.Invoke()` (:816), the `lock(_maintenanceLifecycleGate)` (:818), the `ObjectDisposedException.ThrowIf(_maintenanceDisposed, this)` (:820), the `TryGetValue`/`TryAdd` race handling (:821–830) are all moved verbatim.

**New teardown primitive** — `MaintenanceDiscoveryPageService[] TakeDisposalSnapshot()`: performs the gated capture that `Dispose` currently does inline (:5046–5052) — the `lock(_maintenanceLifecycleGate)` + `if (_maintenanceDisposed) return Array.Empty(...)` + `_maintenanceDisposed = true` + `return _maintenancePages.Values.ToArray()` + `_maintenancePages.Clear()`. Returns the collected services so the store can dispose them in its existing `firstFailure`-collecting loop.

### Wiring into `FileMemoryStore`

- Add `private readonly MaintenancePageServiceCache _maintenancePageServiceCache;`.
- Construct in the `internal FileMemoryStore(...)` constructor (near :294–295, alongside the P4 manager constructions):
  ```csharp
  _maintenancePageServiceCache = new MaintenancePageServiceCache(_recoveryDiscovery, _maintenanceRuns);
  ```
- Route the four discovery passthroughs (:796–812) through the cache: `GetMaintenancePageService(scope, storeDirectory)` → `_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory)`. `DiscoverMaintenancePage`/`ContinueMaintenancePage` still build `new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory), _maintenanceRuns)`.
- Keep `DiscoverRecoveryForScope` (:792) in store — it is a `_recoveryDiscovery` passthrough, not part of the page-service cache seam.
- Keep the maintenance-page teardown in `Dispose` (:5045–5064), but replace the inline `lock(_maintenanceLifecycleGate)` capture with `var maintenancePages = _maintenancePageServiceCache.TakeDisposalSnapshot();`. The collected services are still disposed in the store's existing `foreach`/`firstFailure` loop, preserving exact ordering.
- Expose `MaintenancePageServiceCreationBarrierForTest` on the store as a forwarding `internal` property so existing test assignment and reads remain valid:
  ```csharp
  internal Action? MaintenancePageServiceCreationBarrierForTest
  {
      get => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest;
      set => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest = value;
  }
  ```

---

## Data Flow

**Discovery passthroughs (before → after):**

```
DiscoverRecoveryPage
   │  GetMaintenancePageService(scope, dir).ReadFirstPage(...)
   ▼  _maintenancePageServiceCache.GetMaintenancePageService(scope, dir).ReadFirstPage(...)
ContinueRecoveryPage  ─▶  _maintenancePageServiceCache.GetMaintenancePageService(scope, dir).ReadNextPage(cursor)
DiscoverMaintenancePage ─▶ new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(...), _maintenanceRuns).Start(...)
ContinueMaintenancePage ─▶ new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(...), _maintenanceRuns).Continue(...)
```

`GetMaintenancePageService` is unchanged except `_recoveryDiscovery` and `_maintenanceRuns` are constructor-injected rather than closure-over-store-fields. `TakeDisposalSnapshot()` performs the capture the old inline `lock(_maintenanceLifecycleGate)` block did, returning the `MaintenanceDiscoveryPageService[]` the store then disposes.

---

## Error Handling

- **`ObjectDisposedException` on acquire after dispose.** `GetMaintenancePageService` keeps the `ObjectDisposedException.ThrowIf(_maintenanceDisposed, this)` guard moved verbatim into the cache. No new failure mode.
- **`firstFailure` rethrow in `Dispose`.** `FileMemoryStore.Dispose` keeps its existing `firstFailure` collection over the fact-materializer, maintenance-page services, and maintenance-run disposals. Only the capture mechanism changes: the gated `lock(_maintenanceLifecycleGate)` + `ToArray` + `Clear` now runs inside `MaintenancePageServiceCache.TakeDisposalSnapshot()`. Ordering is therefore: gated maintenance-page capture (via the cache) → scope lock manager → fact materializer → maintenance pages → maintenance run → rethrow.
- **Test barrier.** `MaintenancePageServiceCreationBarrierForTest?.Invoke()` (:816) still runs exactly once per page-service creation, now guarded by the cache's own lifecycle gate.

---

## Testing Matrix

| Test family | Pins |
|---|---|
| `B5MaintenanceDiscoveryTests` (`MaintenancePageServices` reflection + barrier-dispose test) | `_maintenancePages` now lives on the cache owner; the reflection helper navigates two levels. The forwarding barrier property preserves `store.MaintenancePageServiceCreationBarrierForTest = ...` assignment. |
| **Full gate:** `dotnet test Scrinia.sln` | 3,750+ `Scrinia.Tests`, 272 `Retrieval.Benchmarks`, 63 `Embeddings`. Any failure is a behavior regression — do not sweep it under the rug. |

**Test-only, behavior-preserving edits (internal test files only):**

- `tests/Scrinia.Tests/B5MaintenanceDiscoveryTests.cs` `MaintenancePageServices` (helper at :594–601): it currently does
  `typeof(FileMemoryStore).GetField("_maintenancePages", BindingFlags.Instance | BindingFlags.NonPublic)`.
  After extraction `_maintenancePages` lives on the cache owner, so navigate two levels: get the
  `FileMemoryStore._maintenancePageServiceCache` field (instance, non-public), then `GetField("_maintenancePages")`
  on THAT manager instance, and cast through `IDictionary` exactly as before. Behavior identical — the
  `MaintenanceDiscoveryPageService[]` returned is unchanged.

---

## Frozen Constraints

These carry from P1–P4 and remain non-negotiable for P5:

- **NMP/2 unchanged.** No format revision.
- **`.scrinia/` layout unchanged.** No new top-level directory or memory shape.
- **CLI single-file surface.** `scri serve` remains the only server surface; no new server mode.
- **MCP 12 + 11 disjoint.** The twelve memory tools + `facts` are untouched; P5 adds no tool.
- **Source-gen JSON only.** No new `[JsonSerializable]`. `MaintenancePageServiceCache` and its moved types
  serialize nothing, so **no `*JsonContext` entry is added** (`FileStoreJsonContext` untouched).
- **Recovery bypasses untouched.** `DiscoverMemoriesRecursive` / `ParseScopeLabel` (and all other
  recovery helpers) are unchanged.
- **No new public API.** The cache is `internal`; the forwarding barrier property is `internal`;
  `TakeDisposalSnapshot` is `internal`.
- **Every new test uses `TestHelpers.StoreScope` isolation.**
- **No public member signature change.** P5 changes NO public member signature of
  `FileMemoryStore` / `IMemoryStore` / `IPluginStore` / `IAddressResolver`, and NO member of the
  three TransactionCoordinator family types or their interfaces.

### Explicit do-not-touch (P5 anchors)

`DiscoverRecoveryForScope` (signature + body), the maintenance deletion layer (`ExecuteMaintenanceScopeAsync` at :849, `ExecuteMaintenanceScopeUnderLock`, `RecoveryRetainedExecution`, `ToPlannerCandidate`, `IsStillDeletable`, `TryDeleteBounded`, `SafeRecoveryRelativePath`), `WithExclusiveScopeAdmissionAsync` (signature + body + `RecoverScopeUnsafe` call), `WithExclusiveLock`, all recovery (`RecoverScopeUnsafe`, `WithRecoveryReadBarrier`, `RecoverAppendUnsafeAsync`, `DiscoverRecoveryForScope`), the three TransactionCoordinator family types + interfaces, `FileStoreJsonContext`, all public `FileMemoryStore`/`IMemoryStore`/`IPluginStore`/`IAddressResolver` members, NMP/2.

---

## Out of Scope

- **The maintenance deletion layer.** Entangled with the frozen admission (P4) and recovery seams; a later phase.
- **Scope/topic discovery + listing/search composition.** A larger, recovery- and migration-gate-coupled seam family (`_cachedTopics`/`DiscoverTopics`/`MemoriesRoot` migration gate/`ResolveReadScopes` plus `ListScoped`/`AnyScoped`/`ListEphemeral`/`GatherTopicInfos`/`SearchAll`); scoped as a distinct phase, not crammed into this one clean pass.
- **Facts materializer.** Already extracted into `EntityFactMaterializer`; only thin store-side glue remains.
- **Any public API.** No new public members on `FileMemoryStore`, `IMemoryStore`, `IPluginStore`, or `IAddressResolver`. No new JSON context entry. No new MCP tool.

---
