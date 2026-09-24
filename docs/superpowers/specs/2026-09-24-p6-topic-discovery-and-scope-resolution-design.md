# P6 — God-Store Seam Extraction: Topic Discovery + Scope Resolution — Design Spec

> **Phase:** P6 (god-store seam extraction — topic discovery + scope resolution)
> **Status:** Approved design — implementation is a separate, gitignored plan.
> **Parent specs:** P1–P5 (admission, lock, index-cache, maintenance-page-service seams). This phase extracts the topic-discovery + scope-resolution family out of `FileMemoryStore`.

---

## Context

`FileMemoryStore` (5,101 lines) is a god store. P4 extracted the admission/lock and index-cache seams; P5 extracted the maintenance page-service cache seam. This phase extracts the **topic-discovery + scope-resolution** family: the `_cachedTopics` cache, the `DiscoverTopics`/`DiscoverMemoriesRecursive` scan, the `NormalizeScopeFilters`/`ResolvePathPrefix`/`MatchesPathPrefix`/`ResolveReadScopes` resolution chain, and the `IncludesEphemeral` predicate — into one small internal owning type `TopicDiscoveryService`, the direct analog of P4's `ScopeIndexCache` and P5's `MaintenancePageServiceCache`.

The listing composition (`ListScoped`/`AnyScoped`/`GatherTopicInfos`/`ListTopicArtifacts`) and search orchestration (`SearchAll`) are **not** part of P6 — they own `LoadIndex` (the P4 recovery/index-lock seam) and the external `WeightedAttributeScorer`, respectively, and belong to later phases. Folding them in here would drag the recovery/admission/index-cache and scorer couplings back into the discovery service — precisely what the phased extraction avoids.

The topic-discovery family today lives in `FileMemoryStore.cs` as:

| Artifact | Location | Kind |
|---|---|---|
| `_cachedTopics` (`string[]?`) | :165 | backing field (discovery cache) |
| `DiscoverTopics()` | :4402 | public scan + cache-populate |
| `DiscoverMemoriesRecursive(...)` | :4425 | private static fs recursion |
| `NormalizeScopeFilters(token)` | :639 | internal resolver |
| `ResolvePathPrefix(pathPrefix)` | :675 | internal resolver |
| `MatchesPathPrefix(topicPart, prefix)` | :709 | private static string op |
| `ResolveReadScopes(scopes)` | :722 | public resolver |
| `ResolveReadScopes(scopes, excludeTopics)` | :4331 | public resolver (IMemoryStore override) |
| `IncludesEphemeral(scopes)` | :4063 | private static predicate |
| `_cachedTopics = null` (9 invalidation sites) | :610, :1659, :1849, :1864, :1880, :1957, :2062, :2139, :4878 | write-path/migration invalidation |

`MemoriesRoot` (:589) is the one-time v3-layout migration gate. It is **not** moved: it is called from the write path (`GetStoreDirForScope` containment at :4006–4007 and the gate at :622), so it stays in `FileMemoryStore`. Its migration gate currently co-invalidates `_scopeIndexCache.Clear()` (:609, stays) and `_cachedTopics = null` (:610); the latter becomes a call into the service.

---

## Decision / Alternatives

**Decision: move `_cachedTopics`, `DiscoverTopics`, `DiscoverMemoriesRecursive`, `NormalizeScopeFilters`, `ResolvePathPrefix`, `MatchesPathPrefix`, both `ResolveReadScopes` overloads, and `IncludesEphemeral` into one new internal owning type `TopicDiscoveryService`. `FileMemoryStore` keeps the public/internal forwarding surface (so `IMemoryStore`, `FakeMemoryStore`, and direct test calls are byte-identical), forwards `InvalidateCachedTopics()` at the 9 invalidation sites, and passes a `Func<string>` hook returning `MemoriesRoot` so the moved `DiscoverTopics` still runs the migration gate.**

Alternatives considered and rejected:

