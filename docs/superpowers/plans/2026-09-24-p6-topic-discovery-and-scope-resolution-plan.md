# P6 — Topic Discovery + Scope Resolution Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p6-topic-discovery-and-scope-resolution-design.md`
**Phase:** P6 (sixth god-store seam extraction out of `FileMemoryStore`).
**Shape:** one new internal owning type (`TopicDiscoveryService`) + store wiring (forwarders, 9 invalidation swaps, 5 `IncludesEphemeral` forwarders, migration-gate swap) + preserved forwarding surface. Single wave, sequential.

## Workstreams

- W1 — new `TopicDiscoveryService` (`src/Scrinia.Core/TopicDiscoveryService.cs`).
- W2 — `FileMemoryStore` wiring (remove moved members; add field + ctor + forwarding methods; swap 9 `_cachedTopics = null` sites; swap 5 `IncludesEphemeral` calls).
- W3 — build + targeted seam test verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 is the only upstream dependency; W2 depends on W1; W3/W4 depend on W2. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `TopicDiscoveryService`

New file `src/Scrinia.Core/TopicDiscoveryService.cs`, namespace `Scrinia.Core`, `internal sealed class TopicDiscoveryService`.

Fields:
```csharp
private string[]? _cachedTopics;
private readonly Func<string> _memoriesRootProvider;
```

Constructor:
```csharp
internal TopicDiscoveryService(Func<string> memoriesRootProvider) => _memoriesRootProvider = memoriesRootProvider;
```

Moved members — copy the bodies from `FileMemoryStore.cs` byte-for-byte, changing only visibility and the `MemoriesRoot` read:

- `internal string[] DiscoverTopics()`:
  ```csharp
  internal string[] DiscoverTopics()
  {
      if (_cachedTopics is not null)
          return _cachedTopics;

      string memoriesRoot = _memoriesRootProvider();

      var results = new List<string>();
      if (Directory.Exists(memoriesRoot))
          DiscoverMemoriesRecursive(memoriesRoot, "", results);

      _cachedTopics = results.ToArray();
      return _cachedTopics;
  }
  ```
- `internal static void DiscoverMemoriesRecursive(string dir, string relativePath, List<string> results)` — body verbatim from :4425.
- `internal IReadOnlyList<string> NormalizeScopeFilters(string? token)` — body verbatim from :639.
- `internal IReadOnlyList<string> ResolvePathPrefix(string pathPrefix)` — body verbatim from :675.
- `internal static bool MatchesPathPrefix(string topicPart, string prefix)` — body verbatim from :709.
- `internal IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null)` — body verbatim from :722.
- `internal IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)` — body verbatim from :4331.
- `internal static bool IncludesEphemeral(IReadOnlyList<string>? scopes)` — body verbatim from :4063.

New member:
```csharp
internal void InvalidateCachedTopics() => _cachedTopics = null;
```

