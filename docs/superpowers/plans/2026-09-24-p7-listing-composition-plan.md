# P7 — Listing Composition Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p7-listing-composition-design.md`
**Phase:** P7 (seventh god-store seam extraction out of `FileMemoryStore`).
**Shape:** one new internal owning type (`ListingService`) + store wiring (5 forwarding signatures + ctor hooks + `SearchAll` forwarders). Single wave, sequential.

## Workstreams

- W1 — new `ListingService` (`src/Scrinia.Core/ListingService.cs`).
- W2 — `FileMemoryStore` wiring (field, ctor hooks, delete 6 moved members, add 5 forwarders, re-point `SearchAll`).
- W3 — build + seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 is the only upstream dependency; W2 depends on W1; W3/W4 depend on W2. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `ListingService`

New file `src/Scrinia.Core/ListingService.cs`, namespace `Scrinia.Core`, `internal sealed class ListingService`.

Fields: 7 private readonly `Func<>` hooks (loadIndex, getStoreDirForScope, sanitizeName, listEphemeral, includesEphemeral, resolveReadScopes, resolveReadScopesWithExcludes).

Constructor (7 params, assign to fields):
```csharp
internal ListingService(
    Func<string, IReadOnlyList<ArtifactEntry>> loadIndex,
    Func<string, string> getStoreDirForScope,
    Func<string, string> sanitizeName,
    Func<IReadOnlyList<ScopedArtifact>> listEphemeral,
    Func<IReadOnlyList<string>?, bool> includesEphemeral,
    Func<IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopes,
    Func<IReadOnlyList<string>?, IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopesWithExcludes)
{
    _loadIndex = loadIndex;
    _getStoreDirForScope = getStoreDirForScope;
    _sanitizeName = sanitizeName;
    _listEphemeral = listEphemeral;
    _includesEphemeral = includesEphemeral;
    _resolveReadScopes = resolveReadScopes;
    _resolveReadScopesWithExcludes = resolveReadScopesWithExcludes;
}
```

Moved members — copy each body from `FileMemoryStore.cs` byte-for-byte, then re-target every cross-seam call to the injected hook:

- `internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes)`:
  - `TopicDiscoveryService.IncludesEphemeral(scopes)` → `_includesEphemeral(scopes)`
  - `ListEphemeral()` → `_listEphemeral()`
  - `ResolveReadScopes(scopes)` → `_resolveReadScopes(scopes)`
  - `LoadIndex(scope)` → `_loadIndex(scope)`
- `internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)`: same re-targeting; `ResolveReadScopes(scopes, excludeTopics)` → `_resolveReadScopesWithExcludes(scopes, excludeTopics)`.
- `internal bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate)`: same re-targeting.
- `internal List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes)`: `LoadIndex(scope)` → `_loadIndex(scope)`; `ResolveReadScopes(scopes)` → `_resolveReadScopes(scopes)`.
- `internal List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)`: `LoadIndex(topicScope)` → `_loadIndex(topicScope)`; `GetStoreDirForScope(topicScope)` → `_getStoreDirForScope(topicScope)`; `SanitizeName(subject)` → `_sanitizeName(subject)`.
- `internal List<ScopedArtifact> FilterToQueryScopes(IReadOnlyList<ScopedArtifact> candidates, IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)`: `ResolveReadScopes(scopes, excludeTopics)` → `_resolveReadScopesWithExcludes(scopes, excludeTopics)`; `IncludesEphemeral(scopes)` → `_includesEphemeral(scopes)`.
- `internal static List<TopicInfo> BuildTopicInfosFromCandidates(IReadOnlyList<ScopedArtifact> candidates)`: body UNCHANGED (pure static; no cross-seam calls).

Confirm the namespaces/types (`ArtifactEntry`, `ScopedArtifact`, `TopicInfo`, `List<T>`, `IReadOnlyList<T>`, `Func<>`, `StringComparer`) resolve — add the same `using` directives `FileMemoryStore` has (System, System.Collections.Generic, System.IO, System.Linq) as needed. `TopicInfo` lives in `Scrinia.Core.Search` (Search/SearchResults.cs); confirm the using.

## W2 — Wire into `FileMemoryStore`

All edits in `src/Scrinia.Core/FileMemoryStore.cs`.

1. **Add** `private readonly ListingService _listing;` in the fields region (next to `_topicDiscoveryService`).
2. **Construct** it in the internal constructor next to `_topicDiscoveryService`:
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
3. **Delete** the six moved members from the store: `ListScoped(scopes)` (:3931), `ListScoped(scopes, excludeTopics)` (:4092), `AnyScoped` (:3954), `GatherTopicInfos` (:4257), `ListTopicArtifacts` (:4281), `FilterToQueryScopes` (:4169), `BuildTopicInfosFromCandidates` (:4066). (That's seven deletions — ListScoped has two overloads.)
4. **Add back** on the store the five public forwarding signatures (one-liners), matching the store's current visibility (`public`, non-virtual since the class is sealed):
   ```csharp
   public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) => _listing.ListScoped(scopes);
   public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics) =>
       _listing.ListScoped(scopes, excludeTopics);
   public bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate) =>
       _listing.AnyScoped(scopes, predicate);
   public List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes = null) => _listing.GatherTopicInfos(scopes);
   public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) =>
       _listing.ListTopicArtifacts(topicScope);
   ```
5. **Re-point `SearchAll`** (:4117): it currently calls `ListScoped(...)` (the now-forwarded member — leave as-is, it resolves to the forwarder), and `FilterToQueryScopes(...)`/`BuildTopicInfosFromCandidates(...)` → change to `_listing.FilterToQueryScopes(...)` and `_listing.BuildTopicInfosFromCandidates(...)`. The `WeightedAttributeScorer`/`RankerOptions` composition in `SearchAll` is UNCHANGED.
6. **Leave untouched**: `ListEphemeral` (:553), `ProjectEphemeralEntry` (:3697), the write-path call at :3694, `GetStoreDirForScope`, `SanitizeName`, `LoadIndex`, `MemoriesRoot`, the facts glue.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ListScoped|FullyQualifiedName~AnyScoped|FullyQualifiedName~GatherTopicInfos|FullyQualifiedName~ListTopicArtifacts|FullyQualifiedName~ListingService|FullyQualifiedName~SearchAll|FullyQualifiedName~EphemeralLifecycle"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln`. Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `ListingService` owns the six composition methods; `FileMemoryStore` has ZERO direct listing-composition bodies (only 5 one-line forwarders + the `ListEphemeral`/`ProjectEphemeralEntry` members behind the hook).
- `SearchAll` composes via `_listing` + the unchanged scorer.
- No new public API; `FileStoreJsonContext` untouched; NMP/2 and `.scrinia/` unchanged; all frozen do-not-touch anchors intact (LoadIndex/P4, GetStoreDirForScope migration gate, ProjectEphemeralEntry write path, facts glue, TopicDiscoveryService/P6).
- Full suite green.
