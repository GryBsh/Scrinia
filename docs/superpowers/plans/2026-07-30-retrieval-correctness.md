# Retrieval Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four confirmed retrieval-correctness defects from the 2026-07-29 whole-codebase review (plus the append-vector corruption found during verification), so search filtering, MMR diversity, and embedding-vector integrity behave correctly.

**Architecture:** Four independent fixes across two subsystems. Search ranking (`Scrinia.Core.Search` + the MCP search pipeline): MMR uses the chunk's own term-frequencies, and `pathPrefix`/`attributeFilter` filter *before* the top-K cut (symmetric with how `excludeTopics` already pre-filters candidates). Embeddings integrity (`Scrinia.Plugin.Embeddings` + `Scrinia.Core.Embeddings`): the two live-serving `VectorStore` construction sites sign the store with the active provider's signature, and the incremental embed path clears stale vectors before re-embedding (shrink) and re-embeds the full memory on append.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, `Scrinia.Core` (search + embeddings), `Scrinia.Plugin.Embeddings` (out-of-process Vulkan embedding MCP server + CLI).

## Global Constraints

- No new NuGet dependencies. The solution-wide vulnerability audit is clean (Stream 3) — **never** use `-p:NuGetAudit=false`; a restore that trips the audit is a real failure.
- `Directory.Build.props` sets `TreatWarningsAsErrors=true` for all non-test/benchmark src projects. Warnings are build failures.
- `Scrinia.Merge` is **not** touched by this stream.
- **A2 invariant (load-bearing):** for any active `pathPrefix`/`attributeFilter`, a search returns up to `limit` matching results whenever at least `limit` matches exist — not the smaller post-`limit` remnant.
- **B1 invariant (load-bearing):** the live-serving vector store is signed via the **same** `ChunkedSignature` composition the reindexer uses, and the signature is `null` when the provider is unavailable (a transient provider-load failure must never quarantine good vectors).
- **B1 migration (expected):** the first run after B1 quarantines existing plugin vector files that carry an empty/unsigned signature (`vectors.bin` → `vectors.bin.stale-*`) and rebuilds them on the next reindex. This is the already-documented one-time upgrade cost of `VectorStore`'s signature check — not a regression.
- MMR chunk term-frequency falls back to the parent entry's TF when the chunk carries none (legacy v2 chunks).
- The user's four uncommitted working-tree files (`.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/prompts/guide.md`) are theirs — never stage or commit them. Commit only the files each task names.

---

### Task 1 (A1): MMR chunk diversity uses chunk term-frequencies

**Files:**
- Modify: `src/Scrinia.Core/Search/MmrReranker.cs:176-181` (`GetTf`)
- Test: `tests/Scrinia.Tests/Search/MmrRerankerTests.cs`

**Interfaces:**
- Consumes: `ChunkEntryResult(ScopedArtifact ParentItem, ChunkEntry Chunk, int TotalChunks, double Score)`; `ChunkEntry.TermFrequencies` is `Dictionary<string,int>?` (already populated — the BM25 scorer reads it).
- Produces: no signature change; `GetTf` behavior change only.

**Background:** `GetTf` returns, for a `ChunkEntryResult`, the *parent document's* `TermFrequencies`. MMR's TF-cosine then treats every chunk of one document as identical (cosine 1.0) and wrongly suppresses diverse chunk hits. The chunk carries its own `TermFrequencies`; use it, with a parent fallback for legacy chunks that have none.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/Search/MmrRerankerTests.cs` (inside the existing test class; reuse existing `using`s — `Scrinia.Core.Search`, `Scrinia.Core.Models`, `Xunit`, `FluentAssertions`):

```csharp
[Fact]
public void Rerank_TwoChunksOfSameParent_WithDifferentChunkTf_AreNotTreatedAsDuplicates()
{
    // Same parent entry, but each chunk has a DISTINCT term-frequency profile.
    var parentTf = new Dictionary<string, int> { ["auth"] = 5, ["token"] = 5 };
    var entry = new ArtifactEntry("doc", "", 100, 2, DateTimeOffset.UtcNow)
    {
        TermFrequencies = parentTf,
    };
    var parent = new ScopedArtifact("local:memory", entry);

    var chunkA = new ChunkEntry(0, "", null, new Dictionary<string, int> { ["auth"] = 10 });
    var chunkB = new ChunkEntry(1, "", null, new Dictionary<string, int> { ["token"] = 10 });

    var results = new List<SearchResult>
    {
        new ChunkEntryResult(parent, chunkA, 2, 100.0),
        new ChunkEntryResult(parent, chunkB, 2, 90.0),
    };

    // With chunk-level TF, chunkA and chunkB are orthogonal (cosine 0), so pure-diversity
    // MMR (lambda=0 after the first pick) keeps BOTH — neither is redundant with the other.
    var reranked = MmrReranker.Rerank(results, k: 2, lambda: 0.5);

    reranked.Should().HaveCount(2);
    reranked.Should().Contain(r => r is ChunkEntryResult c && c.Chunk.ChunkIndex == 0);
    reranked.Should().Contain(r => r is ChunkEntryResult c && c.Chunk.ChunkIndex == 1);
}

