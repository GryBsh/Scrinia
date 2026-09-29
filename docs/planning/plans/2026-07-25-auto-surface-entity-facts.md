# Auto-surfacing entity facts (Piece 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When an MCP `search` query names an entity, prepend a compact aggregate fact summary (count + date span + top co-occurrences) to the response — informational only, ranking untouched.

**Architecture:** Lift `EntityGraph`'s private query→entity seeding into a shared pure primitive (`EntityQuerySeeder`) so the graph and the new surfacing path share one rule. Add a pure projection (`EntityFactSummarizer`) over the existing `EntityFactStore`. Compute the summary inside the shared `ExecuteSearchAsync` from the candidates already loaded for scoring, gated behind an opt-in flag (default off) so only the agent-facing MCP surface pays for it. Render an `entity_facts:` preamble in the MCP `search` formatter.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- Target framework: `net10.0`.
- **Build/test the test project directly** (the full `Scrinia.sln` restore fails on a pre-existing `Scrinia.AppHost` NuGet-audit error): `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`.
- The C# LSP may emit **phantom** CS errors against cached/deleted symbols mid-edit. Trust `dotnet build`/`dotnet test` output, not the diagnostic stream.
- New public/pure helpers live in namespace `Scrinia.Core.Search` (same as `EntityGraph`, `EntityFactStore`), so `TextAnalysis`, `EntityRef`, etc. resolve without new usings.
- **No ranking/selection change.** The `results:` section — order, scores, membership — must be byte-identical with and without the summary. This is a hard requirement of every task that touches the search path.
- Determinism: stable ordering everywhere (mention count desc, then `Key`/`Value` ordinal). Same inputs → same output.
- Commit messages follow the repo convention (`feat(...)`, `refactor(...)`, `test(...)`) and end with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- DRY / YAGNI / TDD. One shared seeding rule; no set-intersection or timeline (those are Piece 4).

---

## File map

- Create `src/Scrinia.Core/Search/EntityQuerySeeder.cs` — shared query→entity seeding rule.
- Modify `src/Scrinia.Core/Search/EntityGraph.cs` — delegate `SeedEntities` to the new primitive; drop its now-unused `SurfaceOf`.
- Create `src/Scrinia.Core/Search/EntityFactSummary.cs` — `CoOccurrence`, `EntityFactSummary`, `EntityFactSummarizer.Summarize`.
- Modify `src/Scrinia.Mcp/MemoryTools.Core.cs` — `SearchOutcome` record; `ExecuteSearchAsync` returns it + `surfaceEntityFacts` flag + `BuildEntityFacts`; `Search` opts in; `FormatRichSearchResults` renders the block.
- Modify `src/Scrinia/Commands/ScriniaCommands.Memory.cs:230` — read `.Matches` from the new return type.
- Create `tests/Scrinia.Tests/EntityQuerySeederTests.cs`
- Create `tests/Scrinia.Tests/EntityFactSummarizerTests.cs`
- Create `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs`

---

## Task 1: `EntityQuerySeeder` shared seeding primitive

Extract `EntityGraph.SeedEntities`'s rule into a pure, tested static helper and point the graph at it. Behavior is unchanged — the existing `EntityGraphTests` are the parity guard.

