# P4 God-Store Seam Extraction — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the admission/lock and index-cache seams from the `FileMemoryStore` god store into two small internal owning types, preserving every external behavior.

**Architecture:** Add `ScopeLockManager` (owns `AdmissionGate` + `_admissionLocks` + `_indexLocks` + `Acquire/ReleaseAdmissionGate` + `GetIndexLock` + Dispose teardown) and `ScopeIndexCache` (owns `CachedIndex` + `_indexCache` + `Replace/Remove/TryGet/Clear`) to `Scrinia.Core`. `FileMemoryStore` holds one `readonly` instance of each and routes the frozen method bodies through them; `WithExclusiveScopeAdmissionAsync` / `WithExclusiveLock` keep their exact signatures and only swap the gate/lock acquisition. All moves are byte-identical in behavior.

**Tech Stack:** C# / .NET 10 (`net10.0`), `dotnet test`/`dotnet build`, `Microsoft.CodeAnalysis` source-gen JSON (untouched).

**Spec:** `docs/superpowers/specs/2026-09-24-p4-god-store-seam-extraction-design.md` — the plan argues from the spec; read both before touching code.

## Global Constraints

- NMP/2 format unchanged — no revision, no new artifact shape.
- No new public API — both managers and both promoted types are `internal`.
- Source-gen JSON only — no new `[JsonSerializable]`; do **not** add any `*JsonContext` entry (`FileStoreJsonContext` untouched).
- `FileMemoryStore` changes NO public member signature and NO member of the three TransactionCoordinator family types or their interfaces.
- Recovery bypasses (`DiscoverMemoriesRecursive` / `ParseScopeLabel`) untouched.
- Every new test uses `TestHelpers.StoreScope` isolation.
- One clean, behavior-preserving pass — no rewrites, no restructuring of the write path.

---

### Task 1: Add the two managers

**Files:**
- Create: `src/Scrinia.Core/ScopeLockManager.cs`
- Create: `src/Scrinia.Core/ScopeIndexCache.cs`

**Interfaces:**
- Consumes: nothing yet (no callers exist).
- Produces: `Scrinia.Core.ScopeLockManager` (with `internal AdmissionGate`, `AcquireAdmissionGate`, `ReleaseAdmissionGate`, `GetIndexLock`, `Dispose`) and `Scrinia.Core.ScopeIndexCache` (with `internal CachedIndex`, `Replace`, `Remove`, `TryGet`, `Clear`).

- [ ] **Step 1: Write `ScopeLockManager.cs`**

  Create `src/Scrinia.Core/ScopeLockManager.cs`, namespace `Scrinia.Core`. Move in the four fields and the promoted `AdmissionGate` type verbatim from `FileMemoryStore` (lines 147-150, 845-851):

  ```csharp
  namespace Scrinia.Core;

  internal sealed class ScopeLockManager
  {
      private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
      private readonly ConcurrentDictionary<string, AdmissionGate> _admissionLocks = new(StringComparer.OrdinalIgnoreCase);
      private readonly object _admissionLifecycleGate = new();
      private bool _admissionDisposed;

      // internal: FileMemoryStore (same assembly) routes its frozen method bodies through these.
      internal AdmissionGate AcquireAdmissionGate(string scope)
      {
          lock (_admissionLifecycleGate)
          {
              ObjectDisposedException.ThrowIf(_admissionDisposed, this);
              AdmissionGate gate = _admissionLocks.GetOrAdd(scope, _ => new AdmissionGate());
              gate.Leases++;
              return gate;
          }
      }

      internal void ReleaseAdmissionGate(string scope, AdmissionGate gate)
      {
          lock (_admissionLifecycleGate)
          {
              gate.Leases--;
              if (gate.Leases != 0)
                  return;

              _admissionLocks.TryGetValue(scope, out AdmissionGate? current);
              if (ReferenceEquals(current, gate))
                  _admissionLocks.TryRemove(scope, out _);
              gate.Dispose();
          }
      }

      internal ReaderWriterLockSlim GetIndexLock(string scope) =>
          _indexLocks.GetOrAdd(scope, _ => new ReaderWriterLockSlim());

      internal void Dispose()
      {
          AdmissionGate[] admissionGatesToDispose;
          lock (_admissionLifecycleGate)
          {
              _admissionDisposed = true;
              admissionGatesToDispose = _admissionLocks.Values
                  .Where(gate => gate.Leases == 0)
                  .ToArray();
              foreach (AdmissionGate gate in admissionGatesToDispose)
              {
                  foreach (var pair in _admissionLocks.Where(pair => ReferenceEquals(pair.Value, gate)).ToArray())
                      _admissionLocks.TryRemove(pair.Key, out _);
                  gate.Dispose();
              }
          }
      }
  }

  internal sealed class AdmissionGate : IDisposable
  {
      internal SemaphoreSlim Semaphore { get; } = new(1, 1);
      internal int Leases { get; set; }

      public void Dispose() => Semaphore.Dispose();
  }
  ```

  Notes: all four moved methods are `internal` — `FileMemoryStore` (same assembly, a different type) calls them through the manager instance, and private cross-type members are not visible even within one assembly. `GetIndexLock` and `Dispose()` are `internal` for the same reason.

