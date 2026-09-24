# P8 — Search Orchestration Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P0–P7 complete).
**Phase:** P8 (eighth seam extraction).
**Goal:** move the search *orchestration* (`SearchAll`) off the store into a new `SearchService` owning type, leaving the store a one-line forwarder. The scorer (`WeightedAttributeScorer`) and its static search helpers (`SearchResultFilter`, `RankerOptions`, `SearchQuery`) stay put. This is the LAST orchestration seam before the audit.

## Why this phase

`SearchAll` is the store's search entry point and the orchestration hub that ties together candidate listing, path/attribute filtering, topic building, and scoring. P7 already extracted listing composition; `SearchAll` still calls `_listing.*` directly and touches the store (`this` → `SearchResultFilter.CandidateMatches`). P8 removes that last orchestration coupling so the store forwards search to a dedicated owner.

## The seam

`SearchService` owns `SearchAll(SearchQuery)`. The store's `SearchAll(SearchQuery)` becomes a public one-line forwarder: `public IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => _search.SearchAll(query);`.

### New type: `SearchService`

New file `src/Scrinia.Core/SearchService.cs`, namespace `Scrinia.Core`, `internal sealed class SearchService`.

Fields: `private readonly ListingService _listing;`, `private readonly Func<string, string, string> _formatQualifiedLabel;`, `private readonly Func<RankerOptions> _rankerOptions;`.

Constructor (3 params):
```csharp
internal SearchService(
    ListingService listing,
    Func<string, string, string> formatQualifiedLabel,
    Func<RankerOptions> rankerOptions)
{
    _listing = listing;
    _formatQualifiedLabel = formatQualifiedLabel;
    _rankerOptions = rankerOptions;
}
```

Moved member — copy `FileMemoryStore.SearchAll` (current lines 4052–4095) byte-for-byte, then re-target:
- `ListScoped(query.Scopes, query.ExcludeTopics)` → `_listing.ListScoped(query.Scopes, query.ExcludeTopics)`
- `SearchResultFilter.CandidateMatches(this, c, pathPrefix, query.AttributeFilter)` → `SearchResultFilter.CandidateMatches(c, pathPrefix, query.AttributeFilter, _formatQualifiedLabel)`
- `ListingService.BuildTopicInfosFromCandidates(allCandidates)` → unchanged (static call on the type)
- `new WeightedAttributeScorer(RankerOptions)` → `new WeightedAttributeScorer(_rankerOptions())`

The scorer composition (`WeightedAttributeScorer.SearchAll(query.Query, entryCandidates, topics, query.Limit, query.SupplementalScores, query.EntityGraphScores)`) is UNCHANGED. The `query.Candidates` supplied-candidates branch (M2/M4 no-double-list optimization, lines 4068–4071) is preserved verbatim.

### Static-helper refactor (the ONE edit to a helper)

`SearchResultFilter.CandidateMatches` takes an `IMemoryStore store` today solely to call `store.FormatQualifiedName(scope, subject)`. Refactor it to drop the store and take a `Func<string, string, string> formatQualifiedLabel` instead:

```csharp
public static bool CandidateMatches(
    ScopedArtifact candidate,
    string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter,
    Func<string, string, string> formatQualifiedLabel)
{
    string label = candidate.Scope == "ephemeral"
        ? $"~{candidate.Entry.Name}"
        : formatQualifiedLabel(candidate.Scope, candidate.Entry.Name);
    return MatchesPathPrefix(label, pathPrefix)
        && MatchesAttributes(candidate.Entry.Attributes, attributeFilter);
}
```

This is the only signature change in the search-helper family. `CandidateMatches` has a single caller (`SearchAll`), so no other call site breaks. `TopicMatches`/`MatchesPathPrefix`/`MatchesAttributes` are UNCHANGED. `MemoryNaming.FormatQualifiedName` (frozen recovery barrier) is only *called through* the injected hook — never modified.

## Store wiring (FileMemoryStore.cs)

All edits in `src/Scrinia.Core/FileMemoryStore.cs`.

1. **Add** `private readonly SearchService _search;` in the fields region (next to `_listing`).
2. **Construct** it in the internal ctor AFTER `RankerOptions` is assigned (line 306, after `_listing` at 291):
   ```csharp
   _search = new SearchService(
       _listing,
       (scope, subject) => FormatQualifiedName(scope, subject),
       () => RankerOptions);
   ```
   `FormatQualifiedName` (store public method :407) delegates to `MemoryNaming.FormatQualifiedName` — the hook calls the static form through the store's own wrapper, so the frozen recovery barrier is untouched.
3. **Delete** the current `SearchAll` body (lines 4052–4095).
4. **Add back** the public one-line forwarder (same signature as `IMemoryStore.SearchAll(SearchQuery)`):
   ```csharp
   public IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => _search.SearchAll(query);
   ```
5. **Leave untouched**: the `RankerOptions` public property (still the store's tunable surface, read by the hook), `ListScoped` public forwarders (SearchPipeline line 55 still calls them), `SearchResultFilter.TopicMatches`, `WeightedAttributeScorer`, `RankerOptions`, `SearchQuery`, `ScoreSupplementalScores`, the contributor `store` pass-through in `SearchPipeline` (line 60), `GetEntityFactStore`, the `WeightedAttributeScorer` doc comment at :268–269 (reword to say "read by SearchService").

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + `DiscoverMemoriesRecursive`/`ParseScopeLabel` recovery barrier (P4), `GetStoreDirForScope`/`MemoriesRoot` migration gate, `SanitizeName`, `ProjectEphemeralEntry` write path (`ProjectEphemeralEntry`/`ListEphemeral`), `FormatQualifiedName` (MemoryNaming), facts glue (`GetEntityFactStore`/`InvalidateEntityFacts`), `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext`, NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, `WeightedAttributeScorer` + its `SearchAll` scoring, `RankerOptions` + `SearchQuery` + `SearchResultFilter.TopicMatches`, `SearchPipeline` orchestration.

## Acceptance

- `SearchService` owns `SearchAll` orchestration; the store has ZERO search-orchestration body (only a one-line forwarder).
- `SearchResultFilter.CandidateMatches` no longer depends on the store instance.
- No new public API on the store or interfaces; `FileStoreJsonContext` untouched; `WeightedAttributeScorer`/`RankerOptions`/`SearchQuery`/`SearchResultFilter.TopicMatches` bodies untouched (only `CandidateMatches` signature changes, single-call-site).
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchAll|FullyQualifiedName~SearchService|FullyQualifiedName~CandidateMatches|FullyQualifiedName~SearchPipeline"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln`. Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