**Files:**
- Create: `src/Scrinia.Core/Search/EntityQuerySeeder.cs`
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs` (`SeedEntities` ~line 273, `SurfaceOf` ~line 298)
- Test: `tests/Scrinia.Tests/EntityQuerySeederTests.cs`

**Interfaces:**
- Produces: `public static List<string> EntityQuerySeeder.Seed(string query, IEnumerable<string> entityKeys)` — returns the subset of `entityKeys` (each `"type:value"`) the query names: lowercased query *contains the surface value as a substring*, OR every token of the surface is a query token. Surface = substring after the first `:` (a key with no usable `:` seeds on the whole string).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EntityQuerySeederTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityQuerySeederTests
{
    private static readonly string[] Vocab =
        ["person:jolene", "place:france", "thing:new york", "topic:auth"];

    [Fact]
    public void SubstringOfSurface_Seeds()
    {
        EntityQuerySeeder.Seed("what do I know about jolene?", Vocab)
            .Should().Contain("person:jolene");
    }

    [Fact]
    public void AllSurfaceTokensPresent_OutOfOrder_Seeds()
    {
        // "new york" surface tokens both present though not contiguous.
        EntityQuerySeeder.Seed("york trip, new plans", Vocab)
            .Should().Contain("thing:new york");
    }

    [Fact]
    public void UnnamedEntity_NotSeeded()
    {
        EntityQuerySeeder.Seed("jolene in france", Vocab)
            .Should().NotContain("topic:auth");
    }

    [Fact]
    public void MultipleNamed_AllSeeded()
    {
        EntityQuerySeeder.Seed("jolene in france", Vocab)
            .Should().Contain("person:jolene").And.Contain("place:france");
    }

    [Fact]
    public void ColonlessKey_SeedsOnWholeString()
    {
        EntityQuerySeeder.Seed("about widgets", ["widgets"])
            .Should().Contain("widgets");
    }

    [Fact]
    public void EmptyQuery_SeedsNothing()
    {
        EntityQuerySeeder.Seed("   ", Vocab).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityQuerySeederTests"`
Expected: FAIL — `EntityQuerySeeder` does not exist (compile error).

- [ ] **Step 3: Create `EntityQuerySeeder`**

Create `src/Scrinia.Core/Search/EntityQuerySeeder.cs`:

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The query→entity seeding rule shared by <see cref="EntityGraph"/>'s PPR seeding and the
/// retrieval auto-surface path: given a query and a set of <c>"type:value"</c> entity keys, return
/// which keys the query names. A key is named when the lowercased query contains the key's surface
/// value as a substring, OR every token of the surface appears among the query tokens. Extracted
/// into one place so the two consumers cannot drift. Pure, ordinal, culture-invariant.
/// </summary>
public static class EntityQuerySeeder
{
    public static List<string> Seed(string query, IEnumerable<string> entityKeys)
    {
        var seeds = new List<string>();
        if (string.IsNullOrWhiteSpace(query) || entityKeys is null) return seeds;

        string queryLower = query.ToLowerInvariant();
        var queryTokens = new HashSet<string>(TextAnalysis.Tokenize(query), StringComparer.OrdinalIgnoreCase);

        foreach (string key in entityKeys)
        {
            string surface = SurfaceOf(key);
            if (queryLower.Contains(surface, StringComparison.Ordinal))
            {
                seeds.Add(key);
                continue;
            }
            var entityTokens = TextAnalysis.Tokenize(surface);
            if (entityTokens.Count > 0 && entityTokens.All(queryTokens.Contains))
                seeds.Add(key);
        }
        return seeds;
    }

    /// <summary>The surface a key seeds on: the value after the first <c>:</c>. A key with no
    /// usable colon seeds on the whole string (defensive parity with the graph's old behavior).</summary>
    internal static string SurfaceOf(string nodeKey)
    {
        int colon = nodeKey.IndexOf(':');
        return colon > 0 && colon < nodeKey.Length - 1 ? nodeKey[(colon + 1)..] : nodeKey;
    }
}
```

- [ ] **Step 4: Point `EntityGraph` at the primitive**

In `src/Scrinia.Core/Search/EntityGraph.cs`, replace the whole private `SeedEntities` method body (~lines 273-292) with a one-line delegation:

```csharp
    private List<string> SeedEntities(string query) =>
        EntityQuerySeeder.Seed(query, _entityToDocs.Keys);
