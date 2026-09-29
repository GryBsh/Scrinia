# P5 — Maintenance Page-Service Cache Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p5-maintenance-page-service-cache-design.md`
**Phase:** P5 (fifth god-store seam extraction out of `FileMemoryStore`).
**Shape:** one new internal owning type + store wiring + one behavior-preserving test-helper edit. Single wave, sequential.

## Workstreams

- W1 — new `MaintenancePageServiceCache` type (`src/Scrinia.Core/MaintenancePageServiceCache.cs`).
- W2 — `FileMemoryStore` wiring (field, ctor, passthrough re-routing, teardown, forwarding property).
- W3 — test-only edit (`B5MaintenanceDiscoveryTests.MaintenancePageServices` reflection).
- W4 — verification (`dotnet test Scrinia.sln`).

W1 is the only upstream dependency; W2 depends on W1; W3 depends on W2; W4 depends on all. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `MaintenancePageServiceCache`

New file `src/Scrinia.Core/MaintenancePageServiceCache.cs`, namespace `Scrinia.Core`, `internal sealed class MaintenancePageServiceCache`.

Move verbatim from `FileMemoryStore`:

- Fields: `MaintenancePageServiceCreationBarrierForTest` (was `internal Action?` at :117 — becomes an `internal` auto-property on the cache), `_maintenancePages` (was `private readonly ConcurrentDictionary<string, MaintenanceDiscoveryPageService> _maintenancePages = new(StringComparer.Ordinal);` at :142), `_maintenanceLifecycleGate` (`private readonly object _maintenanceLifecycleGate = new();` at :143), `_maintenanceDisposed` (`private bool _maintenanceDisposed;` at :144).
- Constructor: `internal MaintenancePageServiceCache(RecoveryDiscoveryCoordinator recoveryDiscovery, MaintenanceRunRegistry maintenanceRuns)` — store the two injected collaborators in private readonly fields named exactly `_recoveryDiscovery` and `_maintenanceRuns`.
- Method `GetMaintenancePageService(string scope, string storeDirectory)` (was `private` at :814–834): copy the body byte-for-byte, but (a) make it `internal`, (b) reference the injected `_recoveryDiscovery`/`_maintenanceRuns` (the body already uses those names, so no textual change beyond visibility), (c) reference `MaintenancePageServiceCreationBarrierForTest`/`_maintenanceLifecycleGate`/`_maintenanceDisposed`/`_maintenancePages` (now the cache's own members).
- Method `MaintenanceDiscoveryPageService[] TakeDisposalSnapshot()`:
  ```csharp
  internal MaintenanceDiscoveryPageService[] TakeDisposalSnapshot()
  {
      lock (_maintenanceLifecycleGate)
      {
          if (_maintenanceDisposed) return Array.Empty<MaintenanceDiscoveryPageService>();
          _maintenanceDisposed = true;
          var pages = _maintenancePages.Values.ToArray();
          _maintenancePages.Clear();
          return pages;
      }
  }
  ```

Do NOT add any using beyond what `FileMemoryStore` already has for these types (`System`, `System.Collections.Concurrent`, `System.Linq`, `Scrinia.Core.Maintenance`-namespace types `RecoveryDiscoveryCoordinator`, `MaintenanceRunRegistry`, `MaintenanceDiscoveryPageService`). Confirm each type's namespace by a quick grep before writing.

## W2 — Wire into `FileMemoryStore`

All edits are in `src/Scrinia.Core/FileMemoryStore.cs`. Body-preserving; only ownership moves.

1. **Remove** the four moved members from `FileMemoryStore`:
   - `internal Action? MaintenancePageServiceCreationBarrierForTest` (:117).
   - `private readonly ConcurrentDictionary<string, MaintenanceDiscoveryPageService> _maintenancePages = new(StringComparer.Ordinal);` (:142).
   - `private readonly object _maintenanceLifecycleGate = new();` (:143).
   - `private bool _maintenanceDisposed;` (:144).
   - `private MaintenanceDiscoveryPageService GetMaintenancePageService(string scope, string storeDirectory)` (:814–834) — copy its body into W1 first, then delete.
2. **Add** `private readonly MaintenancePageServiceCache _maintenancePageServiceCache;` in the fields region (drop the four removed fields' slot).
3. **Construct** it in the internal constructor right after `_scopeIndexCache = new ScopeIndexCache();` (near :295):
   ```csharp
   _maintenancePageServiceCache = new MaintenancePageServiceCache(_recoveryDiscovery, _maintenanceRuns);
   ```
   (`_recoveryDiscovery` at :140 and `_maintenanceRuns` at :141 are already constructed earlier in the ctor.)
4. **Forward the barrier property** — add to `FileMemoryStore` an `internal` forwarding property (replaces the removed :117 property):
   ```csharp
   internal Action? MaintenancePageServiceCreationBarrierForTest
   {
       get => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest;
       set => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest = value;
   }
   ```
5. **Re-route the four passthroughs** (:796–812): replace every `GetMaintenancePageService(scope, storeDirectory)` call with `_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory)`. `DiscoverMaintenancePage`/`ContinueMaintenancePage` become:
   ```csharp
   new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory), _maintenanceRuns)
   ```
   Leave `DiscoverRecoveryForScope` (:792) untouched.
6. **Re-route `Dispose`** teardown (:5045–5052): replace
   ```csharp
   lock (_maintenanceLifecycleGate)
   {
       if (_maintenanceDisposed) return;
       _maintenanceDisposed = true;
       maintenancePages = _maintenancePages.Values.ToArray();
       _maintenancePages.Clear();
   }
   ```
   with
   ```csharp
   maintenancePages = _maintenancePageServiceCache.TakeDisposalSnapshot();
   ```
   Keep the rest of `Dispose` byte-identical (the `foreach` disposing `maintenancePages`, `_scopeLockManager.Dispose()`, `_factMaterializer`, `_maintenanceRuns`, the `firstFailure` rethrow). Note: the local `MaintenanceDiscoveryPageService[] maintenancePages;` declaration stays; the body now comes from the cache.

## W3 — Test-only edit (internal file)

`tests/Scrinia.Tests/B5MaintenanceDiscoveryTests.cs` `MaintenancePageServices(FileMemoryStore store)` helper (:594–601). Currently:
```csharp
FieldInfo field = typeof(FileMemoryStore).GetField("_maintenancePages",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
return ((System.Collections.IDictionary)field.GetValue(store)!).Values...
```
Change to navigate two levels (behavior identical):
```csharp
FieldInfo owner = typeof(FileMemoryStore).GetField("_maintenancePageServiceCache",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
object cache = owner.GetValue(store)!;
FieldInfo field = cache.GetType().GetField("_maintenancePages",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
return ((System.Collections.IDictionary)field.GetValue(cache)!).Values...
```
Do NOT touch `AdmissionGateCount` (:603–611) — that is P4's pin. Do NOT touch the barrier test (:563/580) — the forwarding property preserves `store.MaintenancePageServiceCreationBarrierForTest`.

## W4 — Verification (mandatory)

1. `dotnet build Scrinia.sln` — must be 0 errors.
2. `dotnet test Scrinia.sln` — must be green. Because the full suite is large, first run a targeted seam filter to isolate P5's blast radius:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~B5Maintenance|FullyQualifiedName~MaintenancePage|FullyQualifiedName~JournaledRestore|FullyQualifiedName~RecoveryDiscovery|FullyQualifiedName~Maintenance"
   ```
   Confirm `Passed!` with 0 failures, then run the FULL `dotnet test Scrinia.sln` and report the final `Passed!`/`Failed!` summary line. Any failure is a behavior regression — fix it, do not sweep it.

## Acceptance

- `MaintenancePageServiceCache` is the sole owner of `_maintenancePages`/`_maintenanceLifecycleGate`/`_maintenanceDisposed`/`MaintenancePageServiceCreationBarrierForTest`/`GetMaintenancePageService`; `FileMemoryStore` holds one `readonly` instance and delegates.
- No new public API; `FileStoreJsonContext` untouched; no new `[JsonSerializable]`; NMP/2 and `.scrinia/` unchanged; all frozen do-not-touch anchors intact.
- Full suite green.
