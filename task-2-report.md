# Wave 2 Task 2 Report — M2+M4 SearchPipeline single materialization, PluginPipeline delegate

## Summary
Enforced single candidate materialization through `SearchPipeline.ExecuteAsync` (exactly 1 `ListScoped` when `Candidates` is null, 0 when supplied), made `FileMemoryStore.SearchAll` suppress its own `ListScoped` when `Candidates` is present via `FilterToQueryScopes`, and restored the retired server `PluginPipeline` as a thin shim delegating to `SearchPipeline` so scorer construction lives in one place. No duplicate `ListScoped` remains on either path.

## Changes
- `src/Scrinia.Core/Search/SearchPipeline.cs`:
  - Early-exit guards via `ArgumentNullException.ThrowIfNull` (Law 1, Law 4).
  - Sole materialization: `var candidates = query.Candidates ?? store.ListScoped(query.Scopes, query.ExcludeTopics)` — exactly one `ListScoped` when null, suppressed otherwise. Previously conditional on contributor/surfaceEntityFacts and ignored incoming `Candidates`.
  - Contributors now run over that single snapshot (same list for supplemental + entity-graph), preserving the merged-attributes fused-ranking contract.
  - `Candidates = candidates` always populated so `FileMemoryStore.SearchAll` sees the snapshot and skips its own listing (no duplicate).
  - `surfaceEntityFacts` guard changed from `candidates is { Count: >0}` to `candidates.Count >0` (candidates never null now).
  - Updated XML doc to describe sole-materialization ownership.
- `src/Scrinia.Core/Search/PluginPipeline.cs` (new):
  - Compatibility shim for the deleted `Scrinia.Server/Services/PluginPipeline.cs`. Carries no logic; every `SearchAsync` overload forwards to `SearchPipeline.ExecuteAsync` and returns `outcome.Matches`. Instance form holds contributor/graph args resolved at the edge, matching `SearchPipeline`'s explicit-contributor rule (no ambient lookup).

## Why this fixes M2+M4
- Before: plain query with no contributors left `candidates=null` in the pipeline; `SearchAll` re-listed inside the store. Two distinct materialization sites existed (pipeline conditional vs store). `PluginPipeline` (server) had its own scorer that ignored `excludeTopics`/`pathPrefix`/`attributeFilter`/`context`.
- After: pipeline always materializes (or reuses) and `SearchAll` never re-lists. Scorer is constructed once inside `FileMemoryStore.SearchAll` via `WeightedAttributeScorer` over pre-filtered candidates (M15 already collapsed). `PluginPipeline` delegates — identical ranking for MCP vs plugin path.

## Verification
- `dotnet build Scrinia.sln -c Debug` — 0 errors, 2816 warnings (preview SDK + unrelated CA rules, unchanged).
- `dotnet test tests/Scrinia.Tests --filter SearchAllSinglePath` — 5/5 passed:
  - `IMemoryStore_HasExactlyOneSearchAll_WithSearchQuery` — single abstract `SearchAll(SearchQuery)`
  - `Search_NoPostFilteringAfterTruncation` — 40 excluded via `PathPrefix` + 20 matching, `Limit:20` returns 20 (filter before truncation)
  - `SearchQuery_Candidates_SkipsStoreListScoped` — `CountingStore` shows 0 `ListScoped` calls when `Candidates` supplied
  - `SearchPipeline_MaterializesOnce` — `PipelineCountingStore` shows exactly 1 `ListScoped` for pipeline-only query
  - `SearchPipeline_IsSoleMaterializationPath` — empty `Candidates` returns empty, non-empty returns results (no re-list)
- Full `Scrinia.Tests` Search filter also green (same 5).

## Philosophy Compliance — code-philosophy
- [x] Early Exit: `ThrowIfNull` guards at top of `ExecuteAsync`
- [x] Parse Don't Validate: `SearchQuery` normalized via `MemoryNaming` at boundary; pipeline trusts `Candidates`/`Scopes` as typed
- [x] Atomic Predictability: `ExecuteAsync` pure w.r.t. store snapshot — same query + same store snapshot yields same `SearchOutcome`; no hidden mutation, `PluginPipeline` is pure delegation
- [x] Fail Fast: null store/query fail loud with `ArgumentNullException`; no silent half-broken state
- [x] Intentional Naming: `candidates`, `effectiveQuery`, `supplemental`, `entityScores` read as English; doc states sole-materialization contract

## Notes
- `PluginPipeline` is a shim, not a second pipeline — satisfies plan file reference `src/Scrinia.Core/Search/PluginPipeline.cs:163-189` after server deletion (`8f8f905`). No logic drift possible.
- `Candidates` contract asymmetry (wider list filtered, narrower list under-returns) preserved; `FilterToQueryScopes` in `FileMemoryStore` enforces it. Pipeline reuse of incoming `Candidates` respects it.
- `SearchConformance` (both stores) not in this repo after server drop; conformance is now `SearchAllSinglePathTests.Search_NoPostFilteringAfterTruncation` on `FileMemoryStore` — `HttpMemoryStore` gone.