- [ ] **Step 2: Write `ScopeIndexCache.cs`**

  Create `src/Scrinia.Core/ScopeIndexCache.cs`, namespace `Scrinia.Core`. Move in the field and promoted `CachedIndex` type verbatim (lines 151, 194-204):

  ```csharp
  namespace Scrinia.Core;

  internal sealed class ScopeIndexCache
  {
      private readonly ConcurrentDictionary<string, CachedIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);

      public void Replace(string scope, List<ArtifactEntry> entries) =>
          _indexCache[scope] = new CachedIndex(entries);

      public void Remove(string scope) => _indexCache.TryRemove(scope, out _);

      public bool TryGet(string scope, out CachedIndex? cachedIndex) =>
          _indexCache.TryGetValue(scope, out cachedIndex);

      public void Clear() => _indexCache.Clear();
  }

  internal sealed class CachedIndex
  {
      public List<ArtifactEntry> Entries { get; }
      public CachedIndex(List<ArtifactEntry> entries) => Entries = entries;
  }
  ```

- [ ] **Step 3: Build Core only**

  Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj`
  Expected: **0 errors, 0 warnings.** No callers wired yet — the two files compile standalone.

---

### Task 2: Route FileMemoryStore through the managers

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/B5MaintenanceDiscoveryTests.cs` (reflection path)
- Modify: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (doc `cref` only)

**Interfaces:**
- Consumes: `ScopeLockManager` (from Task 1) and `ScopeIndexCache` (from Task 1).
- Produces: none new — `FileMemoryStore` public surface is unchanged.

- [ ] **Step 1: Wire the two `readonly` fields in the constructor**

  In `FileMemoryStore.cs`, add the fields near the other backing fields (around line 151):

  ```csharp
  private readonly ScopeLockManager _scopeLockManager;
  private readonly ScopeIndexCache _scopeIndexCache;
  ```

  In the `internal FileMemoryStore(...)` constructor (line 314), construct both alongside the coordinator constructions at :320-326:

  ```csharp
  _scopeLockManager = new ScopeLockManager();
  _scopeIndexCache = new ScopeIndexCache();
  ```

- [ ] **Step 2: Route `WithExclusiveScopeAdmissionAsync` (789-817) through the manager**

  Change only the gate acquire/release calls; keep the body, signature, and the `RecoverScopeUnsafe(scope, ...)` call at :808 byte-identical:

  ```csharp
  // before:
  AdmissionGate admissionGate = AcquireAdmissionGate(scope);
  // after:
  AdmissionGate admissionGate = _scopeLockManager.AcquireAdmissionGate(scope);
  ...
  // before:
  ReleaseAdmissionGate(scope, admissionGate);
  // after:
  _scopeLockManager.ReleaseAdmissionGate(scope, admissionGate);
  ```

  Delete the now-unused inline `AcquireAdmissionGate` (:819-828), `ReleaseAdmissionGate` (:830-843), `AdmissionGate` nested type (:845-851), and the `GetIndexLock` primitive (:783-784) — their logic now lives in `ScopeLockManager`.

- [ ] **Step 3: Route `WithExclusiveLock` (1196-1213) through the manager**

  Change only the lock acquisition; keep the signature and the `RecoverScopeUnsafe` at :1205 unchanged:

  ```csharp
  // before:
  var lk = GetIndexLock(scope);
  // after:
  var lk = _scopeLockManager.GetIndexLock(scope);
  ```