[Fact]
public void Rerank_ChunkWithNullTf_FallsBackToParentTf()
{
    var parentTf = new Dictionary<string, int> { ["auth"] = 3 };
    var entry = new ArtifactEntry("doc", "", 100, 1, DateTimeOffset.UtcNow)
    {
        TermFrequencies = parentTf,
    };
    var parent = new ScopedArtifact("local:memory", entry);

    // Chunk carries no TF of its own -> GetTf must fall back to the parent dict, not throw.
    var chunk = new ChunkEntry(0, "", null, null);
    var results = new List<SearchResult> { new ChunkEntryResult(parent, chunk, 1, 50.0) };

    var reranked = MmrReranker.Rerank(results, k: 1, lambda: 0.5);

    reranked.Should().ContainSingle();
}
```

Confirm the `ChunkEntry` and `ArtifactEntry` constructor/initializer shapes against the current source before running — `ChunkEntry(int ChunkIndex, string ContentPreview, string[]? Keywords, Dictionary<string,int>? TermFrequencies)` and `ArtifactEntry` with an object-initializer `TermFrequencies`. Adjust the literal argument lists to match exactly (do not change the assertions).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MmrRerankerTests"`
Expected: `Rerank_TwoChunksOfSameParent_WithDifferentChunkTf_AreNotTreatedAsDuplicates` FAILS (today both chunks share `parentTf`, so they look identical and MMR may drop one). The null-TF test should already PASS (it exercises the fallback) — that is fine; it guards the fallback stays.

- [ ] **Step 3: Apply the fix**

In `src/Scrinia.Core/Search/MmrReranker.cs`, change the `ChunkEntryResult` arm of `GetTf`:

```csharp
private static Dictionary<string, int>? GetTf(SearchResult r) => r switch
{
    EntryResult er => er.Item.Entry.TermFrequencies,
    ChunkEntryResult cr => cr.Chunk.TermFrequencies ?? cr.ParentItem.Entry.TermFrequencies,
    _ => null,
};
```

Update the `<para>` in the class doc-comment that says similarity is TF-cosine over `ArtifactEntry.TermFrequencies` to note that chunk results use the chunk's own TF (falling back to the parent entry's TF when the chunk has none).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MmrRerankerTests"`
Expected: PASS (all MMR tests, including the pre-existing ones).

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/Search/MmrReranker.cs tests/Scrinia.Tests/Search/MmrRerankerTests.cs
rtk git commit -m "fix(search): MMR diversity uses chunk TF, not parent TF"
```

---

### Task 2 (A2): pathPrefix / attributeFilter filter before the top-K cut

**Files:**
- Create: `src/Scrinia.Core/Search/SearchResultFilter.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (add one `SearchAll` overload with a default post-filter impl)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (override the new overload with pre-limit candidate/topic filtering)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`ExecuteSearchAsync` calls the new overload; delete `ApplyPostFilters`, `MatchesPathPrefix`, `MatchesAttributes`)
- Test: `tests/Scrinia.Tests/SearchPathAttributeFilterTests.cs` (new)

**Interfaces:**
- Consumes: `ListScoped(string? scopes, string? excludeTopics)` (FileMemoryStore), `WeightedAttributeScorer.SearchAll(query, candidates, topics, limit, supplementalScores, entityGraphScores)`, `IMemoryStore.FormatQualifiedName(scope, subject)`, `MemoryNaming.FormatScopeLabel(scope)`, `BuildTopicInfosFromCandidates(...)`, `ArtifactEntry.Attributes` (`Dictionary<string,string[]>?`), `TopicInfo.Scope`.
- Produces:
  - `SearchResultFilter.CandidateMatches(IMemoryStore store, ScopedArtifact candidate, string? pathPrefix, IReadOnlyDictionary<string,string[]>? attributeFilter) -> bool`
  - `SearchResultFilter.TopicMatches(TopicInfo topic, string? pathPrefix) -> bool`
  - `SearchResultFilter.Matches(IMemoryStore store, SearchResult result, string? pathPrefix, IReadOnlyDictionary<string,string[]>? attributeFilter) -> bool`
  - New `IMemoryStore.SearchAll(string query, string? scopes, int limit, string? excludeTopics, IReadOnlyDictionary<string,double>? supplementalScores, IReadOnlyDictionary<string,double>? entityGraphScores, string? pathPrefix, IReadOnlyDictionary<string,string[]>? attributeFilter)` (8 params, **no** default values, to avoid overload ambiguity with the existing 6-param overload).

