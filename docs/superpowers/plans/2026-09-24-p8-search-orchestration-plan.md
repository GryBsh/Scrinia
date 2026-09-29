# P8 — Search Orchestration Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p8-search-orchestration-seam.md`
**Phase:** P8 (eighth god-store seam extraction).
**Shape:** one new internal owning type (`SearchService`) + one static-helper signature change (`SearchResultFilter.CandidateMatches`) + store wiring (1 field, 1 ctor line, 1 deleted member, 1 forwarder). Single wave, sequential.

## Workstreams

- W1 — new `SearchService` (`src/Scrinia.Core/SearchService.cs`).
- W2 — `SearchResultFilter.CandidateMatches` refactor (drop `IMemoryStore`, take `Func<string,string,string>`).
- W3 — `FileMemoryStore` wiring (field, ctor line, delete `SearchAll` body, add forwarder).
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 are sequential (W3 depends on W1/W2 being compiled); W4/W5 depend on W3. No parallelism benefit — execute sequentially.

## W1 — Create `SearchService`

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

Moved member — copy `FileMemoryStore.SearchAll` (current lines **4052–4095**) byte-for-byte, then re-target:
- `ListScoped(query.Scopes, query.ExcludeTopics)` → `_listing.ListScoped(query.Scopes, query.ExcludeTopics)`
- `SearchResultFilter.CandidateMatches(this, c, pathPrefix, query.AttributeFilter)` → `SearchResultFilter.CandidateMatches(c, pathPrefix, query.AttributeFilter, _formatQualifiedLabel)`
- `ListingService.BuildTopicInfosFromCandidates(allCandidates)` → unchanged (static call on the type; confirm `ListingService.BuildTopicInfosFromCandidates` is `internal static` — it is, from P7)
- `new WeightedAttributeScorer(RankerOptions)` → `new WeightedAttributeScorer(_rankerOptions())`

The `query.Candidates` supplied-candidates branch (M2/M4 no-double-list optimization) is preserved verbatim. `WeightedAttributeScorer.SearchAll` composition is UNCHANGED.

Confirm the namespaces/types (`SearchResult`, `SearchQuery`, `ScopedArtifact`, `TopicInfo`, `WeightedAttributeScorer`, `RankerOptions`, `IReadOnlyList<T>`, `IDictionary`) resolve — add the same `using` directives `FileMemoryStore` has (System, System.Collections.Generic, System.Linq) as needed. `WeightedAttributeScorer`/`RankerOptions`/`SearchResult`/`SearchQuery` live in `Scrinia.Core.Search` (grep the current `using` block at the top of `FileMemoryStore.cs` lines 1–15 to mirror it). `ListingService` is `Scrinia.Core` (same namespace as `SearchService`).

## W2 — Refactor `SearchResultFilter.CandidateMatches`

Edit `src/Scrinia.Core/Search/SearchResultFilter.cs`. Change the signature from:
```csharp
public static bool CandidateMatches(
    IMemoryStore store, ScopedArtifact candidate,
    string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
```
to:
```csharp
public static bool CandidateMatches(
    ScopedArtifact candidate,
    string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter,
    Func<string, string, string> formatQualifiedLabel)
```
and the body from:
```csharp
string label = candidate.Scope == "ephemeral"
    ? $"~{candidate.Entry.Name}"
    : store.FormatQualifiedName(candidate.Scope, candidate.Entry.Name);
```
to:
```csharp
string label = candidate.Scope == "ephemeral"
    ? $"~{candidate.Entry.Name}"
    : formatQualifiedLabel(candidate.Scope, candidate.Entry.Name);
```
Remove any now-unused `using Scrinia.Core;` if it becomes unreferenced by this file (the file currently has `using Scrinia.Core.Models;` at line 1 — keep it; `IMemoryStore` was referenced only via the `store` param, which is gone; confirm the `using Scrinia.Core;` is not needed elsewhere in the file — it isn't, so drop it). `TopicMatches`/`MatchesPathPrefix`/`MatchesAttributes` UNCHANGED.

## W3 — Wire into `FileMemoryStore`

All edits in `src/Scrinia.Core/FileMemoryStore.cs`.

1. **Add** `private readonly SearchService _search;` in the fields region (next to `_listing`).
2. **Construct** it in the internal ctor AFTER `RankerOptions` assignment (line 306, after `_listing` at 291):
   ```csharp
   _search = new SearchService(
       _listing,
       (scope, subject) => FormatQualifiedName(scope, subject),
       () => RankerOptions);
   ```
   `FormatQualifiedName` is the store's public method (:407) delegating to `MemoryNaming.FormatQualifiedName`.
3. **Delete** the current `SearchAll` body (lines 4052–4095).
4. **Add back** the public one-line forwarder (same signature as `IMemoryStore.SearchAll(SearchQuery)`):
   ```csharp
   public IReadOnlyList<SearchResult> SearchAll(SearchQuery query) => _search.SearchAll(query);
   ```
   Place the forwarder where the old body was (near line 4052).
5. **Leave untouched**: `RankerOptions` public property (:273), `ListScoped` public forwarders (SearchPipeline :55 still calls them), `SearchResultFilter.TopicMatches`, `WeightedAttributeScorer`, `SearchQuery`, `SearchPipeline`, `GetEntityFactStore`, the `WeightedAttributeScorer` doc comment at :268–269 (reword to say "read by SearchService").

## W4 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchAll|FullyQualifiedName~SearchService|FullyQualifiedName~CandidateMatches|FullyQualifiedName~SearchPipeline"
   ```
   Confirm `Passed!` / 0 failed.

## W5 — Full verification (mandatory)

Full `dotnet test Scrinia.sln`. Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `SearchService` owns `SearchAll` orchestration; `FileMemoryStore` has ZERO search-orchestration body (only the one-line forwarder).
- `SearchResultFilter.CandidateMatches` no longer depends on the store instance.
- No new public API; `FileStoreJsonContext` untouched; `WeightedAttributeScorer`/`RankerOptions`/`SearchQuery`/`SearchResultFilter.TopicMatches` bodies untouched (only `CandidateMatches` signature changes, single-call-site).
- Full suite green.