```

Then delete the now-unused private `static string SurfaceOf(string nodeKey)` method (~lines 294-302) and its doc-comment from `EntityGraph.cs`. (It was only referenced by `SeedEntities`.)

- [ ] **Step 5: Run the new tests + the EntityGraph parity tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityQuerySeederTests|FullyQualifiedName~EntityGraph"`
Expected: PASS — the new seeder tests pass AND every existing `EntityGraphTests` / `EntityGraphScoreContributorTests` / `EntityGraphSourceSearchTests` stays green (behavior parity after the refactor).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Search/EntityQuerySeeder.cs src/Scrinia.Core/Search/EntityGraph.cs tests/Scrinia.Tests/EntityQuerySeederTests.cs
git commit -m "$(cat <<'EOF'
refactor(search): extract shared EntityQuerySeeder from EntityGraph

Lifts the private query->entity seeding rule into a pure, tested static
helper so the PPR path and the coming auto-surface path share one
definition. EntityGraph.SeedEntities now delegates; behavior unchanged
(existing EntityGraph tests are the parity guard).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `EntityFactSummary` + `EntityFactSummarizer`

A pure projection over the existing `EntityFactStore`: given the entity refs the query named, produce up to N ranked compact summaries. No new store internals.

**Files:**
- Create: `src/Scrinia.Core/Search/EntityFactSummary.cs`
- Test: `tests/Scrinia.Tests/EntityFactSummarizerTests.cs`

**Interfaces:**
- Consumes: `EntityFactStore.MentionCount(EntityRef)`, `.Span(EntityRef) → (DateTimeOffset First, DateTimeOffset Last)?`, `.Neighbors(EntityRef) → IReadOnlyList<(EntityRef Entity, int Count)>` (already ordered count desc); `EntityRef(string Key, string Value)`; `MemoryEntities(string MemoryId, DateTimeOffset Date, IReadOnlyList<EntityRef> Entities)`.
- Produces:
  - `public readonly record struct CoOccurrence(EntityRef Entity, int Count)`
  - `public sealed record EntityFactSummary(EntityRef Entity, int MentionCount, DateTimeOffset? First, DateTimeOffset? Last, IReadOnlyList<CoOccurrence> TopCoOccurring)`
  - `public static IReadOnlyList<EntityFactSummary> EntityFactSummarizer.Summarize(EntityFactStore store, IEnumerable<EntityRef> namedEntities, int maxEntities = 3, int coOccurrenceLimit = 3)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EntityFactSummarizerTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactSummarizerTests
{
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D3 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);

    // jolene appears in 3 memories; france in 2 (both co-occur with jolene); seraphim in 1.
    private static EntityFactStore Store() => new(
    [
        new MemoryEntities("m1", D1, [R("person", "jolene"), R("place", "france")]),
        new MemoryEntities("m2", D2, [R("person", "jolene"), R("place", "france"), R("thing", "seraphim")]),
        new MemoryEntities("m3", D3, [R("person", "jolene")]),
    ]);

    [Fact]
    public void CountSpanAndCoOccurrences_AreCorrect()
    {
        var s = EntityFactSummarizer.Summarize(Store(), [R("person", "jolene")]).Single();

        s.MentionCount.Should().Be(3);
        s.First.Should().Be(D1);
        s.Last.Should().Be(D3);
        s.TopCoOccurring.Should().ContainSingle(c => c.Entity == R("place", "france") && c.Count == 2);
        s.TopCoOccurring.Should().Contain(c => c.Entity == R("thing", "seraphim") && c.Count == 1);
    }

    [Fact]
    public void RanksByMentionCountDesc_AndCapsEntities()
    {
        var all = EntityFactSummarizer.Summarize(
            Store(),
            [R("thing", "seraphim"), R("person", "jolene"), R("place", "france")],
            maxEntities: 2);

        all.Should().HaveCount(2);
        all[0].Entity.Should().Be(R("person", "jolene")); // 3 mentions
        all[1].Entity.Should().Be(R("place", "france"));  // 2 mentions (seraphim's 1 is dropped by the cap)
    }

    [Fact]
    public void CapsCoOccurrences()
    {
        var s = EntityFactSummarizer.Summarize(Store(), [R("person", "jolene")], coOccurrenceLimit: 1).Single();
        s.TopCoOccurring.Should().HaveCount(1);
        s.TopCoOccurring[0].Entity.Should().Be(R("place", "france")); // highest co-occurrence
    }

    [Fact]
    public void UnknownEntity_IsSkipped()
    {
        EntityFactSummarizer.Summarize(Store(), [R("person", "nobody")]).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactSummarizerTests"`