**Background:** `ExecuteSearchAsync` calls `store.SearchAll(..., limit, ...)` — which cuts to the top `limit` inside `WeightedAttributeScorer`'s size-K min-heap — and *then* runs `ApplyPostFilters`. So a filtered search under-fills. `excludeTopics` is already applied pre-limit by filtering the candidate list (`ListScoped(scopes, excludeTopics)`). Every `pathPrefix`/`attributeFilter` dimension is a property of the parent candidate (entry path/attributes) or the topic (scope label) — so filtering the candidate and topic lists *before* the scorer runs is exactly equivalent to filtering the results afterward, and it fills to `limit`. Chunk results inherit their parent's path and attributes, so filtering parents covers chunk results too.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/SearchPathAttributeFilterTests.cs`. Model the store setup on `tests/Scrinia.Tests/SearchExcludeTopicsTests.cs` (read it first for the exact `FileMemoryStore` seeding helpers and namespace conventions). The load-bearing assertion is the fill test:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class SearchPathAttributeFilterTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public SearchPathAttributeFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_filter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Seed(string scope, string name, string description)
    {
        // All entries share the query term "alpha" so relevance is non-zero for every one;
        // the filter, not relevance, must decide what survives.
        var entry = new ArtifactEntry(name, description, description.Length, 1, DateTimeOffset.UtcNow)
        {
            TermFrequencies = new Dictionary<string, int> { ["alpha"] = 3 },
        };
        _store.Upsert(entry, scope);
    }

    [Fact]
    public void SearchAll_WithPathPrefix_FillsUpToLimit_NotThePostLimitRemnant()
    {
        // 5 matching entries under local:memory/keep + 5 decoys under local:memory/drop.
        for (int i = 0; i < 5; i++) Seed("local:memory/keep", $"k{i}", "alpha note");
        for (int i = 0; i < 5; i++) Seed("local:memory/drop", $"d{i}", "alpha note");

        var results = _store.SearchAll(
            query: "alpha", scopes: null, limit: 3, excludeTopics: null,
            supplementalScores: null, entityGraphScores: null,
            pathPrefix: "/memory/keep", attributeFilter: null);

        // Old behavior: top-3 by score are cut FIRST, then path-filtered -> often < 3 kept.
        // Correct behavior: 3 keep-entries returned, every one under the prefix.
        results.Should().HaveCount(3);
        results.OfType<EntryResult>()
            .Should().OnlyContain(r => r.Item.Scope == "local:memory/keep");
    }

    [Fact]
    public void SearchAll_WithAttributeFilter_ReturnsOnlyMatchingEntries()
    {
        var tagged = new ArtifactEntry("t1", "alpha", 5, 1, DateTimeOffset.UtcNow)
        {
            TermFrequencies = new Dictionary<string, int> { ["alpha"] = 3 },
            Attributes = new Dictionary<string, string[]> { ["kind"] = new[] { "spec" } },
        };
        _store.Upsert(tagged, "local:memory");
        Seed("local:memory", "u1", "alpha"); // no attributes

        var results = _store.SearchAll(
            query: "alpha", scopes: null, limit: 10, excludeTopics: null,
            supplementalScores: null, entityGraphScores: null,
            pathPrefix: null,
            attributeFilter: new Dictionary<string, string[]> { ["kind"] = new[] { "spec" } });

        results.OfType<EntryResult>().Should().OnlyContain(r => r.Item.Entry.Name == "t1");
    }

    [Fact]
    public void SearchAll_NoFilter_MatchesUnfilteredOverload()
    {
        for (int i = 0; i < 4; i++) Seed("local:memory", $"m{i}", "alpha");

        var filtered = _store.SearchAll(
            "alpha", null, 10, null, null, null, pathPrefix: null, attributeFilter: null);
        var plain = _store.SearchAll("alpha", null, 10, excludeTopics: null);

        filtered.Select(r => r.Score).Should().Equal(plain.Select(r => r.Score));
    }
}
```

Verify `ArtifactEntry`'s `Attributes` initializer name/type against the source before running (it is `Dictionary<string,string[]>? Attributes`). Adjust `Upsert` calls if `SearchExcludeTopicsTests` shows a different seeding helper is required for entries to be searchable (some stores need the on-disk artifact written — mirror whatever that test does).

- [ ] **Step 2: Run the test to verify it fails to compile / fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchPathAttributeFilterTests"`
Expected: FAILS to compile — the 8-parameter `SearchAll` overload does not exist yet.

- [ ] **Step 3: Create the Core filter helper**

Create `src/Scrinia.Core/Search/SearchResultFilter.cs`:

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Path-prefix and attribute predicates for search results and candidates. Relocated from the
/// MCP layer into Core so the store can apply them BEFORE the top-K cut (symmetric with how
/// excludeTopics pre-filters candidates), rather than post-filtering an already-limited result
/// set. Path-prefix is a case-insensitive StartsWith on the qualified label (ephemeral entries
/// labelled "~{name}", topics via MemoryNaming.FormatScopeLabel). Attribute filter is AND across
/// keys, OR within each key's values, case-insensitive; it excludes topics (which carry no
/// attributes).
/// </summary>
public static class SearchResultFilter
{
    /// <summary>Keep-predicate for an entry candidate (covers its entry and chunk results —
    /// both inherit the parent's path and attributes).</summary>
    public static bool CandidateMatches(
        IMemoryStore store, ScopedArtifact candidate,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        string label = candidate.Scope == "ephemeral"
            ? $"~{candidate.Entry.Name}"
            : store.FormatQualifiedName(candidate.Scope, candidate.Entry.Name);
        return MatchesPathPrefix(label, pathPrefix)
            && MatchesAttributes(candidate.Entry.Attributes, attributeFilter);
    }

    /// <summary>Keep-predicate for a topic. Topics carry no attributes, so a topic is kept only
    /// when no attribute filter is active AND its scope label matches the path prefix. Callers
    /// that have an active attribute filter should drop all topics without calling this.</summary>
    public static bool TopicMatches(TopicInfo topic, string? pathPrefix)
        => MatchesPathPrefix(MemoryNaming.FormatScopeLabel(topic.Scope), pathPrefix);