Confirm the namespaces/types (`List<T>`, `Directory`, `Path`, `IReadOnlyList<string>`, `MemoryNaming.TryBuildScopedTopicScope`, `MemoryNaming.NormalizeFilterList`, `IMemoryStore.BuildExcludedScopeSet`) resolve — add the same `using` directives `FileMemoryStore` already has (System, System.Collections.Generic, System.IO, System.Linq) as needed. `IMemoryStore.BuildExcludedScopeSet` is a static interface extension/implementation; verify the call `IMemoryStore.BuildExcludedScopeSet(excludeTopics)` still compiles from the service (it does — it's a static member of the interface).

## W2 — Wire into `FileMemoryStore`

All edits in `src/Scrinia.Core/FileMemoryStore.cs`.

1. **Add** `private readonly TopicDiscoveryService _topicDiscoveryService;` in the fields region (near the P4/P5 managers).
2. **Construct** it in the internal constructor next to `_scopeIndexCache` / `_maintenancePageServiceCache`:
   ```csharp
   _topicDiscoveryService = new TopicDiscoveryService(() => MemoriesRoot);
   ```
3. **Delete** these members from the store (their bodies now live in the service): `DiscoverTopics()`, `DiscoverMemoriesRecursive(...)`, `NormalizeScopeFilters(...)`, `ResolvePathPrefix(...)`, `MatchesPathPrefix(...)`, both `ResolveReadScopes` overloads, `IncludesEphemeral(...)`.
4. **Add back** on the store the exact same-signature forwarding members (bodies are one-liners):
   ```csharp
   public virtual string[] DiscoverTopics() => _topicDiscoveryService.DiscoverTopics();
   public virtual IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null) =>
       _topicDiscoveryService.ResolveReadScopes(scopes);
   public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics) =>
       _topicDiscoveryService.ResolveReadScopes(scopes, excludeTopics);
   internal IReadOnlyList<string> NormalizeScopeFilters(string? token) =>
       _topicDiscoveryService.NormalizeScopeFilters(token);
   ```
   Keep `DiscoverTopics` and the 1-arg `ResolveReadScopes` `public virtual` (FakeMemoryStore :77/:79 and `ListScopedCompletenessTests` :198 override them). Keep `NormalizeScopeFilters` `internal` (`FileMemoryStoreTests` :1135 calls it directly on `_store`).
5. **Swap the 9 `_cachedTopics = null` sites** for `_topicDiscoveryService.InvalidateCachedTopics()`, keeping each site's guard:
   - :610 (inside `MemoriesRoot`'s `if (result.Ran)`, after `_scopeIndexCache.Clear()`).
   - :1659 (`PersistMigratedEntry`, inside `if (scope.StartsWith("local:"))`).
   - :1849 (`InvalidateCaches`, unconditional).
   - :1864 (`InvalidateScopeCacheAfterRollback`, inside `if (scope.StartsWith("local:"))`).
   - :1880 (`InvalidateCommittedScope`, inside `if (scope.StartsWith("local:"))`).
   - :1957 (`PublishIndexUnsafe`, inside `if (scope.StartsWith("local:"))`).
   - :2062 (`UpsertCore`, inside `if (scopeWasEmpty && scope.StartsWith("local:"))`).
   - :2139 (`Remove`, inside `if (scope.StartsWith("local:") && !Directory...empty)`).
   - :4878 (`InvalidateRecoveredScopeUnsafe`, inside `if (scope.StartsWith("local:"))`).
   Use `grep -n "_cachedTopics = null" src/Scrinia.Core/FileMemoryStore.cs` to find every remaining occurrence before and after — there must be ZERO `_cachedTopics = null` assignments left in the store after W2; all nine become `InvalidateCachedTopics()`.
6. **Swap the 5 `IncludesEphemeral(...)` calls** for `_topicDiscoveryService.IncludesEphemeral(...)`:
   - :4077 (`ListScoped`), :4098 (`AnyScoped`), :4136 (`GetEntityFactStore`), :4242 (`ListScoped(excludeTopics)`), :4317 (`FilterToQueryScopes`).
   `grep -n "IncludesEphemeral(" src/Scrinia.Core/FileMemoryStore.cs` should show only the five call sites (the definition is deleted in step 3).
7. `MemoriesRoot` (:589) keeps its migration-gate body UNCHANGED except :610 (step 5). `GetStoreDirForScope` containment (:4006–4007) and the gate (:622) are untouched.

## W3 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DiscoverTopics|FullyQualifiedName~ResolveReadScopes|FullyQualifiedName~NormalizeScopeFilters|FullyQualifiedName~TopicCache|FullyQualifiedName~ListScoped|FullyQualifiedName~StoreLayoutMigration|FullyQualifiedName~DualMode"
   ```
   Confirm `Passed!` / 0 failed.

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln`. Report the final `Passed!`/`Failed!` summary line for each of the three test projects (Scrinia.Tests, Scrinia.Retrieval.Benchmarks.Tests, Scrinia.Plugin.Embeddings.Tests). The known pre-existing embeddings parallel-load flake (`ComputeScoresAsync_ConcurrentWithDispose_...`) may fail under full-suite load; confirm any such failure passes in isolation (it is zero-overlap with this seam). Any deterministic failure is a regression — fix it.

## Acceptance

- `TopicDiscoveryService` is the SOLE owner of `_cachedTopics`; the store never assigns `_cachedTopics` again.
- `DiscoverTopics`/`ResolveReadScopes`/`NormalizeScopeFilters` forwarding signatures unchanged; `FakeMemoryStore` and test overrides still compile and behave identically.
- No new public API; `FileStoreJsonContext` untouched; NMP/2 and `.scrinia/` unchanged; all frozen do-not-touch anchors intact (migration gate, `LoadIndex`, recovery, deletion layer, facts glue, listing/search composition).
- Full suite green.