Expected: FAIL — `EntityFactSummary` / `EntityFactSummarizer` do not exist.

- [ ] **Step 3: Create the records + summarizer**

Create `src/Scrinia.Core/Search/EntityFactSummary.cs`:

```csharp
namespace Scrinia.Core.Search;

/// <summary>One co-occurring entity and how many of the summarized entity's memories it shares.</summary>
public readonly record struct CoOccurrence(EntityRef Entity, int Count);

/// <summary>A compact aggregate view of one entity: how many memories mention it, the span of their
/// dates, and its strongest co-occurring entities. Built by <see cref="EntityFactSummarizer"/> for
/// retrieval auto-surfacing. <see cref="First"/>/<see cref="Last"/> are null only when the entity
/// has no facts (defensive — callers pass entities known to the store).</summary>
public sealed record EntityFactSummary(
    EntityRef Entity,
    int MentionCount,
    DateTimeOffset? First,
    DateTimeOffset? Last,
    IReadOnlyList<CoOccurrence> TopCoOccurring);

/// <summary>Pure projection of an <see cref="EntityFactStore"/> into compact per-entity summaries
/// for the entities a query named. Deterministic: entities ranked by mention count desc, then
/// Key/Value ordinal (matching the store's own tie-breaks).</summary>
public static class EntityFactSummarizer
{
    public static IReadOnlyList<EntityFactSummary> Summarize(
        EntityFactStore store,
        IEnumerable<EntityRef> namedEntities,
        int maxEntities = 3,
        int coOccurrenceLimit = 3)
    {
        var summaries = new List<EntityFactSummary>();
        foreach (var entity in namedEntities.Distinct())
        {
            int count = store.MentionCount(entity);
            if (count == 0) continue; // named a key the candidate set doesn't actually carry
            var span = store.Span(entity);
            var coOcc = store.Neighbors(entity)   // already ordered by count desc
                .Take(coOccurrenceLimit)
                .Select(n => new CoOccurrence(n.Entity, n.Count))
                .ToArray();
            summaries.Add(new EntityFactSummary(entity, count, span?.First, span?.Last, coOcc));
        }

        return summaries
            .OrderByDescending(s => s.MentionCount)
            .ThenBy(s => s.Entity.Key, StringComparer.Ordinal)
            .ThenBy(s => s.Entity.Value, StringComparer.Ordinal)
            .Take(maxEntities)
            .ToArray();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactSummarizerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactSummary.cs tests/Scrinia.Tests/EntityFactSummarizerTests.cs
git commit -m "$(cat <<'EOF'
feat(search): EntityFactSummarizer projects the store into compact summaries

Pure projection over EntityFactStore: per named entity, mention count +
date span + top-K co-occurrences, ranked by count and capped. Reuses
MentionCount/Span/Neighbors; no new store internals.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Wire the summary into `ExecuteSearchAsync` (opt-in), update callers

`ExecuteSearchAsync` returns a `SearchOutcome` carrying both matches and the (possibly empty) entity summaries. The summary is built from the candidates already loaded for scoring, only when the caller opts in — so the CLI path stays free. No rendering yet (Task 4).

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`ExecuteSearchAsync` ~lines 848-892; `Search` ~lines 829-837; add `SearchOutcome` + `BuildEntityFacts`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:230-232`
- Test: `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs`