    /// <summary>Keep-predicate over a materialized <see cref="SearchResult"/>. Used by the
    /// default <see cref="IMemoryStore"/> post-filter fallback for stores without pre-limit
    /// candidate filtering.</summary>
    public static bool Matches(
        IMemoryStore store, SearchResult result,
        string? pathPrefix, IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        string label = result switch
        {
            EntryResult er => er.Item.Scope == "ephemeral"
                ? $"~{er.Item.Entry.Name}"
                : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name),
            ChunkEntryResult cr => cr.ParentItem.Scope == "ephemeral"
                ? $"~{cr.ParentItem.Entry.Name}"
                : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name),
            TopicResult tr => MemoryNaming.FormatScopeLabel(tr.Scope),
            _ => string.Empty,
        };
        if (!MatchesPathPrefix(label, pathPrefix)) return false;

        Dictionary<string, string[]>? attributes = result switch
        {
            EntryResult er => er.Item.Entry.Attributes,
            ChunkEntryResult cr => cr.ParentItem.Entry.Attributes,
            _ => null, // TopicResult: no attributes -> excluded when a filter is active
        };
        return MatchesAttributes(attributes, attributeFilter);
    }

    private static bool MatchesPathPrefix(string label, string? pathPrefix)
        => string.IsNullOrWhiteSpace(pathPrefix)
        || label.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAttributes(
        Dictionary<string, string[]>? attributes, IReadOnlyDictionary<string, string[]>? filter)
    {
        if (filter is null || filter.Count == 0) return true;
        if (attributes is null || attributes.Count == 0) return false;
        foreach (var want in filter)
        {
            if (!attributes.TryGetValue(want.Key.Trim().ToLowerInvariant(), out var have)
                || have is null || have.Length == 0)
                return false;
            if (want.Value is { Length: > 0 })
            {
                bool any = false;
                foreach (string wv in want.Value)
                {
                    foreach (string hv in have)
                        if (string.Equals(hv, wv, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                    if (any) break;
                }
                if (!any) return false;
            }
        }
        return true;
    }
}
```

This is a faithful relocation of the current `MemoryTools.Core.MatchesPathPrefix` / `MatchesAttributes` bodies — keep the exact ephemeral labelling, key normalization (`Trim().ToLowerInvariant()`), and AND/OR semantics.

- [ ] **Step 4: Add the new IMemoryStore overload with a default post-filter impl**

In `src/Scrinia.Core/IMemoryStore.cs`, next to the existing `SearchAll(..., supplementalScores, entityGraphScores)` overload, add:

```csharp
/// <summary>
/// Searches with supplemental + entity-graph scores, then keeps only results matching the
/// path-prefix / attribute filter. The default implementation post-filters after the base
/// SearchAll (correct filtering, but it cannot guarantee fill-to-limit for stores without a
/// scorer). <see cref="FileMemoryStore"/> overrides this to filter candidates BEFORE the top-K
/// cut so a filtered search returns up to <paramref name="limit"/> matching results.
/// </summary>
IReadOnlyList<SearchResult> SearchAll(
    string query, string? scopes, int limit, string? excludeTopics,
    IReadOnlyDictionary<string, double>? supplementalScores,
    IReadOnlyDictionary<string, double>? entityGraphScores,
    string? pathPrefix,
    IReadOnlyDictionary<string, string[]>? attributeFilter)
{
    var results = SearchAll(query, scopes, limit, excludeTopics, supplementalScores, entityGraphScores);
    bool hasPath = !string.IsNullOrWhiteSpace(pathPrefix);
    bool hasAttr = attributeFilter is { Count: > 0 };
    if (!hasPath && !hasAttr) return results;
    return results.Where(r => SearchResultFilter.Matches(this, r, pathPrefix, attributeFilter)).ToList();
}
```

Add `using Scrinia.Core.Search;` to `IMemoryStore.cs` if it is not already present.

- [ ] **Step 5: Override in FileMemoryStore with pre-limit filtering**

In `src/Scrinia.Core/FileMemoryStore.cs`, after the existing `excludeTopics + supplementalScores` overload (~L1583-1600), add:

```csharp
/// <summary>
/// Pre-limit path/attribute filtering: filters the candidate and topic lists BEFORE the scorer
/// applies the top-K cut, so a selective filter fills to <paramref name="limit"/> instead of
/// returning the post-limit remnant. Every filter dimension is a property of the parent
/// candidate (path/attributes) or the topic (scope label); chunk results inherit their parent's,
/// so filtering parents covers them.
/// </summary>
public IReadOnlyList<SearchResult> SearchAll(
    string query, string? scopes, int limit, string? excludeTopics,
    IReadOnlyDictionary<string, double>? supplementalScores,
    IReadOnlyDictionary<string, double>? entityGraphScores,
    string? pathPrefix,
    IReadOnlyDictionary<string, string[]>? attributeFilter)
{
    bool hasPath = !string.IsNullOrWhiteSpace(pathPrefix);
    bool hasAttr = attributeFilter is { Count: > 0 };
    if (!hasPath && !hasAttr)
        return SearchAll(query, scopes, limit, excludeTopics, supplementalScores, entityGraphScores);

    if (string.IsNullOrWhiteSpace(query))
        return [];

    var searcher = new WeightedAttributeScorer(RankerOptions);
    var allCandidates = ListScoped(scopes, excludeTopics);
    var entryCandidates = allCandidates
        .Where(c => SearchResultFilter.CandidateMatches(this, c, pathPrefix, attributeFilter))
        .ToList();
    // Topics carry no attributes: an active attribute filter drops them all. Otherwise filter
    // topics by their own scope label, derived from the UNFILTERED candidate set (a topic can
    // match even when its entries are filtered out by a longer path prefix, and vice versa).
    var topics = hasAttr
        ? new List<TopicInfo>()
        : BuildTopicInfosFromCandidates(allCandidates)
            .Where(t => SearchResultFilter.TopicMatches(t, pathPrefix))
            .ToList();
    return searcher.SearchAll(query, entryCandidates, topics, limit, supplementalScores, entityGraphScores);
}
```

Ensure `using Scrinia.Core.Search;` is present in `FileMemoryStore.cs` (it already references `WeightedAttributeScorer` / `TopicInfo`, so it is).

- [ ] **Step 6: Point ExecuteSearchAsync at the new overload; delete the MCP post-filter**

In `src/Scrinia.Mcp/MemoryTools.Core.cs` `ExecuteSearchAsync`, replace the two-branch `SearchAll` call + `ApplyPostFilters` (~L908-918) with a single call:

```csharp
// pathPrefix / attributeFilter are pushed into SearchAll so they apply BEFORE the top-K cut
// (symmetric with excludeTopics). A filtered search fills to `limit` instead of returning the
// post-limit remnant. Empty supplemental is normalized to null so the scorer skips it.
var supp = supplemental is { Count: > 0 } ? supplemental : null;
IReadOnlyList<SearchResult> filtered = store.SearchAll(
    effectiveQuery, scopes, limit, excludeTopics, supp, entityScores, pathPrefix, attributeFilter);
```

Then delete the now-unused private methods `ApplyPostFilters`, `MatchesPathPrefix`, and `MatchesAttributes` from `MemoryTools.Core.cs`. Grep first to confirm nothing else references them:

```bash
rtk grep -n "ApplyPostFilters\|MatchesPathPrefix\|MatchesAttributes" src/Scrinia.Mcp
```

Expected after deletion: no matches. Keep the `filtered` variable name so the rest of `ExecuteSearchAsync` (the `SearchOutcome` construction and `entityFacts`) is unchanged.

- [ ] **Step 7: Run the A2 tests and the search regression suites**

Run:
```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchPathAttributeFilterTests|FullyQualifiedName~SearchExcludeTopics|FullyQualifiedName~V2PathSearch"
```
Expected: PASS. The `SearchExcludeTopics` and `V2PathSearch` suites must stay green (they exercise the neighboring candidate-filtering path).

- [ ] **Step 8: Build the MCP project to confirm the deletions compile**

Run: `rtk dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj`
Expected: 0 warnings, 0 errors (no dangling references to the deleted helpers; `TreatWarningsAsErrors` would fail the build on an unused-field/using left behind — clean those up if flagged).

- [ ] **Step 9: Commit**

```bash
rtk git add src/Scrinia.Core/Search/SearchResultFilter.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/SearchPathAttributeFilterTests.cs
rtk git commit -m "fix(search): apply pathPrefix/attributeFilter before the top-K cut"
```

---

### Task 3 (B1): sign the live-serving vector stores

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/ChunkedSignature.cs` (add `ForProvider`)
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` (`EnsureInitialized`: provider before store, signed store)
- Modify: `src/Scrinia.Plugin.Embeddings.Cli/Program.cs` (provider + options before store, signed store)
- Test: `tests/Scrinia.Tests/Embeddings/VectorStoreTests.cs` (non-destructive-null-signature), `tests/Scrinia.Tests/Embeddings/ChunkedSignatureForProviderTests.cs` (new)

**Interfaces:**
- Consumes: `IEmbeddingProvider { bool IsAvailable; int Dimensions; string Signature; Task<float[]?> EmbedAsync(...); Task<float[][]?> EmbedBatchAsync(...) }`, `EmbeddingOptions { int ChunkSize; int ChunkOverlap; ... }`, `VectorStore(string baseDir, string? expectedSignature = null)`, `ChunkedSignature.Compose(providerSignature, chunkSize, overlap)`.
- Produces: `ChunkedSignature.ForProvider(IEmbeddingProvider provider, EmbeddingOptions options) -> string?` (null when `!provider.IsAvailable`).

**Background:** `EmbeddingsPlugin.EnsureInitialized` and the plugin CLI construct `new VectorStore(dir)` with no signature, disabling SVF3 quarantine-on-mismatch on the live-serving path. The reindexer signs correctly. Both live sites must sign with the active provider's signature — composed identically to the reindexer — and pass `null` when the provider is unavailable so a transient load failure never quarantines good vectors. Both sites currently build the `VectorStore` *before* the provider, so they must be reordered.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Embeddings/ChunkedSignatureForProviderTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public sealed class ChunkedSignatureForProviderTests
{
    private sealed class FakeProvider(bool available, string signature) : IEmbeddingProvider
    {
        public bool IsAvailable => available;
        public int Dimensions => 3;
        public string Signature => signature;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { 1, 0, 0 });
        public void Dispose() { }
    }

    [Fact]
    public void ForProvider_Available_MatchesReindexerComposition()
    {
        var provider = new FakeProvider(available: true, signature: "vulkan:m2v-MiniLM-L6-v2");
        var options = new EmbeddingOptions(); // defaults, same as the reindexer's `options ??= new()`

        string? sig = ChunkedSignature.ForProvider(provider, options);

        sig.Should().Be(ChunkedSignature.Compose(
            "vulkan:m2v-MiniLM-L6-v2", options.ChunkSize, options.ChunkOverlap));
    }

    [Fact]
    public void ForProvider_Unavailable_ReturnsNull_SoQuarantineIsDisabled()
    {
        var provider = new FakeProvider(available: false, signature: "none");

        ChunkedSignature.ForProvider(provider, new EmbeddingOptions()).Should().BeNull();
    }
}
```

Add to `tests/Scrinia.Tests/Embeddings/VectorStoreTests.cs` a non-destructive test (reuse that file's existing helpers for writing a signed SVF3 file — read it first to find the write/save helper; if none exists, write vectors via a signed `VectorStore` then reopen):

```csharp
[Fact]
public async Task NullSignatureStore_LoadsExistingSignedFile_WithoutQuarantine()
{
    string dir = Path.Combine(Path.GetTempPath(), $"scrinia_vs_null_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        // Write a signed file under signature "x".
        var signed = new VectorStore(dir, expectedSignature: "x");
        await signed.UpsertAsync("local:memory", "doc", null, new float[] { 1, 0, 0 });

        // A store constructed with a null signature must NOT quarantine it (non-destructive):
        var unsigned = new VectorStore(dir, expectedSignature: null);
        var loaded = unsigned.GetVectors("local:memory");

        loaded.Should().ContainSingle(v => v.Name == "doc");
        unsigned.HasStaleQuarantines.Should().BeFalse();
        Directory.GetFiles(dir, "*.stale-*", SearchOption.AllDirectories).Should().BeEmpty();
    }
    finally { try { Directory.Delete(dir, recursive: true); } catch { } }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ChunkedSignatureForProvider|FullyQualifiedName~NullSignatureStore_LoadsExistingSignedFile"`
Expected: `ChunkedSignatureForProvider*` FAIL to compile (`ForProvider` missing). The `NullSignatureStore...` test should PASS already (it documents the existing non-destructive behavior of a null signature) — keep it as a guard that B1's null-on-unavailable path stays safe.

- [ ] **Step 3: Add ChunkedSignature.ForProvider**

In `src/Scrinia.Core/Embeddings/ChunkedSignature.cs`:

```csharp
/// <summary>
/// The signature a live-serving store should expect for vectors produced by
/// <paramref name="provider"/>, or <c>null</c> when the provider is unavailable. A store built
/// with a null signature disables quarantine-on-mismatch, so a transient provider-load failure
/// never quarantines good vectors. Uses the same <see cref="Compose"/> as the reindexer, so a
/// reindex with the same provider + options reproduces a byte-identical signature.
/// </summary>
public static string? ForProvider(IEmbeddingProvider provider, EmbeddingOptions options)
    => provider.IsAvailable
        ? Compose(provider.Signature, options.ChunkSize, options.ChunkOverlap)
        : null;
```

`IEmbeddingProvider` and `EmbeddingOptions` are in the same `Scrinia.Core.Embeddings` namespace — no new `using` needed.

- [ ] **Step 4: Sign the plugin's live store (reorder provider before store)**

In `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` `EnsureInitialized`, move the provider-init `try/catch` and the `LogInformation` line to run BEFORE the `VectorStore` construction, then sign the store:

```csharp
string modelsDir = Path.Combine(pluginsDir, "scri-plugin-embeddings");
Directory.CreateDirectory(modelsDir);

// Initialize the provider FIRST so the store can be signed with its signature.
try
{
    if (VulkanModelManager.IsModelAvailable(modelsDir))
    {
        string modelPath = VulkanModelManager.GetModelPath(modelsDir);
        _provider = VulkanEmbeddingProvider.Create(modelPath, 384, _logger);
    }
    else
    {
        _logger.LogWarning("Vulkan GGUF model not found at {Dir}. Plugin inactive.", modelsDir);
    }
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to create Vulkan embedding provider");
}

_logger.LogInformation("Embeddings plugin initialized: provider={Provider}, available={Available}",
    _provider.GetType().Name, _provider.IsAvailable);

// Sign the store with the active provider's signature (null when unavailable — a transient
// provider-load failure must not quarantine good vectors). On a real provider change, mismatched
// files self-quarantine on first load and rebuild via reindex.
string? expectedSignature = ChunkedSignature.ForProvider(_provider, new EmbeddingOptions());
var vectorStore = new VectorStore(dataDir, expectedSignature);

_vectorStore = vectorStore; // assign LAST: it's the initialized sentinel
```

Add `using Scrinia.Core.Embeddings;` if not already imported (the file already uses `VectorStore` / `NullEmbeddingProvider`, so it is). Do not add a `HasStaleQuarantines` check here — quarantine happens lazily on first per-scope load, so it is always empty at construction; `VectorStore.QuarantineStaleFile` already logs each quarantine when it occurs.

- [ ] **Step 5: Sign the plugin CLI's store (reorder provider + options before store)**

In `src/Scrinia.Plugin.Embeddings.Cli/Program.cs`, move the provider selection and `options` construction ABOVE the `VectorStore` line, then sign:

```csharp
IEmbeddingProvider provider;

if (VulkanModelManager.IsModelAvailable(modelsDir))
{
    string modelPath = VulkanModelManager.GetModelPath(modelsDir);
    provider = VulkanEmbeddingProvider.Create(modelPath, 384, logger);
}
else
{
    Console.Error.WriteLine("[scrinia:warn] GGUF model not found. Run setup to download it.");
    provider = new NullEmbeddingProvider();
}

var options = new EmbeddingOptions { SemanticWeight = semanticWeight };

// Sign the store with the active provider's signature (null when unavailable — non-destructive).
var vectorStore = new VectorStore(embeddingsDir, ChunkedSignature.ForProvider(provider, options));
```

Delete the original `var vectorStore = new VectorStore(embeddingsDir);` line and the now-duplicated later `options` construction. Confirm the later DI registration order (`AddSingleton(vectorStore)`, `AddSingleton(provider)`, `AddSingleton(options)`) still compiles — all three are now defined above it.

- [ ] **Step 6: Run the tests and build both embeddings projects**

Run:
```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ChunkedSignatureForProvider|FullyQualifiedName~VectorStoreTests"
rtk dotnet build src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj
rtk dotnet build src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj
```
Expected: tests PASS; both builds 0 warnings / 0 errors.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/Embeddings/ChunkedSignature.cs src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs src/Scrinia.Plugin.Embeddings.Cli/Program.cs tests/Scrinia.Tests/Embeddings/ChunkedSignatureForProviderTests.cs tests/Scrinia.Tests/Embeddings/VectorStoreTests.cs
rtk git commit -m "fix(embeddings): sign live-serving vector stores with the active provider signature"
```

---

### Task 4 (B2): incremental embed clears stale vectors on shrink and re-embeds full memory on append

**Files:**
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` (`EmbedAndIndexAsync`, `OnAppendedAsync`)
- Test: `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginIncrementalEmbedTests.cs` (new)

**Interfaces:**
- Consumes: `IMemoryEventSink.OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)`, `OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)`; `VectorStore.RemoveAsync(scope, name, ct)`, `VectorStore.GetVectors(scope)`, `VectorStore.UpsertAsync(scope, name, chunkIndex, vector, ct)`; `IMemoryStore.ParseQualifiedName(...)`, `IMemoryStore.ReadArtifactAsync(subject, scope, ct)`.
- Produces: no signature change; `EmbedAndIndexAsync` now Removes before Upserting; `OnAppendedAsync` re-embeds the full current memory.

**Background:** `EmbedAndIndexAsync` upserts vectors without first removing prior ones for the name, so re-storing a memory with fewer chunks leaves orphaned higher-index chunk vectors. `OnAppendedAsync` re-embeds only the appended fragment as the full-document (null-chunk) vector, corrupting it. Both share the incremental-path root cause. The reindexer already Removes-then-Upserts per memory; mirror it, and on append read the full current content and re-embed that.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginIncrementalEmbedTests.cs`. Inject a real `VectorStore` (temp dir) and a deterministic fake provider into the plugin via reflection — the same seam pattern `EmbeddingsPluginReindexTests` uses (set the private `_vectorStore` field so `EnsureInitialized` short-circuits at its `if (_vectorStore is not null) return;` guard, and set `_provider`):

```csharp
using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class EmbeddingsPluginIncrementalEmbedTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly string _vecDir;
    private readonly VectorStore _vectors;

    // Deterministic provider: vector encodes the input length so we can tell WHICH text was embedded.
    private sealed class LengthProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 1;
        public string Signature => "fake:len";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { text.Length });
        public void Dispose() { }
    }

    public EmbeddingsPluginIncrementalEmbedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_inc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
        _vecDir = Path.Combine(_root, "vec");
        Directory.CreateDirectory(_vecDir);
        _vectors = new VectorStore(_vecDir, expectedSignature: null);
    }

    public void Dispose()
    {
        _store.Dispose();
        _vectors.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private EmbeddingsPlugin BuildPlugin()
    {
        var plugin = new EmbeddingsPlugin();
        typeof(EmbeddingsPlugin).GetField("_vectorStore", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, _vectors);
        typeof(EmbeddingsPlugin).GetField("_provider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, new LengthProvider());
        typeof(EmbeddingsPlugin).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return plugin;
    }

    private void SeedArtifact(string scope, string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task ReStore_WithFewerChunks_LeavesNoStaleChunkVectors()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string qn = "local:memory/notes";

        // First store: 3 chunks -> null-chunk doc vector + chunks 1,2,3 = 4 vectors.
        await plugin.OnStoredAsync(qn, new[] { "aaa", "bbb", "ccc" }, _store, CancellationToken.None);
        _vectors.GetVectors(scope).Count(v => v.Name == "notes").Should().Be(4);

        // Re-store: 1 chunk -> only the null-chunk doc vector. Without Remove-first, the 3 old
        // chunk vectors would remain (count 4). With the fix: exactly 1.
        await plugin.OnStoredAsync(qn, new[] { "single" }, _store, CancellationToken.None);
        _vectors.GetVectors(scope).Count(v => v.Name == "notes").Should().Be(1);
    }

    [Fact]
    public async Task Append_ReEmbedsFullMemory_NotJustTheFragment()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string subject = "log";
        const string qn = "local:memory/log";

        SeedArtifact(scope, subject, "0123456789"); // length 10
        await plugin.OnStoredAsync(qn, new[] { "0123456789" }, _store, CancellationToken.None);

        // Append 5 chars on disk, then fire the append event with only the fragment.
        string full = await _store.ReadArtifactAsync(subject, scope, CancellationToken.None);
        string appendedFull = System.Text.Encoding.UTF8.GetString(new Nmp2Strategy().Decode(full)) + "ABCDE";
        SeedArtifact(scope, subject, appendedFull); // full content now length 15
        await plugin.OnAppendedAsync(qn, "ABCDE", _store, CancellationToken.None);

        // The doc (null-chunk) vector must encode the FULL current content length (15), not the
        // 5-char fragment. LengthProvider makes this observable.
        var doc = _vectors.GetVectors(scope).Single(v => v.Name == subject && v.ChunkIndex is null);
        doc.Vector[0].Should().Be(appendedFull.Length);
    }
}
```

Read `EmbeddingsPluginReindexTests.cs` for the exact `SeedEntry`/`ArtifactPath`/`Nmp2ChunkedEncoder` usage and mirror it; adjust `VectorEntry`'s vector-property name (`Vector`) and `ChunkIndex` against the source. If `ReadArtifactAsync` returns already-decoded text (not the encoded artifact), simplify the append test's `appendedFull` accordingly — verify by reading `Nmp2Strategy`/`ReadArtifactAsync` semantics first.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter "FullyQualifiedName~EmbeddingsPluginIncrementalEmbedTests"`
Expected: both FAIL — `ReStore...` returns 4 (stale chunks remain); `Append...` returns 5 (fragment length), not 15.

