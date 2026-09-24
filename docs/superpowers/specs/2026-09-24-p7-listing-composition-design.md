# P7 — God-Store Seam Extraction: Listing Composition — Design Spec

> **Phase:** P7 (god-store seam extraction — listing composition)
> **Status:** Approved design — implementation is a separate, gitignored plan.
> **Parent specs:** P4 (index-cache/loadIndex), P5 (maintenance cache), P6 (topic discovery + scope resolution). This phase extracts the breadth-listing composition out of `FileMemoryStore`.

---

## Context

`FileMemoryStore` (4,910 lines) is a god store. P4–P6 extracted the admission/index-cache, maintenance-page-service, and topic-discovery/scope-resolution seams. This phase extracts the **listing composition** family — the methods that compose `LoadIndex` (P4), scope resolution (P6), and the ephemeral listing — into one small internal owning type `ListingService`.

The listing composition today lives in `FileMemoryStore.cs` as:

| Artifact | Location | Kind |
|---|---|---|
| `ListScoped(scopes)` | :3931 | public IMemoryStore member |
| `ListScoped(scopes, excludeTopics)` | :4092 | public IMemoryStore member |
| `AnyScoped(scopes, predicate)` | :3954 | public IMemoryStore member (interface default override) |
| `GatherTopicInfos(scopes)` | :4257 | public IMemoryStore member |
| `ListTopicArtifacts(topicScope)` | :4281 | public IMemoryStore member |
| `FilterToQueryScopes(...)` | :4169 | private composition |
| `BuildTopicInfosFromCandidates(...)` | :4066 | private static composition |
| `ListEphemeral()` | :553 | public (not an IMemoryStore member) |
| `ProjectEphemeralEntry(...)` | :3697 | private static (write path uses it at :3694) |

`ListEphemeral` and `ProjectEphemeralEntry` **stay in the store**: `ProjectEphemeralEntry` is used by the ephemeral write path (:3694) and `ListEphemeral` owns the in-memory `_ephemeralStore` dictionary. They are reachable from the new service through a single `listEphemeral` hook, so no write-path member moves.

---

## Decision / Alternatives

**Decision: move `ListScoped`×2, `AnyScoped`, `GatherTopicInfos`, `ListTopicArtifacts`, `FilterToQueryScopes`, and `BuildTopicInfosFromCandidates` into one new internal owning type `ListingService`, injecting the already-extracted seams (`LoadIndex` from P4, `TopicDiscoveryService` from P6) and the store-owned `GetStoreDirForScope`/`SanitizeName`/`ListEphemeral` as `Func<>` hooks. `FileMemoryStore` keeps the five public `IMemoryStore` forwarding signatures and the `ListEphemeral`/`ProjectEphemeralEntry` members; the four composition helpers live in the service.**

Alternatives considered and rejected:

1. **Extract the listing family inline, extract nothing.** Rejected: the god-store reduction is never completed; the listing composition stays entangled with the write path.
2. **Move `ListEphemeral`/`ProjectEphemeralEntry` into the service too.** Rejected: `ProjectEphemeralEntry` is used by the ephemeral write path (:3694), which must stay on the store; splitting the two projection sites across the seam would force a forwarder on the write path. Keep them behind one `listEphemeral` hook.
3. **Fold listing composition into the P6 `TopicDiscoveryService`.** Rejected: listing owns `LoadIndex` (the P4 recovery/index-lock seam) and the filesystem-layout calls (`GetStoreDirForScope`/`SanitizeName`); folding it back in re-introduces the recovery/filesystem couplings the phased extraction avoids.
4. **An interface (`IListingService`).** Rejected: `FileMemoryStore` is the only owner and caller; no factory/DI surface needs a swap.

**Guiding rule — behavior-preserving, one clean pass.** Every moved body is byte-identical to its original semantics. No new public API, no new JSON context entry, no new recovery bypass. The five public `IMemoryStore` listing signatures are preserved as one-line forwarders.

---

## Architecture + Components

### 1. `ListingService`

**File:** `src/Scrinia.Core/ListingService.cs`
**Namespace:** `Scrinia.Core`
**Visibility:** `internal sealed class`

**Constructor hooks** (each a `Func<>` isolating the service from the seam it composes):
```csharp
internal ListingService(
    Func<string, IReadOnlyList<ArtifactEntry>> loadIndex,
    Func<string, string> getStoreDirForScope,
    Func<string, string> sanitizeName,
    Func<IReadOnlyList<ScopedArtifact>> listEphemeral,
    Func<IReadOnlyList<string>?, bool> includesEphemeral,
    Func<IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopes,
    Func<IReadOnlyList<string>?, IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopesWithExcludes)
{ ... assign to private readonly fields ... }
```