**Interfaces:**
- Consumes: `EntityQuerySeeder.Seed` (Task 1); `EntityFactSummarizer.Summarize` (Task 2); `EntityFactDerivation.Derive(ScopedArtifact, string) → MemoryEntities?`; `IMemoryStore.FormatRoundTripQualifiedName(scope, subject)`; `IMemoryStore.ListScoped(string? scopes, string? excludeTopics)`; `EntityFactStore(IEnumerable<MemoryEntities>)`.
- Produces:
  - `internal readonly record struct SearchOutcome(IReadOnlyList<SearchResult> Matches, IReadOnlyList<EntityFactSummary> EntityFacts)`
  - `ExecuteSearchAsync(...)` now returns `Task<SearchOutcome>` and takes a trailing `bool surfaceEntityFacts = false`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs`. Uses the codebase's `TestHelpers.StoreScope` (sets `MemoryStoreContext.Current` to a fresh `FileMemoryStore` and resets on dispose), so the direct `ExecuteSearchAsync(store, ...)` calls here and the `Search()` rendering calls in Task 4 share one ambient store:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class AutoSurfaceEntityFactsTests
{
    private static ArtifactEntry Note(string name, params (string Key, string[] Vals)[] attrs)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in attrs) d[k] = v;
        return new ArtifactEntry(name, "uri", 1, 1, DateTimeOffset.UtcNow,
            ContentPreview: "jolene france seraphim", Attributes: d);
    }

    [Fact]
    public async Task QueryNamesEntity_ProducesSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("m2", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "what about jolene", scopes: null, limit: 20, excludeTopics: null,
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        outcome.EntityFacts.Should().Contain(f => f.Entity.Key == "person" && f.Entity.Value == "jolene"
            && f.MentionCount == 2);
    }

    [Fact]
    public async Task QueryNamesNothing_NoSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "zzqqxx", null, 20, null, null, null, null, default, surfaceEntityFacts: true);

        outcome.EntityFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task FlagOff_NoSummary_EvenWhenEntityNamed()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "jolene", null, 20, null, null, null, null, default); // surfaceEntityFacts defaults false

        outcome.EntityFacts.Should().BeEmpty();
        outcome.Matches.Should().NotBeEmpty(); // matches still returned as before
    }

    [Fact]
    public async Task ExcludedTopic_DoesNotCountTowardSummary()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        // jolene appears once in findings and once in an excluded 'plan' topic scope.
        store.Upsert(Note("keep", ("person", ["jolene"])), "local:findings");
        store.Upsert(Note("hidden", ("person", ["jolene"])), "local:plan");

        var outcome = await ScriniaMcpTools.ExecuteSearchAsync(
            store, "jolene", scopes: null, limit: 20, excludeTopics: "plan",
            context: null, pathPrefix: null, attributeFilter: null, cancellationToken: default,
            surfaceEntityFacts: true);

        outcome.EntityFacts.Single(f => f.Entity.Value == "jolene").MentionCount
            .Should().Be(1, "the excluded 'plan' scope must not contribute to the count");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AutoSurfaceEntityFactsTests"`
Expected: FAIL — `ExecuteSearchAsync` does not return `SearchOutcome` / has no `surfaceEntityFacts` param (compile error).

- [ ] **Step 3: Add `SearchOutcome` and `BuildEntityFacts`, change `ExecuteSearchAsync`**

In `src/Scrinia.Mcp/MemoryTools.Core.cs`, add the record just above the `ExecuteSearchAsync` method (inside the `Scrinia.Mcp` namespace, alongside the class or as a top-level internal type in the file):

```csharp
/// <summary>The result of a scoring-pipeline run: the ranked matches plus the (possibly empty)
/// entity fact summaries for entities the query named. EntityFacts is always empty unless the
/// caller passes surfaceEntityFacts: true.</summary>
internal readonly record struct SearchOutcome(
    IReadOnlyList<SearchResult> Matches,
    IReadOnlyList<EntityFactSummary> EntityFacts);
```

