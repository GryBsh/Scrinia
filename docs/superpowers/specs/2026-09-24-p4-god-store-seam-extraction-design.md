# P4 — God-Store Seam Extraction: Design Spec

> **Phase:** P4 (god-store seam extraction)
> **Status:** Approved design — implementation is a separate, gitignored plan.
> **Parent specs:** P1 / P2 / P3 (admission, lock, and recovery seam extraction). This phase is the fourth and final extraction out of `FileMemoryStore`.

---

## Context

`FileMemoryStore` (5,207 lines) is a god store. Two of its responsibilities — the
per-scope **admission + lock** machinery and the in-process **index cache** — live inline as
nested types, backing fields, and primitives that are interleaved with the write path. Three of
the four seam families were extracted in P1–P3; this phase extracts the remaining two.

The admission/lock seam today lives in `FileMemoryStore.cs` as:

| Artifact | Location | Kind |
|---|---|---|
| `private sealed class AdmissionGate : IDisposable` | :845 | nested type |
| `private ReaderWriterLockSlim GetIndexLock(string scope)` | :783 | primitive |
| `private AdmissionGate AcquireAdmissionGate(string scope)` | :819 | primitive |
| `private void ReleaseAdmissionGate(string scope, AdmissionGate gate)` | :830 | primitive |
| `_admissionLocks` (`ConcurrentDictionary<string, AdmissionGate>`) | :148 | backing field |
| `_indexLocks` (`ConcurrentDictionary<string, ReaderWriterLockSlim>`) | :147 | backing field |
| `_admissionLifecycleGate` (`object`) | :149 | backing field |
| `_admissionDisposed` (`bool`) | :150 | backing field |

The index-cache seam today lives in `FileMemoryStore.cs` as:

| Artifact | Location | Kind |
|---|---|---|
| `internal sealed class CachedIndex` | :194 | nested type |
| `_indexCache` (`ConcurrentDictionary<string, CachedIndex>`) | :151 | backing field |
| `_indexCache[scope] = new CachedIndex(...)` | :2034, :2130 | install-on-write |
| `_indexCache.TryRemove(scope, out _)` | :1941, :1957, :2203, :4955 | drop |
| `_indexCache.TryGetValue(scope, out var cachedIndex)` | :2104, :2184 | read |
| `_indexCache.Clear()` | :1928 | bulk drop |

The three TransactionCoordinator family types (`WholeMemoryTransactionCoordinator`,
`FileEntityTransactionCoordinator`, `MultiMemoryTransactionCoordinator`) are already separate
files with their own seams; the recovery/discovery/maintenance/write-path members of
`FileMemoryStore` are explicitly **out of scope** for P4. P4 extracts **only** the admission/lock
and index-cache seams into two small internal owning types.

### Why now, and why this shape

The god store has four seam families: recovery, admission/lock, index-cache, and the
TransactionCoordinator family. The first three phases drained the outer two. What remains inside
`FileMemoryStore` are the admission/lock primitives and the cache primitives — each a coherent,
self-contained responsibility that can own its own fields, type, and teardown. Extracting them
completes the god-store reduction and removes the last inline `AdmissionGate` / `CachedIndex`
nested types.

---

## Decision / Alternatives

**Decision: extract the admission/lock state into one new type (`ScopeLockManager`) and the
index-cache state into another (`ScopeIndexCache`), each owning its fields, its promoted nested
type, and its primitives. `FileMemoryStore` keeps exactly one `readonly` instance of each and
delegates.**

Alternatives considered and rejected:

1. **`ConcurrentDictionary`-scoped nesting, no new type.** Keep `AdmissionGate` / `CachedIndex`
   nested and add nothing. Rejected: the nested types and their primitives stay entangled with
   `FileMemoryStore`'s write path; the god-store reduction is never completed.

2. **A single `ScopeSeams` or `StoreInternals` mega-type** owning both admission/lock and
   index-cache. Rejected: two unrelated responsibilities (lifecycle-gated admission gates vs.
   copy-on-write cache install) in one file violates one-responsibility-per-file. The managers
   have different lifecycles, different teardown ordering, and different test pins.

3. **An interface (`IScopeLockManager` / `IScopeIndexCache`) with `FileMemoryStore` holding the
   reference.** Rejected: `FileMemoryStore` is the only owner and caller; no participant
   factory or DI surface needs a swap. An interface would add indirection with zero behavioral
   benefit and would touch the (frozen) constructor's public shape. The managers are internal
   implementation detail, not a contract.

**Guiding rule — behavior-preserving, one clean pass.** Every moved line is byte-identical to its
original semantics. No new public API, no new JSON context entry, no new recovery bypass. The
three TransactionCoordinator types keep their exact external call sites; only the internal gate
and lock management is routed through the managers.

---

## Architecture + Components