**Moved members** (bodies byte-identical from `FileMemoryStore`; only call sites are re-targeted to the injected hooks):
- `internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes)` — `IncludesEphemeral` → `_includesEphemeral(scopes)`; `ListEphemeral` → `_listEphemeral()`; `ResolveReadScopes` → `_resolveReadScopes(scopes)`; `LoadIndex` → `_loadIndex(scope)`.
- `internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)` — same re-targeting; `ResolveReadScopes(scopes, excludeTopics)` → `_resolveReadScopesWithExcludes(scopes, excludeTopics)`.
- `internal bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate)` — same re-targeting.
- `internal List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes)` — same re-targeting.
- `internal List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)` — `LoadIndex` → `_loadIndex`; `GetStoreDirForScope` → `_getStoreDirForScope`; `SanitizeName` → `_sanitizeName`.
- `internal List<ScopedArtifact> FilterToQueryScopes(IReadOnlyList<ScopedArtifact> candidates, IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)` — `ResolveReadScopes` → `_resolveReadScopesWithExcludes`; `IncludesEphemeral` → `_includesEphemeral`.
- `internal static List<TopicInfo> BuildTopicInfosFromCandidates(IReadOnlyList<ScopedArtifact> candidates)` — body unchanged (pure static).

### 2. Wiring into `FileMemoryStore`

- Add `private readonly ListingService _listing;`.
- Construct in the internal constructor next to the other services:
  ```csharp
  _listing = new ListingService(
      scope => LoadIndex(scope),
      scope => GetStoreDirForScope(scope),
      name => SanitizeName(name),
      () => ListEphemeral(),
      s => TopicDiscoveryService.IncludesEphemeral(s),
      s => _topicDiscoveryService.ResolveReadScopes(s),
      (s, e) => _topicDiscoveryService.ResolveReadScopes(s, e));
  ```
  (`TopicDiscoveryService.IncludesEphemeral` is the P6 static; `_topicDiscoveryService.ResolveReadScopes` are the P6 forwarders.)
- **Delete** the six moved members from the store (`ListScoped`×2, `AnyScoped`, `GatherTopicInfos`, `ListTopicArtifacts`, `FilterToQueryScopes`, `BuildTopicInfosFromCandidates`).
- **Add back** on the store the five public forwarding signatures (one-liners), keeping `sealed`-class compatibility (no `virtual` — `FileMemoryStore` is sealed):
  ```csharp
  public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) => _listing.ListScoped(scopes);
  public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics) =>
      _listing.ListScoped(scopes, excludeTopics);
  public bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate) =>
      _listing.AnyScoped(scopes, predicate);
  public List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes = null) => _listing.GatherTopicInfos(scopes);
  public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) => _listing.ListTopicArtifacts(topicScope);
  ```
- `SearchAll` (:4117) keeps its body but forwards its composition calls to `_listing` (`_listing.FilterToQueryScopes(...)`, `_listing.BuildTopicInfosFromCandidates(...)`, `ListScoped(...)` via the forwarder).
- `ListEphemeral` (:553) and `ProjectEphemeralEntry` (:3697) are **deleted from the store** and recreated inside `ListingService` as the `_listEphemeral`/`_includesEphemeral`-backed members — NO. They stay on the store; only `ListEphemeral` is wrapped by the `listEphemeral` hook. `ProjectEphemeralEntry` (:3697) stays untouched (write path at :3694 unchanged).

---

## Data Flow

```
Store.ListScoped(scopes)              ──▶ _listing.ListScoped(scopes)
Store.AnyScoped(scopes, pred)         ──▶ _listing.AnyScoped(scopes, pred)
Store.GatherTopicInfos(scopes)        ──▶ _listing.GatherTopicInfos(scopes)
Store.ListTopicArtifacts(scope)       ──▶ _listing.ListTopicArtifacts(scope)
Store.SearchAll(q)                    ──▶ _listing.FilterToQueryScopes(...)/BuildTopicInfosFromCandidates(...)/ListScoped(...)
ListingService ──loadIndex──▶ LoadIndex (P4)
ListingService ──getStoreDirForScope──▶ GetStoreDirForScope
ListingService ──sanitizeName──▶ SanitizeName
ListingService ──listEphemeral──▶ ListEphemeral (store, owns _ephemeralStore)
ListingService ──includesEphemeral──▶ TopicDiscoveryService.IncludesEphemeral (P6)
ListingService ──resolveReadScopes──▶ TopicDiscoveryService.ResolveReadScopes (P6)
```