Change the `ExecuteSearchAsync` signature (add the trailing flag, change the return type):

```csharp
    internal static async Task<SearchOutcome> ExecuteSearchAsync(
        IMemoryStore store,
        string query,
        string? scopes,
        int limit,
        string? excludeTopics,
        string? context,
        string? pathPrefix,
        IReadOnlyDictionary<string, string[]>? attributeFilter,
        CancellationToken cancellationToken,
        bool surfaceEntityFacts = false)
    {
```

Replace the candidate-loading + scoring block (currently ~lines 868-879, the `var contributor = ...` through the two `ComputeScoresAsync` calls) with a version that loads candidates once when *either* scoring or surfacing needs them:

```csharp
        var contributor = SearchContributorContext.Current;
        var entityContributor = EntityGraphScoreContext.Current;
        IReadOnlyList<ScopedArtifact>? candidates = null;
        if (contributor is not null || entityContributor is not null || surfaceEntityFacts)
            candidates = store.ListScoped(scopes, excludeTopics);

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (candidates is not null)
        {
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
            if (entityContributor is not null)
                entityScores = await entityContributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
        }
```

Then change the method's final line — currently `return ApplyPostFilters(store, matches, pathPrefix, attributeFilter);` — to capture the post-filtered matches into a local and build the outcome:

```csharp
        var filtered = ApplyPostFilters(store, matches, pathPrefix, attributeFilter);

        IReadOnlyList<EntityFactSummary> entityFacts = [];
        if (surfaceEntityFacts && candidates is { Count: > 0 })
            entityFacts = BuildEntityFacts(store, effectiveQuery, candidates);

        return new SearchOutcome(filtered, entityFacts);
```

Add the private helper near `ExecuteSearchAsync`:

```csharp
    /// <summary>Builds up to 3 entity fact summaries from the same scope+excludeTopics-filtered
    /// candidate set the scorer used — never a separate store read, never a hidden scope. Derives a
    /// scoped EntityFactStore, detects the entities the query names via the shared seeder, and
    /// projects them. Empty when the query names no known entity.</summary>
    private static IReadOnlyList<EntityFactSummary> BuildEntityFacts(
        IMemoryStore store, string effectiveQuery, IReadOnlyList<ScopedArtifact> candidates)
    {
        var memories = candidates
            .Select(sa => EntityFactDerivation.Derive(
                sa, store.FormatRoundTripQualifiedName(sa.Scope, sa.Entry.Name)))
            .Where(m => m is not null)
            .Select(m => m!);
        var factStore = new EntityFactStore(memories);

        // Map "type:value" -> EntityRef so we round-trip named keys without re-parsing colons.
        var refByKey = new Dictionary<string, EntityRef>(StringComparer.Ordinal);
        foreach (var e in factStore.Entities())
            refByKey[$"{e.Key}:{e.Value}"] = e;
        if (refByKey.Count == 0) return [];

        var named = EntityQuerySeeder.Seed(effectiveQuery, refByKey.Keys)
            .Select(k => refByKey[k]);

        return EntityFactSummarizer.Summarize(factStore, named);
    }
```

Ensure the file has `using Scrinia.Core.Search;` (for `EntityFactSummary`, `EntityQuerySeeder`, `EntityFactStore`, `EntityFactDerivation`, `ScopedArtifact`) — add it if missing.

- [ ] **Step 4: Update the two callers**

In `src/Scrinia.Mcp/MemoryTools.Core.cs`, the `Search` method (~line 830): opt in and read `.Matches` (no rendering change yet):

```csharp
        var store = CurrentStore;
        var outcome = await ExecuteSearchAsync(
            store, query, scopes, limit, excludeTopics, context, pathPrefix, attributeFilter,
            cancellationToken, surfaceEntityFacts: true);
        var matches = outcome.Matches;

        if (matches.Count == 0)
            return ResponseBuilder.Success("No matching memories found.").WithAction("searched").ToYaml();

        string body = FormatRichSearchResults(store, matches);
        return ResponseBuilder.Success(body).WithAction("searched").ToYaml();
```