1. **Extract the whole listing/search family together with discovery.** Rejected: `ListScoped`/`AnyScoped`/`GatherTopicInfos`/`ListTopicArtifacts` call `LoadIndex` (P4 recovery + index-lock seam); `SearchAll` composes `WeightedAttributeScorer`/`RankerOptions` (`Search/*`). Folding them into the discovery service reintroduces those couplings — the opposite of the phased approach. Separate phases.
2. **Keep `_cachedTopics` invalidation as inline `_cachedTopics = null` and only move the read side.** Rejected: that leaves the single source of truth for `_cachedTopics` split across the store and service; a later reader cannot tell which owns the cache. One owner.
3. **An interface (`ITopicDiscoveryService`).** Rejected: `FileMemoryStore` is the only owner and caller; no factory/DI surface needs a swap. Internal implementation detail.

**Guiding rule — behavior-preserving, one clean pass.** Every moved method body is byte-identical. No new public API (the store keeps the exact `DiscoverTopics`/`ResolveReadScopes`/`NormalizeScopeFilters` signatures as forwarders), no new JSON context entry, no new recovery bypass, no change to the migration gate or `LoadIndex`.

---

## Architecture + Components

### 1. `TopicDiscoveryService`

**File:** `src/Scrinia.Core/TopicDiscoveryService.cs`
**Namespace:** `Scrinia.Core`
**Visibility:** `internal sealed class`

**Fields (moved verbatim):**
```csharp
private string[]? _cachedTopics;   // was :165
```

**Constructor:**
```csharp
internal TopicDiscoveryService(Func<string> memoriesRootProvider)
{
    _memoriesRootProvider = memoriesRootProvider;
}
```
The `memoriesRootProvider` hook returns `FileMemoryStore.MemoriesRoot` (the migration-gated root). `DiscoverTopics` calls it to run the gate before scanning — identical to the old `MemoriesRoot` read inside `DiscoverTopics`.

**Moved members (bodies byte-identical, only visibility/owner changes):**
- `public string[] DiscoverTopics()` (:4402) — `internal` on the service; `MemoriesRoot` read becomes `_memoriesRootProvider()`.
- `private static void DiscoverMemoriesRecursive(string dir, string relativePath, List<string> results)` (:4425) — `internal`.
- `internal IReadOnlyList<string> NormalizeScopeFilters(string? token)` (:639) — `internal` (kept internal so the store forwarder + test can reach it).
- `internal IReadOnlyList<string> ResolvePathPrefix(string pathPrefix)` (:675) — `internal`.
- `private static bool MatchesPathPrefix(string topicPart, string prefix)` (:709) — `internal static`.
- `public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null)` (:722) — `internal`.
- `public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)` (:4331) — `internal`.
- `private static bool IncludesEphemeral(IReadOnlyList<string>? scopes)` (:4063) — `internal static`.

**New member:**
```csharp
internal void InvalidateCachedTopics() => _cachedTopics = null;
```

### 2. Wiring into `FileMemoryStore`

- Add `private readonly TopicDiscoveryService _topicDiscoveryService;`.
- Construct in the internal constructor next to the P4/P5 managers:
  ```csharp
  _topicDiscoveryService = new TopicDiscoveryService(() => MemoriesRoot);
  ```
  (`MemoriesRoot` is the migration-gated property; the closure runs the gate on first discovery call, exactly as before.)
- **Remove** the nine moved members from the store (the bodies above).
- **Keep** on the store the exact same-signature forwarding members (bodies are one-liners delegating to the service):
  - `public virtual string[] DiscoverTopics()` → `_topicDiscoveryService.DiscoverTopics()`. **Must stay `public virtual`** — `FakeMemoryStore` (:77) and test subclasses override it.
  - `public virtual IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null)` → `_topicDiscoveryService.ResolveReadScopes(scopes)`. **Must stay `public virtual`** — `FakeMemoryStore` (:79) and `ListScopedCompletenessTests` (:198) override it.
  - `public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)` → `_topicDiscoveryService.ResolveReadScopes(scopes, excludeTopics)` (the IMemoryStore override; keep non-virtual default-overriding shape).
  - `internal IReadOnlyList<string> NormalizeScopeFilters(string? token)` → `_topicDiscoveryService.NormalizeScopeFilters(token)`. **Must stay `internal`** — `tests/.../FileMemoryStoreTests.cs` calls `_store.NormalizeScopeFilters(...)` directly.
  - `private static bool IncludesEphemeral(...)` — the store callers forward to `_topicDiscoveryService.IncludesEphemeral(...)`; this private helper is deleted from the store.