Two new internal types, one new file each, both in namespace `Scrinia.Core`. `FileMemoryStore`
holds one `readonly` instance of each and delegates the seam primitives.

### 1. `ScopeLockManager` — admission/lock ownership

**File:** `src/Scrinia.Core/ScopeLockManager.cs`
**Namespace:** `Scrinia.Core`
**Visibility:** `internal` (type) + `internal` (promoted nested type)

Owns the admission/lock state and its primitives:

```csharp
// Fields (moved verbatim from FileMemoryStore):
private readonly ConcurrentDictionary<string, AdmissionGate> _admissionLocks = new(StringComparer.OrdinalIgnoreCase);
private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
private readonly object _admissionLifecycleGate = new();
private bool _admissionDisposed;
```

**Promoted nested type** — `AdmissionGate` moves from `FileMemoryStore.AdmissionGate`
(`private sealed class AdmissionGate : IDisposable`, :845) to a top-level
`internal sealed class AdmissionGate : IDisposable`. Members unchanged:

```csharp
internal sealed class AdmissionGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    internal int Leases { get; set; }

    public void Dispose() => Semaphore.Dispose();
}
```

**Moved primitives** (behavior byte-identical):

- `AcquireAdmissionGate(string scope)` — the `lock(_admissionLifecycleGate)` + `GetOrAdd` +
  `gate.Leases++` block (:819-828), including the `ObjectDisposedException.ThrowIf(_admissionDisposed, this)` guard.
- `ReleaseAdmissionGate(string scope, AdmissionGate gate)` — `gate.Leases--`, the zero-lease
  conditional removal, and `gate.Dispose()` (:830-843).
- `GetIndexLock(string scope)` — `_indexLocks.GetOrAdd(scope, _ => new ReaderWriterLockSlim())`
  (:783-784).
- `Dispose()` — the admission-gate teardown block (FileMemoryStore :5115-5128): the
  `lock(_admissionLifecycleGate)` + `_admissionDisposed = true` + collect zero-lease gates +
  remove-and-dispose.

The manager's `GetIndexLock` returns the `ReaderWriterLockSlim` exactly as before; its
`AcquireAdmissionGate` / `ReleaseAdmissionGate` behave byte-identically to the originals.

### 2. `ScopeIndexCache` — cache ownership

**File:** `src/Scrinia.Core/ScopeIndexCache.cs`
**Namespace:** `Scrinia.Core`
**Visibility:** `internal` (type) + `internal` (promoted nested type)

Owns the index cache:

```csharp
// Field (moved verbatim from FileMemoryStore):
private readonly ConcurrentDictionary<string, CachedIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);
```

**Promoted nested type** — `CachedIndex` moves from `FileMemoryStore.CachedIndex`
(`internal sealed class CachedIndex`, :194) to a top-level
`internal sealed class CachedIndex`. Members unchanged:

```csharp
internal sealed class CachedIndex
{
    public List<ArtifactEntry> Entries { get; }
    public CachedIndex(List<ArtifactEntry> entries) => Entries = entries;
}
```

**Moved primitives** — the surface `FileMemoryStore` keeps (the copy-on-write rebuild loops stay
in `FileMemoryStore`; only the cache install moves):

| Method | Body |
|---|---|
| `void Replace(string scope, List<ArtifactEntry> entries)` | `_indexCache[scope] = new CachedIndex(entries);` — takes ownership of `entries`, exactly as the inline `_indexCache[scope] = new CachedIndex(...)` did. |
| `void Remove(string scope)` | `_indexCache.TryRemove(scope, out _);` |
| `bool TryGet(string scope, out CachedIndex? cachedIndex)` | `_indexCache.TryGetValue(scope, out cachedIndex);` |
| `void Clear()` | `_indexCache.Clear();` |

### 3. Wiring into `FileMemoryStore`

- Add `private readonly ScopeLockManager _scopeLockManager;` and
  `private readonly ScopeIndexCache _scopeIndexCache;`.
- Construct both in the `internal FileMemoryStore(...)` constructor (line 314), alongside the
  existing coordinator constructions at :320-326:
  ```csharp
  _scopeLockManager = new ScopeLockManager();
  _scopeIndexCache = new ScopeIndexCache();
  ```