In `src/Scrinia/Commands/ScriniaCommands.Memory.cs` (~line 230), take `.Matches` from the new return type (flag defaults false):

```csharp
        var matches = (await ScriniaMcpTools.ExecuteSearchAsync(
            store, query, scopes, limit, excludeTopics, context, pathPrefix,
            attributeFilter: null, cancellationToken)).Matches;
```

- [ ] **Step 5: Run the auto-surface tests + a broad search regression sweep**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AutoSurfaceEntityFactsTests|FullyQualifiedName~Search|FullyQualifiedName~EntityGraph"`
Expected: PASS — the 4 new tests pass AND all existing search/entity-graph tests stay green (return-type change is mechanical; ranking untouched).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs
git commit -m "$(cat <<'EOF'
feat(search): compute entity fact summaries in ExecuteSearchAsync (opt-in)

ExecuteSearchAsync now returns SearchOutcome(Matches, EntityFacts). When
surfaceEntityFacts is set (MCP search only), it derives a scoped fact
store from the candidates already loaded for scoring, detects
query-named entities via the shared seeder, and summarizes them. The CLI
path leaves the flag off and builds nothing. Ranking is untouched.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Render the `entity_facts:` preamble in MCP `search`

Render the summaries as a compact block before `results:`. Absent when empty; the `results:` section is unchanged.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`FormatRichSearchResults` ~line 979; `Search` ~line 836; add `AppendEntityFactsBlock`)
- Test: `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs` (add rendering cases)