- **Replace the 9 `_cachedTopics = null` sites** with `_topicDiscoveryService.InvalidateCachedTopics()`, keeping each site's existing guard intact:
  - :610 (`MemoriesRoot` migration gate) — inside `if (result.Ran)`, after `_scopeIndexCache.Clear()`.
  - :1659, :1864, :1880, :1957, :4878 — inside `if (scope.StartsWith("local:"))`.
  - :1849 (`InvalidateCaches`) — unconditional.
  - :2062 (`UpsertCore`) — inside `if (scopeWasEmpty && scope.StartsWith("local:"))`.
  - :2139 (`Remove`) — inside `if (scope.StartsWith("local:") && !Directory...empty)`.
- **`InvalidateCaches`** (:1846) keeps `_scopeIndexCache.Clear()`, `_artifactCache.Invalidate(...)`, `InvalidateEntityFacts()`; only `_cachedTopics = null` → `_topicDiscoveryService.InvalidateCachedTopics()`.

### 3. Callers that forward `IncludesEphemeral` (stay in store, P6b/P7 territory)

`ListScoped` (:4077), `AnyScoped` (:4098), `FilterToQueryScopes` (:4317), `ListScoped(excludeTopics)` (:4242), and `GetEntityFactStore` (:4136) each call `IncludesEphemeral(...)`; change them to `_topicDiscoveryService.IncludesEphemeral(...)`.

---

## Data Flow

```
DiscoverTopics() [service]
   │  _memoriesRootProvider()  ──▶ () => store.MemoriesRoot  (runs one-time migration gate;
   │                            on migration: store._scopeIndexCache.Clear() +
   │                            service.InvalidateCachedTopics())
   ▼  scan _memoriesRoot via DiscoverMemoriesRecursive
   ▶  _cachedTopics = results; return _cachedTopics

ResolveReadScopes(scopes) [service]
   │  NormalizeScopeFilters (service) ──▶ ResolvePathPrefix (service) ──▶ DiscoverTopics (service)
   │  MatchesPathPrefix (service)
   ▼  distinct, ordered scope list

Store forwarders: store.DiscoverTopics() ──▶ service.DiscoverTopics();
                  store.ResolveReadScopes(├─▶ service.ResolveReadScopes(...);
                  store.NormalizeScopeFilters(├─▶ service.NormalizeScopeFilters(...);
Store invalidation: <9 write-path sites>.cachedTopics = null  ──▶  service.InvalidateCachedTopics()
```

---

## Error Handling

- **Migration gate unchanged.** `MemoriesRoot` still runs `StoreLayoutMigration` once (double-checked lock `_layoutGate`/`_layoutChecked`), still writes the same `Console.Error` line, still `_scopeIndexCache.Clear()` at :609. Only :610's `_cachedTopics = null` becomes `InvalidateCachedTopics()` — same effect, same timing.
- **Cache-hit invariant preserved.** `DiscoverTopics` still returns the cached reference between mutations and still only re-scans after `InvalidateCachedTopics()` (the 9 sites) or the first migration. No TTL reintroduced.
- **No new failure mode.** No public/internal signature changes on the forwarding members; `IMemoryStore`/`FakeMemoryStore`/test call sites see identical behavior.

---

## Testing Matrix

| Test family | Pins |
|---|---|
| `FileMemoryStoreCacheTests` (`DiscoverTopics` cache-hit/invalidation) + `Store/TopicCacheInvalidationTests` | `_cachedTopics` now owned by the service; invalidation still fires at every original site (via `InvalidateCachedTopics()`). |
| `FileMemoryStoreTests` (`DiscoverTopics` dir scan + `CachedAfterFirstCall`, migration paths :404/429/446/462; `ResolveReadScopes` family :1061+; `NormalizeScopeFilters` :1135) | Forwarding surface preserves every signature the tests call; `NormalizeScopeFilters` still reachable on `_store`. |
| `DualModeParseTests` (`MemoriesRoot` auto-migration + `NormalizeScopeFilters`) | Migration gate still runs through `() => MemoriesRoot`; `NormalizeScopeFilters` forwarder intact. |
| `StoreLayoutMigrationTests` (`DiscoverTopics_AsFirstAccess_MigratesBeforeScanning`) | `MemoriesRoot` gate + `InvalidateCachedTopics` ordering preserved. |
| `ListScopedCompletenessTests` (`ResolveReadScopes` override :198) | `ResolveReadScopes` stays `public virtual` on the store. |
| `FakeMemoryStore` overrides (`DiscoverTopics` :77, `ResolveReadScopes` :79) | Both stay `public virtual`; default IMemoryStore 2-arg path still routes through the fake's 1-arg override. |
| **Full gate:** `dotnet test Scrinia.sln` | 3,750+ `Scrinia.Tests`, 272 `Retrieval.Benchmarks`, 63 `Embeddings`. Any failure is a behavior regression — do not sweep it under the rug. |