- Route the frozen method bodies through the managers (bodies, signatures, and every
  `RecoverScopeUnsafe(scope, ...)` call stay byte-identical — only the gate/lock acquisition is
  swapped):
  - `WithExclusiveScopeAdmissionAsync` (789): `AcquireAdmissionGate(scope)` →
    `_scopeLockManager.AcquireAdmissionGate(scope)`; `ReleaseAdmissionGate(scope, admissionGate)`
    → `_scopeLockManager.ReleaseAdmissionGate(scope, admissionGate)`.
  - `WithExclusiveLock` (1196): `GetIndexLock(scope)` → `_scopeLockManager.GetIndexLock(scope)`.
  - Every `_indexCache[scope] = new CachedIndex(...)` → `_scopeIndexCache.Replace(scope, ...)`.
  - Every `_indexCache.TryRemove(scope, out _)` → `_scopeIndexCache.Remove(scope)`.
  - Every `_indexCache.TryGetValue(scope, out var cachedIndex)` / `out var cached` →
    `_scopeIndexCache.TryGet(scope, out var cachedIndex)`.
  - Every `_indexCache.Clear()` → `_scopeIndexCache.Clear()`.
- `Dispose` (5104): remove the admission-gate teardown block (:5115-5128) and the index-lock
  teardown block (:5143-5148); replace both with a single `_scopeLockManager.Dispose()`. Keep the
  maintenance/dispose ordering and the `firstFailure` rethrow exactly as-is.

### Callers (unchanged external surface)

`MultiMemoryTransactionCoordinator` calls `WithExclusiveScopeAdmissionAsync` at :101, :197, :240
and `WithExclusiveLock` / `GetIndexLock` through the frozen `FileMemoryStore` methods. The
TransactionCoordinator types keep their exact back-references; only the internal gate/lock
management moves one level deeper.

---

## Data Flow

**Admission/lock (before → after):**

```
WithExclusiveScopeAdmissionAsync ──AcquireAdmissionGate(scope)──▶ [inline _admissionLocks.GetOrAdd + gate.Leases++]
   │
   └─────────────────────────────────────▶ _scopeLockManager.AcquireAdmissionGate(scope)
                                          │   lock(_admissionLifecycleGate)
                                          │   _admissionLocks.GetOrAdd(scope, _ => new AdmissionGate())
                                          │   gate.Leases++
                                          │   return gate
                                          ▼
   body() ──ReleaseAdmissionGate(scope, gate)──▶ _scopeLockManager.ReleaseAdmissionGate(scope, gate)
                                          │   gate.Leases--
                                          │   (Leases==0 && ReferenceEquals) → _admissionLocks.TryRemove
                                          │   gate.Dispose()
```

**Index cache (before → after):**

```
Upsert / WriteSidecarCoW ── _indexCache[scope] = new CachedIndex(entries) ──▶ _scopeIndexCache.Replace(scope, entries)
JournaledRemove ── _indexCache.TryRemove(scope, out _) ──▶ _scopeIndexCache.Remove(scope)
LoadIndex read ── _indexCache.TryGetValue(scope, out var cachedIndex) ──▶ _scopeIndexCache.TryGet(scope, out var cachedIndex)
InvalidateCaches ── _indexCache.Clear() ──▶ _scopeIndexCache.Clear()
```

The CoW rebuild loop (`new List<ArtifactEntry>(cachedIndex.Entries.Count + 1)`, re-add existing
entries, install `new CachedIndex(next)`) **stays in `FileMemoryStore`** — it does not move to
`ScopeIndexCache`. `Replace` takes ownership of the freshly-built list exactly as the inline
install did; the "handed out by reference, live view, never mutated in place" invariant of
`CachedIndex.Entries` is preserved.

---

## Error Handling

- **`ObjectDisposedException` on acquire after dispose.** `AcquireAdmissionGate` keeps the
  `ObjectDisposedException.ThrowIf(_admissionDisposed, this)` guard moved verbatim into
  `ScopeLockManager`. No new failure mode.
- **`firstFailure` rethrow in `Dispose`.** `FileMemoryStore.Dispose` keeps its existing
  `firstFailure` collection over the fact-materializer, maintenance-page, and maintenance-run
  disposals. The two teardown blocks (:5115-5128 admission gates, :5143-5148 index locks) are
  both removed and replaced with a single `_scopeLockManager.Dispose()` call placed at the original
  admission-teardown slot — i.e. after the gated `_maintenancePages` capture (:5106-5113) and
  **before** the fact-materializer disposal (:5130). The outer rethrow
  (`ExceptionDispatchInfo.Capture(firstFailure).Throw()`, :5153-5154) is untouched. Ordering is
  therefore: gated maintenance-page capture → **scope lock manager** → fact materializer →
  maintenance pages → maintenance run → rethrow.
- **Timeout / file-lock failures** (`FileLockTimeoutException` in `WithExclusiveScopeAdmissionAsync`)
  are unchanged — they live in the frozen method body, not in the moved primitives.

---

## Testing Matrix