**Interfaces:**
- Consumes: `EntityFactSummary`, `CoOccurrence` (Task 2); `SearchOutcome` (Task 3).
- Produces: `FormatRichSearchResults(IMemoryStore, IReadOnlyList<SearchResult>, IReadOnlyList<EntityFactSummary>)` — the entity-facts block now precedes `results:`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs` (inside the class). `new ScriniaMcpTools().Search(...)` reads the ambient `MemoryStoreContext.Current` that `StoreScope` set:

```csharp
    [Fact]
    public async Task RendersEntityFactsBlock_BeforeResults()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("m2", ("person", ["jolene"]), ("place", ["france"])), "local:findings");

        string yaml = await new ScriniaMcpTools().Search("jolene", cancellationToken: default);

        yaml.Should().Contain("entity_facts:");
        yaml.Should().Contain("person:jolene");
        yaml.Should().Contain("2 memories");
        yaml.Should().Contain("with place:france (2)");
        yaml.IndexOf("entity_facts:", StringComparison.Ordinal)
            .Should().BeLessThan(yaml.IndexOf("results:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleMention_UsesSingular_AndNoCoOccurrenceClause()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("solo", ("person", ["jolene"])), "local:findings");

        string yaml = await new ScriniaMcpTools().Search("jolene", cancellationToken: default);

        yaml.Should().Contain("1 memory");
        yaml.Should().NotContain("; with ");
    }

    [Fact]
    public async Task NoEntityNamed_NoBlock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        store.Upsert(Note("m1", ("person", ["jolene"])), "local:findings");

        // "seraphim" is only in ContentPreview (so it matches for a results list) but is NOT an
        // entity attribute on any memory, so the seeder names no entity -> no block.
        string yaml = await new ScriniaMcpTools().Search("seraphim", cancellationToken: default);

        yaml.Should().NotContain("entity_facts:");
        yaml.Should().Contain("results:");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AutoSurfaceEntityFactsTests"`
Expected: FAIL — no `entity_facts:` block is emitted yet.

- [ ] **Step 3: Add `AppendEntityFactsBlock` and thread it through `FormatRichSearchResults`**

In `src/Scrinia.Mcp/MemoryTools.Core.cs`, change `FormatRichSearchResults` to accept and render the summaries:

```csharp
    private static string FormatRichSearchResults(
        IMemoryStore store, IReadOnlyList<SearchResult> matches, IReadOnlyList<EntityFactSummary> entityFacts)
    {
        var sb = new System.Text.StringBuilder();
        AppendEntityFactsBlock(sb, entityFacts);
        sb.Append("results: ").Append(matches.Count).Append('\n');

        for (int i = 0; i < matches.Count; i++)
        {
            sb.Append('\n');
            AppendResultBlock(sb, store, matches[i], i + 1);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendEntityFactsBlock(
        System.Text.StringBuilder sb, IReadOnlyList<EntityFactSummary> facts)
    {
        if (facts.Count == 0) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("entity_facts:\n");
        foreach (var f in facts)
        {
            sb.Append("  ").Append(f.Entity.Key).Append(':').Append(f.Entity.Value)
              .Append(" — ").Append(f.MentionCount).Append(f.MentionCount == 1 ? " memory" : " memories");
            if (f.First is { } first && f.Last is { } last)
                sb.Append(", ")
                  .Append(first.ToString("yyyy-MM-dd", ci)).Append('→').Append(last.ToString("yyyy-MM-dd", ci));
            if (f.TopCoOccurring.Count > 0)
            {
                sb.Append("; with ");
                for (int i = 0; i < f.TopCoOccurring.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var c = f.TopCoOccurring[i];
                    sb.Append(c.Entity.Key).Append(':').Append(c.Entity.Value)
                      .Append(" (").Append(c.Count).Append(')');
                }
            }
            sb.Append('\n');
        }
        sb.Append('\n'); // blank line between the block and results:
    }
```

Update the `Search` method's format call to pass the summaries:

```csharp
        string body = FormatRichSearchResults(store, outcome.Matches, outcome.EntityFacts);
```

(Replace the Task-3 interim `var matches = outcome.Matches; ... FormatRichSearchResults(store, matches);` — keep the empty-match early return using `outcome.Matches.Count`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~AutoSurfaceEntityFactsTests"`
Expected: PASS (all 7 auto-surface tests).

- [ ] **Step 5: Full suite — confirm no regressions**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS — full suite green (baseline was 1086 passed / 0 failed / 4 skipped before this piece; expect +~14 new tests, 0 failures).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/AutoSurfaceEntityFactsTests.cs
git commit -m "$(cat <<'EOF'
feat(search): render entity_facts: preamble in MCP search

When a query names an entity, the search response leads with a compact
per-entity block (count + date span + top co-occurrences) before the
results list. Absent when no entity is named; the results section is
unchanged. Auto-surfacing (Piece 3) is now agent-visible.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Self-review notes (traceability to spec)

- **Trigger = query names entity** → Task 1 (`EntityQuerySeeder`, reused by both graph and surfacing).
- **Content = count + span + co-occ** → Task 2 (`EntityFactSummarizer`).
- **Universe = searched candidates** → Task 3 (`BuildEntityFacts` derives from the scope+excludeTopics-filtered `candidates`; the `ExcludedTopic_DoesNotCountTowardSummary` test is the guard).
- **Shape = preamble ≤3, MCP only** → Task 4 (`AppendEntityFactsBlock`, before `results:`); `maxEntities: 3` default; CLI keeps the flag off.
- **`mention:` uniform** → falls out for free: `EntityFactDerivation` treats every `(key,value)` uniformly, so a named `mention:x` summarizes like any other key. No special-casing needed.
- **No behavior change** → Task 3 `FlagOff_NoSummary` (CLI parity) + Task 4 `NoEntityNamed_NoBlock`; the `results:` builder is untouched except for the prepended block.
- **Deferred** (intersection, timeline, CLI table, recall, semantic detection) → not implemented; recorded as Non-goals in the spec.