- [ ] **Step 4: Route every `_indexCache` call site through `_scopeIndexCache`**

  Replace each inline primitive with the matching manager call:

  | FileMemoryStore site | Replace with |
  |---|---|
  | `_indexCache[scope] = new CachedIndex(entries)` (:2034, :2130) | `_scopeIndexCache.Replace(scope, entries)` |
  | `_indexCache.TryRemove(scope, out _)` (:1941, :1957, :2203, :4955) | `_scopeIndexCache.Remove(scope)` |
  | `_indexCache.TryGetValue(scope, out var cachedIndex)` (:2104, :2184) | `_scopeIndexCache.TryGet(scope, out var cachedIndex)` |
  | `_indexCache.Clear()` (:1928) | `_scopeIndexCache.Clear()` |

  Keep the copy-on-write rebuild loops (the `next` list construction at :2116-2129 and :2187-2198) **in `FileMemoryStore`** — only the `_indexCache[scope] = new CachedIndex(...)` install moves to `Replace`.

  Delete the now-unused inline `CachedIndex` nested type (:194-204) — it lives in `ScopeIndexCache`.

- [ ] **Step 5: Route `Dispose` (5104-5155) through the manager**

  Remove the admission-gate teardown block (:5115-5128) and the index-lock teardown block (:5143-5148). Replace both with a single `_scopeLockManager.Dispose()` call. Keep the maintenance/dispose ordering, the `_maintenancePages` gating at :5106-5113, the `firstFailure` collection over the fact-materializer / maintenance-pages / maintenance-run disposals, and the final `ExceptionDispatchInfo.Capture(firstFailure).Throw()` at :5153-5154 exactly as-is.

- [ ] **Step 6: Update `B5MaintenanceDiscoveryTests.AdmissionGateCount` (helper at :603)**

  `_admissionLocks` now lives on the manager, not on `FileMemoryStore`. Navigate two levels via reflection:

  ```csharp
  private static int AdmissionGateCount(FileMemoryStore store)
  {
      FieldInfo managerField = typeof(FileMemoryStore).GetField("_scopeLockManager",
          BindingFlags.Instance | BindingFlags.NonPublic)!;
      object manager = managerField.GetValue(store)!;
      FieldInfo field = manager.GetType().GetField("_admissionLocks",
          BindingFlags.Instance | BindingFlags.NonPublic)!;
      return ((System.Collections.IDictionary)field.GetValue(manager)!).Count;
  }
  ```

  Behavior identical — the count of admission gates is unchanged.

- [ ] **Step 7: Update `SidecarVersioningTests` doc `cref` (at :418)**

  Doc-comment-only change — the type is promoted to top-level `Scrinia.Core.CachedIndex`:

  ```csharp
  // before:
  /// has no member for, then invalidates the store's in-process index cache so the next read
  /// goes back to disk. Without the invalidation, <c>LoadIndex</c> would keep serving the
  /// warm <see cref="FileMemoryStore.CachedIndex"/> populated by the seeding
  // after:
  /// has no member for, then invalidates the store's in-process index cache so the next read
  /// goes back to disk. Without the invalidation, <c>LoadIndex</c> would keep serving the
  /// warm <see cref="CachedIndex"/> populated by the seeding
  ```

- [ ] **Step 8: Build the solution**

  Run: `dotnet build Scrinia.sln`
  Expected: **0 errors.** If any member is still referenced after removal, it is an un-routed call site — find it and route it before proceeding.

---

### Task 3: Verify + commit

**Files:** all of `src/Scrinia.Core/FileMemoryStore.cs`, the two new manager files, and the two test files.

- [ ] **Step 1: Run the full suite**

  Run: `dotnet test Scrinia.sln --nologo`
  Expected: **fully green.** 3,750+ `Scrinia.Tests`, 272 `Retrieval.Benchmarks`, 63 `Embeddings`. Any failure is a behavior regression — do not sweep it under the rug. Locate the failing family using the matrix in the spec (admission count, replace-on-write, recovery barrier, or the `cref` doc-comment) and fix inline.

- [ ] **Step 2: Commit all changed files**

  ```bash
  git add -A
  git commit -m "core: extract admission/lock + index-cache seams from FileMemoryStore (P4)" -m "FileMemoryStore owned the per-scope admission/lock machinery and the in-process index cache inline as nested types (AdmissionGate, CachedIndex) with scattered _indexCache writes. Extracted ScopeLockManager (AdmissionGate + _admissionLocks + _indexLocks + Acquire/ReleaseAdmissionGate + GetIndexLock + Dispose teardown) and ScopeIndexCache (CachedIndex + _indexCache + Replace/Remove/TryGet/Clear). FileMemoryStore holds one of each and delegates; WithExclusiveScopeAdmissionAsync/WithExclusiveLock keep their exact signatures (external callers) and only route the gate/lock management. Recovery, the three TransactionCoordinator family types, FileStoreJsonContext, and all public API are untouched. Behavior-preserving; full suite green."
  ```

- [ ] **Step 3: Report**

  Report the build pass/fail (Task 1 and Task 2) and the test totals per project (Task 3). Confirm the working tree is clean after commit.