---

## Error Handling

- **Forwarding signatures preserved.** The five public `IMemoryStore` listing members keep their exact signatures on `FileMemoryStore`; `SearchAll` and all MCP/tool call sites see identical behavior.
- **`ProjectEphemeralEntry` write path unchanged** (:3694); only `ListScoped`/`AnyScoped` reach it through the `listEphemeral` hook, which is unchanged in effect.
- **`LoadIndex` identity preserved.** `ListingService.loadIndex` forwards to the same `LoadIndex` (P4), so the cache-completeness behavioral pin (`ListScoped` and `GatherTopicInfos` both hit `LoadIndex`) still holds.

---

## Testing Matrix

| Test family | Pins |
|---|---|
| `FileMemoryStoreTests` (`ListScoped_includes_local_entries` :152), `FileMemoryStoreWriteSurfaceTests` (`ListScoped_ProjectsAnEphemeralsKeywordProvenance` :448), `FileMemoryStoreCacheTests` (:123, :114), `EphemeralLifecycleTests` (:89), `StoreFactoryTests` (:27), `EntityFactStorePersistenceTests` (:90/:203), `WholeMemoryStoreTransactionTests` (:127/:138), embedding + V2 path `ListScoped` callers | Forwarding signatures preserve every black-box assertion; `LoadIndex`/`ListEphemeral` behavior identical via hooks. |
| `FakeMemoryStore`-based doubles (`ListScopedCompletenessTests`, `MaintenanceEventSinkTests`, `ContractTypingTests`, `SearchAllSinglePathTests`) | `FakeMemoryStore` is a separate `IMemoryStore` (not a `FileMemoryStore` subclass); moving members off `FileMemoryStore` does not touch the double. No test overrides these members on the sealed `FileMemoryStore`. |
| **Full gate:** `dotnet test Scrinia.sln` | 3,750+ `Scrinia.Tests`, 272 `Retrieval.Benchmarks`, 63 `Embeddings`. Any failure is a behavior regression — do not sweep it under the rug. |

---

## Frozen Constraints

These carry from P1–P6 and remain non-negotiable for P7:

- **NMP/2 unchanged.** No format revision.
- **`.scrinia/` layout unchanged.**
- **CLI single-file surface.** `scri serve` remains the only server surface.
- **MCP 12 + 11 disjoint.** P7 adds no tool.
- **Source-gen JSON only.** No new `[JsonSerializable]`; `FileStoreJsonContext` untouched.
- **Recovery bypasses untouched.** `LoadIndex`'s recovery barrier (`WithRecoveryReadBarrier`), `RecoverScopeUnsafe`, `DiscoverMemoriesRecursive`/`ParseScopeLabel` unchanged.
- **No new public API.** `ListingService` is `internal`; the five forwarding signatures keep their existing visibility.
- **Every new test uses `TestHelpers.StoreScope` isolation.**
- **No public member signature change.** `ListScoped`/`AnyScoped`/`GatherTopicInfos`/`ListTopicArtifacts`/`ListEphemeral` keep their exact signatures on `FileMemoryStore`/`IMemoryStore`; the three TransactionCoordinator family types and their interfaces untouched.

### Explicit do-not-touch (P7 anchors)

`LoadIndex` + `LoadIndexUnderRecoveryBarrier`/`LoadIndexUnsafe` (P4 recovery/index-lock), `WithRecoveryReadBarrier`/`RecoverScopeUnsafe`/`RecoverAppendUnsafeAsync`, the maintenance deletion layer (`ExecuteMaintenanceScopeAsync` and below), `GetStoreDirForScope` (migration-gate `MemoriesRoot` call), `SanitizeName` (broad store seam), `ProjectEphemeralEntry` write-path usage at :3694, the facts materializer glue (`GetEntityFactStore`/`InvalidateEntityFacts`/`ResolveFactSidecarPath`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), the TransactionCoordinator family types + interfaces, `FileStoreJsonContext`, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` members other than the five listing forwarders above, and `SearchAll` (P8 — orchestration only; it forwards to `_listing` but its scorer composition is untouched here).

---

## Out of Scope

- **`SearchAll` scorer composition** (`SearchAll` + its `WeightedAttributeScorer`/`RankerOptions` orchestration) — owns the search scorer; a later phase.
- **`MemoriesRoot` migration gate** — stays in the store (write-path containment).
- **Any public API.** No new public members. No new JSON context entry. No new MCP tool.