| Test family | Pins |
|---|---|
| `B5MaintenanceDiscoveryTests` (AdmissionGateCount + concurrent admission) | ScopeLockManager admission/lock lifecycle unchanged; the reflection helper still counts the same gates. |
| `Store/IndexCacheWritePathTests` + `Wave1/LoadIndexImmutabilityTests` | Replace-on-write invariant: every write installs a NEW CachedIndex over a NEW list; readers hold a live view. |
| `JournaledRestoreRecoveryTests` (`LoadIndex_WhenAWarmScopeGainsAPreparedJournal_RecoversBeforeReturningTheCachedIndex`) | Recovery barrier still runs under the exclusive lock after the seam move. |
| `Formats/SidecarVersioningTests` | CachedIndex promotion did not break the warm-cache invalidation path. |
| **Full gate:** `dotnet test Scrinia.sln` | 3,750+ `Scrinia.Tests`, 272 `Retrieval.Benchmarks`, 63 `Embeddings`. Any failure is a behavior regression — do not sweep it under the rug. |

**Test-only, behavior-preserving edits (internal test files only):**

- `tests/Scrinia.Tests/B5MaintenanceDiscoveryTests.cs` `AdmissionGateCount` (helper at :603): it
  currently reflects
  `typeof(FileMemoryStore).GetField("_admissionLocks", BindingFlags.Instance | BindingFlags.NonPublic)`.
  After extraction `_admissionLocks` lives on the manager, so navigate two levels: get the
  `FileMemoryStore._scopeLockManager` field (instance, non-public), then `GetField("_admissionLocks")`
  on THAT manager instance, and return `.Count`. Behavior identical — the count of admission gates
  is unchanged.
- `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (doc `cref` at :418):
  `See cref="FileMemoryStore.CachedIndex"` → `See cref="CachedIndex"`. The type is promoted to
  top-level `Scrinia.Core.CachedIndex`; the old nested-qualified name no longer exists. This is a
  doc-comment-only change.

---

## Frozen Constraints

These carry from P1–P3 and remain non-negotiable for P4:

- **NMP/2 unchanged.** No format revision.
- **`.scrinia/` layout unchanged.** No new top-level directory or memory shape.
- **CLI single-file surface.** `scri serve` remains the only server surface; no new server mode.
- **MCP 12 + 11 disjoint.** The twelve memory tools + `facts` are untouched; P4 adds no tool.
- **Source-gen JSON only.** No new `[JsonSerializable]`. `ScopeLockManager` and `ScopeIndexCache`
  serialize nothing, so **no `*JsonContext` entry is added** (`FileStoreJsonContext` untouched).
- **Recovery bypasses untouched.** `DiscoverMemoriesRecursive` / `ParseScopeLabel` (and all other
  recovery helpers) are unchanged.
- **No new public API.** Both managers are `internal`; both promoted types are `internal`.
- **Every new test uses `TestHelpers.StoreScope` isolation.**
- **No public member signature change.** P4 changes NO public member signature of
  `FileMemoryStore` / `IMemoryStore` / `IPluginStore` / `IAddressResolver`, and NO member of the
  three TransactionCoordinator family types or their interfaces.

### Explicit do-not-touch (P4 anchors)

`WithExclusiveScopeAdmissionAsync` (signature + body), `WithExclusiveLock` (signature + body),
`InvokeWithExclusiveLockForTest`, `RecoverScopeUnsafe`, `WithRecoveryReadBarrier`,
`RecoverAppendUnsafeAsync`, `DiscoverRecoveryForScope`, all append/journal recovery helpers,
`WholeMemoryTransactionCoordinator` / `FileEntityTransactionCoordinator` /
`MultiMemoryTransactionCoordinator` + `IWholeMemoryTransactionCoordinator` /
`IFileEntityTransactionCoordinator` / `IMultiMemoryTransactionCoordinator`, `FileStoreJsonContext`,
all public `FileMemoryStore` members, NMP/2, and the three coordinators' back-references to
`FileMemoryStore` (`WithExclusiveScopeAdmissionAsync`, `ResolveAddress`, `GetStoreDirForScope`,
`DiscoverRecoveryForScope`, `InvalidateScopeCacheAfterRollback`, `WholeMemoryJournalPath`,
`PruneWholeArchives`, `InjectWholeFault`).

---

## Out of Scope

- **Recovery, discovery, and maintenance write-path members.** `RecoverScopeUnsafe`, the
  append/journal recovery helpers, `DiscoverMemoriesRecursive`, `ParseScopeLabel`, and the
  maintenance page service lifecycle are not touched.
- **The TransactionCoordinator family types.** Their seams were extracted in earlier phases; P4
  changes none of them.
- **Any public API.** No new public members on `FileMemoryStore`, `IMemoryStore`, `IPluginStore`,
  or `IAddressResolver`. No new JSON context entry. No new MCP tool.
- **Anything outside the admission/lock and index-cache seams.** The god store keeps its write
  path, its artifact cache, and its topic cache exactly as they are.

</content>
<parameter=filePath>
C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\docs\superpowers\specs\2026-09-24-p4-god-store-seam-extraction-design.md