---

## Frozen Constraints

These carry from P1–P5 and remain non-negotiable for P6:

- **NMP/2 unchanged.** No format revision.
- **`.scrinia/` layout unchanged.** No new top-level directory or memory shape.
- **CLI single-file surface.** `scri serve` remains the only server surface.
- **MCP 12 + 11 disjoint.** The twelve memory tools + `facts` are untouched; P6 adds no tool.
- **Source-gen JSON only.** No new `[JsonSerializable]`. `TopicDiscoveryService` and the moved types serialize nothing — **no `*JsonContext` entry added** (`FileStoreJsonContext` untouched).
- **Recovery bypasses untouched.** `DiscoverMemoriesRecursive`/`ParseScopeLabel` and all recovery helpers unchanged; `LoadIndex`, `WithRecoveryReadBarrier`, `RecoverScopeUnsafe` untouched.
- **No new public API.** The service is `internal`; the store's forwarding members keep their exact existing signatures/visibility.
- **Every new test uses `TestHelpers.StoreScope` isolation.**
- **No public member signature change.** `DiscoverTopics`/`ResolveReadScopes`/`NormalizeScopeFilters` keep their current signatures on `FileMemoryStore`/`IMemoryStore`/`IPluginStore`/`IAddressResolver`; the three TransactionCoordinator family types and their interfaces untouched.

### Explicit do-not-touch (P6 anchors)

`MemoriesRoot` (migration-gate body + `_layoutGate`/`_layoutChecked` double-check), `LoadIndex` and everything it delegates to (`LoadIndexUnderRecoveryBarrier`, `LoadIndexUnsafe`, `LoadEntriesFromSidecars*`, `ResolveSidecarPath`, `CanonicalizeEntryForScope`), the recovery seam (`WithRecoveryReadBarrier`, `RecoverScopeUnsafe`, `RecoverAppendUnsafeAsync`, `DiscoverRecoveryForScope`), the maintenance deletion layer (`ExecuteMaintenanceScopeAsync` and below), the `WithExclusiveScopeAdmissionAsync`/`WithExclusiveLock` admission + lock seams (P4), `_scopeIndexCache`/`ScopeIndexCache` (P4), `_maintenancePageServiceCache`/`MaintenancePageServiceCache` (P5), the facts materializer glue (`GetEntityFactStore`, `InvalidateEntityFacts`, `ResolveFactSidecarPath`), `InvalidateCaches`/`InvalidateScopeCacheAfterRollback`/`InvalidateCommittedScope` except for the single `_cachedTopics = null` → `InvalidateCachedTopics()` swap, the TransactionCoordinator family types + interfaces, `FileStoreJsonContext`, all public `FileMemoryStore`/`IMemoryStore`/`IPluginStore`/`IAddressResolver` members other than the forwarding ones above, and the listing/search composition (`ListScoped`, `AnyScoped`, `GatherTopicInfos`, `ListTopicArtifacts`, `SearchAll`, `FilterToQueryScopes`, `BuildTopicInfosFromCandidates`).

---

## Out of Scope

- **Listing composition** (`ListScoped` both overloads, `AnyScoped`, `ListEphemeral`, `GatherTopicInfos`, `ListTopicArtifacts`) — owns `LoadIndex`; a later phase.
- **Search orchestration** (`SearchAll`, `FilterToQueryScopes`, `BuildTopicInfosFromCandidates`) — owns `WeightedAttributeScorer`/`RankerOptions`; a later phase.
- **Facts materializer** — already extracted to `EntityFactMaterializer`.
- **`MemoriesRoot` migration gate** — stays in the store (write-path containment); only its `_cachedTopics` invalidation is forwarded.
- **Any public API.** No new public members. No new JSON context entry. No new MCP tool.