- [ ] **Step 3: Fix the shrink path (Remove before Upsert)**

In `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs` `EmbedAndIndexAsync`, add a remove before the upsert loop:

```csharp
var vectors = await _provider.EmbedBatchAsync(
    items.Select(x => x.text).ToList(), ct);
if (vectors is null) return;

// Atomic per-memory replace: clear prior vectors (including stale higher-index chunks from a
// previous, larger chunk count) before upserting the new set — mirrors EmbeddingReindexer.
await _vectorStore.RemoveAsync(scope, subject, ct);

for (int i = 0; i < vectors.Length; i++)
    await _vectorStore.UpsertAsync(scope, subject, items[i].chunkIndex, vectors[i], ct);
```

(`RemoveAsync` is a no-op when the name has no prior vectors, so first-time stores are unaffected. Remove and the upserts all run under `VectorStore`'s per-scope lock, so the remove completes before the upserts.)

- [ ] **Step 4: Fix the append path (re-embed the full memory)**

In the same file, change `OnAppendedAsync` to read the full current content and re-embed it instead of embedding only the fragment:

```csharp
public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
{
    EnsureInitialized();
    if (_vectorStore is null) return;

    // An append mutates the memory: re-embed the FULL current content, not just the appended
    // fragment (which would overwrite the document vector with a fragment). Read the current
    // artifact and route it through the shared embed path (which now Removes stale vectors first).
    try
    {
        var (scope, subject) = store.ParseQualifiedName(qualifiedName);
        string fullText = await store.ReadArtifactAsync(subject, scope, ct);
        if (string.IsNullOrWhiteSpace(fullText)) return;
        await EmbedAndIndexAsync(qualifiedName, [fullText], store, ct);
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Failed to re-embed appended memory '{Name}'", qualifiedName);
    }
}
```

Confirm `ReadArtifactAsync` returns the decoded plain text (the reindexer decodes the artifact via `Nmp2Strategy` before embedding — if `ReadArtifactAsync` returns the *encoded* artifact, decode it here the same way the reindexer does: `System.Text.Encoding.UTF8.GetString(new Nmp2Strategy().Decode(fullText))`). Match whichever the source dictates so the embedded text is plain content, and align the Step 1 test's expectation with it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter "FullyQualifiedName~EmbeddingsPluginIncrementalEmbedTests"`
Expected: PASS.

- [ ] **Step 6: Run the full embeddings plugin suite (guard against regressions)**

Run: `rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`
Expected: PASS (existing `EmbeddingsPluginReindexTests`, `EmbeddingsPluginInitTests`, CLI tests stay green).

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginIncrementalEmbedTests.cs
rtk git commit -m "fix(embeddings): incremental embed clears stale vectors on shrink; append re-embeds full memory"
```

---

## Final validation (after all four tasks)

- [ ] **Full solution build (audit must stay clean, no flag):**

Run: `rtk dotnet build Scrinia.sln`
Expected: 0 warnings / 0 errors; restore does not trip `NU19xx` (Stream 3 cleared the audit).

- [ ] **Run the affected test projects:**

Run:
```bash
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
```
Expected: all green. (`UpdateCorrectnessBenchmarkTests.UpdatedFact_OldContentNotReturned` is a known parallel-load flake unrelated to this stream — if it appears, re-run it in isolation to confirm.)

---

## Self-Review notes (author)

- **Spec coverage:** A1 → Task 1; A2 → Task 2; B1 → Task 3; B2 (shrink + append) → Task 4. Both load-bearing invariants have dedicated tests (A2 fill test in Task 2 Step 1; B1 equivalence + non-destructive in Task 3 Step 1). The B1 migration note is captured in Global Constraints.
- **Type consistency:** `ChunkedSignature.ForProvider(IEmbeddingProvider, EmbeddingOptions) -> string?` is defined in Task 3 Step 3 and consumed in Steps 4-5; `SearchResultFilter.{CandidateMatches,TopicMatches,Matches}` defined in Task 2 Step 3 and consumed in Steps 4-5; the 8-param `SearchAll` overload defined in Task 2 Step 4 and consumed in Step 6.
- **Verification hooks:** three steps ("Confirm…"/"Read…first") flag places where a constructor/initializer or a decode boundary must be checked against live source before the code compiles/asserts correctly — these are deliberate guards, not placeholders; each names the exact thing to confirm and how to adjust.
