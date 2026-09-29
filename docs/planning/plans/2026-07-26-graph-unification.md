# Graph Unification (Piece 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire the per-query `EntityGraph` rebuild in production — run the entity-graph PPR signal over an `EntityFactStore`'s adjacency instead, reusing the cached cross-scope store on the fully-unfiltered query — with **no change to search ranking**.

**Architecture:** `MemoryEntities` carries the raw `(Scope, Subject)` identity and derives both its user-facing `MemoryId` and the scorer's `ScoringKey` (`"{scope}|{subject}"`), so the store's adjacency is natively scoring-keyed. The PPR walk is extracted from `EntityGraph` into a pure `PersonalizedPageRank`. `EntityGraphScoreContributor` runs that walk over an `EntityFactStore` handed to it by the caller; the caller selects the cached `GetEntityFactStore()` when fully unfiltered, else `BuildStore(candidates)`.

**Tech Stack:** C# / .NET, xUnit + FluentAssertions.

## Global Constraints

- **No ranking change.** This is an optimization + dedup, never a behavior change. The equivalence unit test (old `EntityGraph` path vs new store path) and the end-to-end ranking test are the gate — both must pass before merge.
- **Constants unchanged:** `DefaultAlpha = 0.15`, `DefaultIterations = 20`, `DefaultWeight = 50.0`. The max-normalization and every `None` short-circuit are copied verbatim.
- **Filtered universe is load-bearing.** A `scopes`/`excludeTopics` query must keep building the store from the handed `candidates`; never substitute the cross-scope cache for it. Excluded-topic memories must never become PPR nodes.
- **Sidecar is a rebuildable cache.** Bumping `EntityFactSidecar.Version` auto-invalidates every on-disk `facts.json`; no hand migration.
- **Build/test:** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false` (add `--filter "FullyQualifiedName~<Name>"` to target). Baseline suite: **1140 passed / 0 failed / 4 skipped**. Trust `dotnet test` over LSP diagnostics (known phantom CS errors during renames).
- **Failure seam:** use the internal `EntityFactDerivation.DeriveForStore` delegate defined by the
  EntityFactStore plan: `internal static Func<ScopedArtifact, MemoryEntities?> DeriveForStore { get; set; } = Derive;`.
  `BuildStore` and `FileMemoryStore.GetEntityFactStore()` call it; there is no public API change and the
  production default remains `Derive`.
- **Final integration base:** capture `INTENDED_BASE` immediately before execution with
  `INTENDED_BASE=$(git rev-parse HEAD)` (PowerShell: `$env:INTENDED_BASE = git rev-parse HEAD`). Use that
  fixed SHA for every range or scope comparison. Never infer a base from `origin/main` or another remote.
  Preserve the approved plan-set requirements for supported RIDs, the external output root, the exact MCP
  probe, workflow fixture/parser/job gating, commit boundaries, dependency/order, citations, headers,
  historical-document protection, and untracked-artifact exclusions.

---

## File Structure

**Create**
- `src/Scrinia.Core/Search/PersonalizedPageRank.cs` — the extracted pure PPR walk.
- `tests/Scrinia.Tests/PersonalizedPageRankTests.cs`
- `tests/Scrinia.Tests/EntityFactStoreScoringAdjacencyTests.cs`
- `tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs`

**Modify**
- `src/Scrinia.Core/MemoryNaming.cs` — add pure `FormatRoundTrip(scope, subject)`.
- `src/Scrinia.Core/IMemoryStore.cs` — `FormatRoundTripQualifiedName` delegates; `GetEntityFactStore` default via `BuildStore`.
- `src/Scrinia.Core/Search/EntityFact.cs` — `MemoryEntities` raw identity + derived `MemoryId`/`ScoringKey`.
- `src/Scrinia.Core/Search/EntityFactDerivation.cs` — `Derive(ScopedArtifact)` drops `memoryId`; add `BuildStore`.
- `src/Scrinia.Core/Search/EntityFactSidecar.cs` — v2; serialize `(Scope, Subject)`.
- `src/Scrinia.Core/FileMemoryStore.cs` — `Derive` calls + `ComputeToken` id.
- `src/Scrinia.Core/Search/EntityGraph.cs` — `Score` delegates to `PersonalizedPageRank`.
- `src/Scrinia.Core/Search/EntityFactStore.cs` — add `ScoringAdjacency()`.
- `src/Scrinia.Core/Search/EntityGraphScoreContributor.cs` — add `Score(EntityFactStore, query)`; `ComputeScoresAsync` delegates.
- `src/Scrinia.Core/Search/ISearchScoreContributor.cs` — retype `EntityGraphScoreContext` to concrete.
- `src/Scrinia.Server/Services/PluginPipeline.cs` — call-site store selection.
- `src/Scrinia.Mcp/MemoryTools.Core.cs` — call-site store selection; `BuildEntityFacts` via `BuildStore`.
- `src/Scrinia.Mcp/MemoryTools.Facts.cs` — `SelectFactStore` via `BuildStore`.
- `tests/Scrinia.Tests/EntityFactDerivationTests.cs`, `tests/Scrinia.Tests/EntityFactSidecarTests.cs`, `tests/Scrinia.Tests/EntityGraphSourceSearchTests.cs` — updated for the new shapes.

---

### Task 1: Pure qualified-name helper

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:33-36`
- Test: `tests/Scrinia.Tests/MemoryNamingTests.cs` (create or append)

**Interfaces:**
- Produces: `public static string MemoryNaming.FormatRoundTrip(string scope, string subject)` — pure, no store instance. Behaviorally identical to today's `IMemoryStore.FormatRoundTripQualifiedName` default.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/MemoryNamingTests.cs
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class MemoryNamingTests
{
    [Theory]
    [InlineData("local:memory/api", "auth", "/memory/api/auth")] // local: keeps the namespace segment
    [InlineData("local", "SEC-001", "SEC-001")]                  // bare local -> subject as-is
    [InlineData("ephemeral", "note", "note")]                    // non-local -> subject as-is
    public void FormatRoundTrip_MatchesLegacyDefault(string scope, string subject, string expected) =>
        MemoryNaming.FormatRoundTrip(scope, subject).Should().Be(expected);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MemoryNamingTests"`
Expected: FAIL — `MemoryNaming.FormatRoundTrip` does not exist.

- [ ] **Step 3: Add the pure static and delegate the interface default**

In `src/Scrinia.Core/MemoryNaming.cs`, add to the `MemoryNaming` static class:

```csharp
/// <summary>
/// Pure form of <see cref="IMemoryStore.FormatRoundTripQualifiedName"/>: builds a qualified name
/// that re-parses back to <paramref name="scope"/>, keeping the internal "memory/"/"entity/"
/// namespace segment (lossless), without needing a store instance.
/// </summary>
public static string FormatRoundTrip(string scope, string subject) =>
    scope.StartsWith("local:", StringComparison.Ordinal)
        ? $"/{scope["local:".Length..]}/{subject}"
        : subject;
```

In `src/Scrinia.Core/IMemoryStore.cs`, replace the default body (lines 33-36) with a delegation:

```csharp
string FormatRoundTripQualifiedName(string scope, string subject) =>
    MemoryNaming.FormatRoundTrip(scope, subject);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MemoryNamingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/MemoryNaming.cs src/Scrinia.Core/IMemoryStore.cs tests/Scrinia.Tests/MemoryNamingTests.cs
rtk git commit -m "refactor(naming): extract pure MemoryNaming.FormatRoundTrip"
```

---

### Task 2: `MemoryEntities` raw identity + `BuildStore` + sidecar v2 (atomic type-shape change)

This is one atomic task: changing `MemoryEntities`'s shape breaks every constructor site, so `Derive`, the sidecar, the store defaults, and their tests must all land together to compile.

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityFact.cs:14-17`
- Modify: `src/Scrinia.Core/Search/EntityFactDerivation.cs`
- Modify: `src/Scrinia.Core/Search/EntityFactSidecar.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:76-80`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1406-1425`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:919-927` and `src/Scrinia.Mcp/MemoryTools.Facts.cs:62-72`
- Test: `tests/Scrinia.Tests/EntityFactDerivationTests.cs`, `tests/Scrinia.Tests/EntityFactSidecarTests.cs`

**Interfaces:**
- Produces:
  - `MemoryEntities(string Scope, string Subject, DateTimeOffset Date, IReadOnlyList<EntityRef> Entities)` with `MemoryId => MemoryNaming.FormatRoundTrip(Scope, Subject)` and `ScoringKey => $"{Scope}|{Subject}"`.
  - `EntityFactDerivation.Derive(ScopedArtifact item)` — no `memoryId` param.
  - `EntityFactDerivation.BuildStore(IEnumerable<ScopedArtifact> items) → EntityFactStore`.
- Consumes: `MemoryNaming.FormatRoundTrip` (Task 1).

- [ ] **Step 1: Update the two failing tests first (they encode the new shape)**

`tests/Scrinia.Tests/EntityFactDerivationTests.cs` — call `Derive(item)` and assert the derived `MemoryId`/`ScoringKey`:

```csharp
[Fact]
public void Derive_UnionsKeywordAndAttributeEntities_DatesByUpdatedThenCreated()
{
    var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    var updated = DateTimeOffset.Parse("2023-06-01T00:00:00Z");
    var item = Entry("m1", "local", created, updated,
        keywords: ["person:jolene", "plain-term"],
        attributes: new() { ["place"] = ["france"], ["thing"] = ["seraphim"] });

    var me = EntityFactDerivation.Derive(item);

    me.Should().NotBeNull();
    me!.Date.Should().Be(updated);
    me.MemoryId.Should().Be("m1");        // FormatRoundTrip("local", "m1")
    me.ScoringKey.Should().Be("local|m1");
    me.Entities.Should().BeEquivalentTo(new[]
    {
        EntityRef.Of("person", "jolene"),
        EntityRef.Of("place", "france"),
        EntityRef.Of("thing", "seraphim"),
    });
}

[Fact]
public void Derive_NoEntities_ReturnsNull()
{
    var item = Entry("m2", "local", DateTimeOffset.UtcNow, keywords: ["just", "plain", "words"]);
    EntityFactDerivation.Derive(item).Should().BeNull();
}
```

`tests/Scrinia.Tests/EntityFactSidecarTests.cs` — the round-trip constructs `MemoryEntities` with `(scope, subject, …)` and the load must round-trip identity; add a v2 assertion:

```csharp
[Fact]
public void SerializeThenTryLoad_RoundTrips()
{
    var mems = new[]
    {
        new MemoryEntities("local", "m1", D1, [EntityRef.Of("person", "jolene"), EntityRef.Of("place", "france")]),
    };
    string token = EntityFactSidecar.ComputeToken([("local|m1", D1, "sig")]);

    string json = EntityFactSidecar.Serialize(token, mems);
    var loaded = EntityFactSidecar.TryLoad(json);

    loaded.Should().NotBeNull();
    loaded!.Value.Token.Should().Be(token);
    loaded.Value.Memories.Should().BeEquivalentTo(mems);       // Scope+Subject survive the round-trip
    loaded.Value.Memories[0].ScoringKey.Should().Be("local|m1");
}

[Fact]
public void TryLoad_RejectsV1Sidecar()
{
    // A v1 document (old "id"-keyed schema) must be rejected so it rebuilds as v2.
    string v1 = """{"version":1,"token":"x","memories":[{"id":"m1","date":"2023-01-01T00:00:00+00:00","entities":[]}]}""";
    EntityFactSidecar.TryLoad(v1).Should().BeNull();
}
```

(Keep the existing `ComputeToken_*` and `TryLoad_RejectsWrongVersion` tests as-is.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactDerivationTests|FullyQualifiedName~EntityFactSidecarTests"`
Expected: FAIL to compile (`Derive` arity, `MemoryEntities` ctor).

- [ ] **Step 3: Change `MemoryEntities` in `EntityFact.cs`**

```csharp
/// <summary>Compact per-memory unit: the memory's raw identity, effective date, and distinct entity
/// set. Stores the raw <c>(Scope, Subject)</c> and derives both presentations: <see cref="MemoryId"/>
/// (the recall-able qualified name, user-facing) and <see cref="ScoringKey"/> (the scorer's
/// "{scope}|{name}" join key). One identity, two views — no translation layer.</summary>
public sealed record MemoryEntities(
    string Scope,
    string Subject,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> Entities)
{
    /// <summary>Recall-able qualified name (unchanged value vs the old stored MemoryId).</summary>
    public string MemoryId => MemoryNaming.FormatRoundTrip(Scope, Subject);

    /// <summary>The scorer's join key, shared with the entity-graph / path-segment / embeddings
    /// contributors and <c>WeightedAttributeScorer.GetSupplemental</c>.</summary>
    public string ScoringKey => $"{Scope}|{Subject}";
}
```

Add `using Scrinia.Core;` to `EntityFact.cs` if `MemoryNaming` is not already in scope.

- [ ] **Step 4: Simplify `Derive` and add `BuildStore` in `EntityFactDerivation.cs`**

```csharp
public static MemoryEntities? Derive(ScopedArtifact item)
{
    var e = item.Entry;
    var entities = EntityGraph.ParseEntityKeywords(e.Keywords)
        .Concat(EntityGraph.ParseEntityAttributes(e.Attributes))
        .Select(t => EntityRef.Of(t.Type, t.Value))
        .Distinct()
        .ToArray();

    if (entities.Length == 0) return null;
    return new MemoryEntities(item.Scope, item.Entry.Name, e.UpdatedAt ?? e.CreatedAt, entities);
}

/// <summary>Derive→filter→build the one shared way. Folds the duplicate derive loops in
/// <c>IMemoryStore.GetEntityFactStore</c>, <c>MemoryTools.Core.BuildEntityFacts</c>, and
/// <c>MemoryTools.Facts.SelectFactStore</c>; also the entity-graph filtered path (Task 6).</summary>
public static EntityFactStore BuildStore(IEnumerable<ScopedArtifact> items) =>
    new(items.Select(Derive).Where(m => m is not null).Select(m => m!));
```

- [ ] **Step 5: Bump the sidecar to v2 in `EntityFactSidecar.cs`**

Set `public const int Version = 2;`. Serialize `(Scope, Subject)` and reconstruct them on load:

```csharp
public static string Serialize(string token, IReadOnlyList<MemoryEntities> memories)
{
    var dto = new Dto(
        Version, token,
        memories.Select(m => new MemDto(
            m.Scope, m.Subject, m.Date,
            m.Entities.Select(e => new RefDto(e.Key, e.Value)).ToArray())).ToArray());
    return JsonSerializer.Serialize(dto, SidecarJsonContext.Default.Dto);
}

public static (string Token, IReadOnlyList<MemoryEntities> Memories)? TryLoad(string json)
{
    Dto? dto;
    try { dto = JsonSerializer.Deserialize(json, SidecarJsonContext.Default.Dto); }
    catch (JsonException) { return null; }
    if (dto is null || dto.Version != Version) return null;

    var mems = dto.Memories
        .Select(m => new MemoryEntities(
            m.Scope, m.Subject, m.Date,
            m.Entities.Select(r => new EntityRef(r.Key, r.Value)).ToArray()))
        .ToArray();
    return (dto.Token, mems);
}
```

Update `MemDto` to carry scope + subject (short JSON names, since v2 already invalidates all old files):

```csharp
internal sealed record MemDto(
    [property: JsonPropertyName("s")] string Scope,
    [property: JsonPropertyName("j")] string Subject,
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("entities")] RefDto[] Entities);
```

- [ ] **Step 6: Update the store defaults and MCP call sites to the new signatures**

`src/Scrinia.Core/IMemoryStore.cs` (`GetEntityFactStore` default, lines 76-80):

```csharp
EntityFactStore GetEntityFactStore() =>
    EntityFactDerivation.BuildStore(ListScoped(null));
```

`src/Scrinia.Core/FileMemoryStore.cs`:
- `ComputeToken` id (line 1407): use the scoring key rather than the qualified name —
  `items.Select(sa => ($"{scope}|{sa.Entry.Name}", sa.Entry.UpdatedAt ?? sa.Entry.CreatedAt, EntitySignature(sa.Entry)))`.
- The rebuild derive loop (lines 1420-1425): `mems = items.Select(EntityFactDerivation.Derive).Where(m => m is not null).Select(m => m!).ToArray();`

`src/Scrinia.Mcp/MemoryTools.Core.cs` (`BuildEntityFacts`, lines 922-927):

```csharp
var factStore = EntityFactDerivation.BuildStore(candidates);
```

`src/Scrinia.Mcp/MemoryTools.Facts.cs` (`SelectFactStore`, lines 62-72):

```csharp
private static EntityFactStore SelectFactStore(IMemoryStore store, string? scopes, string? excludeTopics)
{
    if (string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics))
        return store.GetEntityFactStore();
    return EntityFactDerivation.BuildStore(store.ListScoped(scopes, excludeTopics));
}
```

- [ ] **Step 7: Run the affected suites to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFact|FullyQualifiedName~TryUpdateEntry|FullyQualifiedName~EntityTyping"`
Expected: PASS (identity round-trips; sidecar v2; store aggregation unchanged).

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Core/Search/EntityFact.cs src/Scrinia.Core/Search/EntityFactDerivation.cs src/Scrinia.Core/Search/EntityFactSidecar.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Facts.cs tests/Scrinia.Tests/EntityFactDerivationTests.cs tests/Scrinia.Tests/EntityFactSidecarTests.cs
rtk git commit -m "refactor(facts): MemoryEntities carries raw (scope,subject); derive MemoryId+ScoringKey; sidecar v2; BuildStore primitive"
```

---

### Task 3: Extract `PersonalizedPageRank`

**Files:**
- Create: `src/Scrinia.Core/Search/PersonalizedPageRank.cs`
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs:185-261`
- Test: `tests/Scrinia.Tests/PersonalizedPageRankTests.cs`

**Interfaces:**
- Produces: `PersonalizedPageRank.Score(IReadOnlyDictionary<string, IReadOnlyCollection<string>> entityToDocs, IReadOnlyDictionary<string, IReadOnlyCollection<string>> docToEntities, IReadOnlyCollection<string> seeds, double alpha, int iterations) → IReadOnlyDictionary<string, double>` (doc key → mass).
- Consumes: nothing new. `EntityGraph.Score` becomes a thin caller (its `EntityGraphTests` are the parity guard).

- [ ] **Step 1: Write the failing test** (a hand-built two-entity/two-doc graph; seed diffusion + restart)

```csharp
// tests/Scrinia.Tests/PersonalizedPageRankTests.cs
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class PersonalizedPageRankTests
{
    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Map(
        params (string Key, string[] Vals)[] rows) =>
        rows.ToDictionary(r => r.Key, r => (IReadOnlyCollection<string>)r.Vals);

    [Fact]
    public void Score_DiffusesFromSeed_AcrossSharedEntity_AndFavoursTheSeededDoc()
    {
        // person:a in d1; person:a & person:b in d2; person:b in d3. Seed person:a.
        var e2d = Map(("person:a", ["d1", "d2"]), ("person:b", ["d2", "d3"]));
        var d2e = Map(("d1", ["person:a"]), ("d2", ["person:a", "person:b"]), ("d3", ["person:b"]));

        var mass = PersonalizedPageRank.Score(e2d, d2e, ["person:a"], 0.15, 20);

        mass.Should().ContainKeys("d1", "d2", "d3");
        mass["d3"].Should().BeGreaterThan(0);            // reached via the shared person:b bridge
        mass["d1"].Should().BeGreaterThan(mass["d3"]);    // closer to the seed than the bridged doc
    }

    [Fact]
    public void Score_NoSeeds_ReturnsEmpty()
    {
        var e2d = Map(("person:a", ["d1"]));
        var d2e = Map(("d1", ["person:a"]));
        PersonalizedPageRank.Score(e2d, d2e, [], 0.15, 20).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~PersonalizedPageRankTests"`
Expected: FAIL — `PersonalizedPageRank` does not exist.

- [ ] **Step 3: Create `PersonalizedPageRank.cs`** — lift the walk body verbatim from `EntityGraph.Score` (lines 193-260), parameterized on the two maps + seeds:

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The Personalized PageRank walk extracted from <see cref="EntityGraph"/> so both the graph and the
/// persisted <see cref="EntityFactStore"/> share one implementation. Bipartite two-hop random walk
/// with restart: mass flows entity→doc→entity each iteration, teleporting to the (uniform) seed set
/// with probability <paramref name="alpha"/>. Returns the stationary mass per doc key. Pure.
/// </summary>
public static class PersonalizedPageRank
{
    public static IReadOnlyDictionary<string, double> Score(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> entityToDocs,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> docToEntities,
        IReadOnlyCollection<string> seeds, double alpha, int iterations)
    {
        var docScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (entityToDocs.Count == 0 || seeds.Count == 0) return docScore;

        if (alpha < 0) alpha = 0;
        if (alpha > 1) alpha = 1;
        if (iterations < 1) iterations = 1;

        double seedMass = 1.0 / seeds.Count;
        var restart = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in seeds) restart[s] = seedMass;

        var entityScore = new Dictionary<string, double>(restart, StringComparer.OrdinalIgnoreCase);

        for (int it = 0; it < iterations; it++)
        {
            docScore.Clear();
            foreach (var (entity, p) in entityScore)
            {
                if (p <= 0) continue;
                var docs = entityToDocs[entity];
                double share = p / docs.Count;
                foreach (string d in docs)
                {
                    docScore.TryGetValue(d, out double acc);
                    docScore[d] = acc + share;
                }
            }

            var nextEntity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (doc, p) in docScore)
            {
                if (p <= 0) continue;
                var ents = docToEntities[doc];
                double share = p / ents.Count;
                foreach (string e in ents)
                {
                    nextEntity.TryGetValue(e, out double acc);
                    nextEntity[e] = acc + share;
                }
            }

            entityScore = new Dictionary<string, double>(nextEntity.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (e, p) in nextEntity)
                entityScore[e] = (1 - alpha) * p;
            foreach (var (e, p) in restart)
            {
                entityScore.TryGetValue(e, out double acc);
                entityScore[e] = acc + alpha * p;
            }
        }

        docScore.Clear();
        foreach (var (entity, p) in entityScore)
        {
            if (p <= 0) continue;
            var docs = entityToDocs[entity];
            double share = p / docs.Count;
            foreach (string d in docs)
            {
                docScore.TryGetValue(d, out double acc);
                docScore[d] = acc + share;
            }
        }

        return docScore;
    }
}
```

- [ ] **Step 4: Make `EntityGraph.Score` delegate** — keep its `query`/empty/seed guards, project its `HashSet`-valued dicts to `IReadOnlyCollection<string>`, and call the extracted walk:

```csharp
public IReadOnlyDictionary<string, double> Score(string query, double alpha, int iterations)
{
    var empty = (IReadOnlyDictionary<string, double>)new Dictionary<string, double>();
    if (string.IsNullOrWhiteSpace(query) || _entityToDocs.Count == 0) return empty;

    var seeds = SeedEntities(query);
    if (seeds.Count == 0) return empty;

    return PersonalizedPageRank.Score(
        _entityToDocs.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
        _docToEntities.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
        seeds, alpha, iterations);
}
```

- [ ] **Step 5: Run PPR tests + the EntityGraph parity guard**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~PersonalizedPageRankTests|FullyQualifiedName~EntityGraphTests"`
Expected: PASS (new PPR tests + existing `EntityGraphTests` unchanged).

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/Search/PersonalizedPageRank.cs src/Scrinia.Core/Search/EntityGraph.cs tests/Scrinia.Tests/PersonalizedPageRankTests.cs
rtk git commit -m "refactor(search): extract PersonalizedPageRank; EntityGraph.Score delegates"
```

---

### Task 4: `EntityFactStore.ScoringAdjacency()`

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityFactStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactStoreScoringAdjacencyTests.cs`

**Interfaces:**
- Produces: `EntityFactStore.ScoringAdjacency() → (IReadOnlyDictionary<string, IReadOnlyCollection<string>> EntityToDocs, IReadOnlyDictionary<string, IReadOnlyCollection<string>> DocToEntities)` — entity nodes `"{key}:{value}"`, doc keys = each memory's `ScoringKey`. Feeds `PersonalizedPageRank.Score`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/EntityFactStoreScoringAdjacencyTests.cs
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactStoreScoringAdjacencyTests
{
    [Fact]
    public void ScoringAdjacency_KeysEntitiesByTypeValue_AndDocsByScoringKey()
    {
        var store = new EntityFactStore(new[]
        {
            new MemoryEntities("local", "m1", default, [EntityRef.Of("person", "a")]),
            new MemoryEntities("local:memory/api", "m2", default,
                [EntityRef.Of("person", "a"), EntityRef.Of("place", "paris")]),
        });

        var (e2d, d2e) = store.ScoringAdjacency();

        e2d.Should().ContainKey("person:a");
        e2d.Should().ContainKey("place:paris");
        e2d["person:a"].Should().BeEquivalentTo(new[] { "local|m1", "local:memory/api|m2" });
        d2e.Should().ContainKey("local:memory/api|m2");
        d2e["local:memory/api|m2"].Should().BeEquivalentTo(new[] { "person:a", "place:paris" });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreScoringAdjacencyTests"`
Expected: FAIL — `ScoringAdjacency` does not exist.

- [ ] **Step 3: Add `ScoringAdjacency` to `EntityFactStore`** — one pass over `_byEntity`, building both maps:

```csharp
/// <summary>Projects the store into the two adjacency maps <see cref="PersonalizedPageRank.Score"/>
/// consumes: entity nodes as "{key}:{value}" (matching <see cref="EntityGraph"/> node identity) and
/// doc keys as each memory's <see cref="MemoryEntities.ScoringKey"/>. Built from the existing index;
/// no new derivation.</summary>
public (IReadOnlyDictionary<string, IReadOnlyCollection<string>> EntityToDocs,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> DocToEntities) ScoringAdjacency()
{
    var e2d = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    var d2e = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    foreach (var (entity, mems) in _byEntity)
    {
        string node = $"{entity.Key}:{entity.Value}";
        foreach (var m in mems)
        {
            (e2d.TryGetValue(node, out var docs) ? docs : e2d[node] = new(StringComparer.OrdinalIgnoreCase))
                .Add(m.ScoringKey);
            (d2e.TryGetValue(m.ScoringKey, out var ents) ? ents : d2e[m.ScoringKey] = new(StringComparer.OrdinalIgnoreCase))
                .Add(node);
        }
    }

    return (e2d.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
            d2e.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value, StringComparer.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreScoringAdjacencyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/Search/EntityFactStore.cs tests/Scrinia.Tests/EntityFactStoreScoringAdjacencyTests.cs
rtk git commit -m "feat(search): EntityFactStore.ScoringAdjacency projects store into PPR maps"
```

---

### Task 5: `EntityGraphScoreContributor.Score(EntityFactStore, query)` + equivalence safety net

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityGraphScoreContributor.cs`
- Test: `tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs`

**Interfaces:**
- Produces: `EntityGraphScoreContributor.Score(EntityFactStore factStore, string query) → IReadOnlyDictionary<string, double>?` — the store-backed PPR (max-normalized × weight), keyed `"{scope}|{name}"`.
- Consumes: `EntityFactStore.ScoringAdjacency` (Task 4), `PersonalizedPageRank.Score` (Task 3), `EntityFactDerivation.BuildStore` (Task 2), `EntityQuerySeeder.Seed`.
- The existing `ComputeScoresAsync(query, candidates, store, ct)` stays and now delegates to `Score(BuildStore(candidates), query)` — the path benchmarks/unit tests exercise.

- [ ] **Step 1: Write the failing equivalence test** (the safety net)

```csharp
// tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs
using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityGraphEquivalenceTests
{
    private static ScopedArtifact Doc(string scope, string name, string[] keywords) =>
        new(scope, new ArtifactEntry(
            Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: default, Keywords: keywords, UpdatedAt: null, Attributes: null));

    [Theory]
    [InlineData("what did alice do")]
    [InlineData("bob paris")]
    [InlineData("nothing matches here")]
    public void StorePath_EqualsGraphPath_WithinTolerance(string query)
    {
        var docs = new[]
        {
            Doc("local", "d1", ["person:alice"]),
            Doc("local", "d2", ["person:alice", "person:bob"]),
            Doc("local", "d3", ["person:bob", "place:paris"]),
            Doc("local", "d4", ["place:paris"]),
        };

        // Old production path: build the graph from the candidates, score it.
        var graphDocs = docs.Select(d => ($"{d.Scope}|{d.Entry.Name}",
            (IReadOnlyList<string>?)d.Entry.Keywords,
            (IReadOnlyDictionary<string, string[]>?)null));
        var graphScores = EntityGraph.Build(graphDocs).Score(query, 0.15, 20);

        // New path: PPR over the store built from the same candidates.
        var storeScores = new EntityGraphScoreContributor()
            .Score(EntityFactDerivation.BuildStore(docs), query);

        // graphScores is the raw PPR mass; the contributor's Score is max-normalized × weight.
        // Compare the *ranking* and the normalized shape: both empty, or same keys with proportional values.
        if (graphScores.Count == 0) { (storeScores is null || storeScores.Count == 0).Should().BeTrue(); return; }

        storeScores.Should().NotBeNull();
        storeScores!.Keys.Should().BeEquivalentTo(graphScores.Keys);

        double gMax = graphScores.Values.Max();
        foreach (var (k, raw) in graphScores)
            storeScores[k].Should().BeApproximately(raw / gMax * EntityGraphScoreContributor.DefaultWeight, 1e-9);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityGraphEquivalenceTests"`
Expected: FAIL — `Score(EntityFactStore, string)` does not exist.

- [ ] **Step 3: Add `Score` and delegate `ComputeScoresAsync`** in `EntityGraphScoreContributor.cs`:

```csharp
public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
    string query, IReadOnlyList<ScopedArtifact> candidates, IMemoryStore store, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(query) || candidates is null || candidates.Count == 0)
        return None;
    return Task.FromResult(Score(EntityFactDerivation.BuildStore(candidates), query));
}

/// <summary>Runs the entity-graph PPR signal over a ready <see cref="EntityFactStore"/> — the caller
/// selects the store (cached cross-scope when fully unfiltered, else a scoped build over the query's
/// candidates). Max-normalizes the PPR mass and scales by the weight; keyed by "{scope}|{name}".
/// Returns null when there are no seeds, no reachable docs, or zero mass (the None short-circuits).</summary>
public IReadOnlyDictionary<string, double>? Score(EntityFactStore factStore, string query)
{
    if (string.IsNullOrWhiteSpace(query)) return null;

    var (entityToDocs, docToEntities) = factStore.ScoringAdjacency();
    if (entityToDocs.Count == 0) return null;

    var seeds = EntityQuerySeeder.Seed(query, entityToDocs.Keys);
    if (seeds.Count == 0) return null;

    var ppr = PersonalizedPageRank.Score(entityToDocs, docToEntities, seeds, _alpha, _iterations);
    if (ppr.Count == 0) return null;

    double max = 0;
    foreach (double v in ppr.Values)
        if (v > max) max = v;
    if (max <= 0) return null;

    var scores = new Dictionary<string, double>(ppr.Count, StringComparer.OrdinalIgnoreCase);
    foreach (var (key, mass) in ppr)
        scores[key] = (mass / max) * _weight;
    return scores;
}
```

Remove the now-unused `EntityGraph.Build` block from `ComputeScoresAsync`. Keep the `None` helper.

- [ ] **Step 4: Run equivalence + existing contributor tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityGraphEquivalenceTests|FullyQualifiedName~EntityGraphScoreContributorTests"`
Expected: PASS — the store path matches the graph path, and the existing contributor tests (candidate-pool + null store) still pass through the delegating `ComputeScoresAsync`.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Core/Search/EntityGraphScoreContributor.cs tests/Scrinia.Tests/EntityGraphEquivalenceTests.cs
rtk git commit -m "feat(search): EntityGraphScoreContributor runs PPR over EntityFactStore; equivalence-tested"
```

---

### Task 6: Call-site store selection + end-to-end ranking guard

**Files:**
- Modify: `src/Scrinia.Core/Search/ISearchScoreContributor.cs:48-58` (retype `EntityGraphScoreContext`)
- Modify: `src/Scrinia.Server/Services/PluginPipeline.cs:166-184`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:880-894`
- Test: `tests/Scrinia.Tests/EntityGraphStoreSelectionTests.cs` (create — cache-branch equivalence + excluded-topic invariant); full suite as the e2e ranking backstop

**Interfaces:**
- Consumes: `EntityGraphScoreContributor.Score` (Task 5), `EntityFactDerivation.BuildStore` (Task 2), `IMemoryStore.GetEntityFactStore`.
- `EntityGraphScoreContext.Current`/`Default` retyped to `EntityGraphScoreContributor?` (dedicated channel; only ever set to `new EntityGraphScoreContributor()`).

- [ ] **Step 1: Write the failing tests** — these guard the two things the call-site change introduces: (a) the unfiltered cache branch is behavior-identical to building from all candidates, and (b) a memory in an excluded topic never joins the diffusion. Both drive the contributor + a real `FileMemoryStore` directly (no server fixture). Create `tests/Scrinia.Tests/EntityGraphStoreSelectionTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityGraphStoreSelectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public EntityGraphStoreSelectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-egsel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private void Upsert(string name, string scope, params string[] keywords) =>
        _store.Upsert(new ArtifactEntry(name, $"test://{name}", 0, 0, DateTimeOffset.UtcNow, Keywords: keywords), scope);

    [Fact]
    public void UnfilteredCacheBranch_EqualsBuildFromAllCandidates()
    {
        // The call site substitutes GetEntityFactStore() for BuildStore(ListScoped(null)) in the
        // fully-unfiltered case. That substitution must be behavior-identical.
        Upsert("d1", "local", "person:alice");
        Upsert("d2", "local", "person:alice", "person:bob");
        Upsert("d3", "local", "person:bob");
        var contributor = new EntityGraphScoreContributor();

        var viaCache = contributor.Score(_store.GetEntityFactStore(), "alice");
        var viaBuild = contributor.Score(EntityFactDerivation.BuildStore(_store.ListScoped(null)), "alice");

        viaCache.Should().NotBeNull();
        viaCache!.Keys.Should().BeEquivalentTo(viaBuild!.Keys);
        foreach (var (k, v) in viaBuild)
            viaCache[k].Should().BeApproximately(v, 1e-9);
    }

    [Fact]
    public void Scorer_IsEquivalent_BeforeAndAfterCachedMaterialization()
    {
        Upsert("d1", "local", "person:alice");
        Upsert("d2", "local", "person:alice", "person:bob");
        Upsert("d3", "local", "person:bob");
        var contributor = new EntityGraphScoreContributor();
        var candidates = _store.ListScoped(null);

        var beforeCache = contributor.Score(EntityFactDerivation.BuildStore(candidates), "alice");
        var afterFirstBuild = contributor.Score(_store.GetEntityFactStore(), "alice");
        var afterCacheHit = contributor.Score(_store.GetEntityFactStore(), "alice");

        afterFirstBuild.Should().BeEquivalentTo(beforeCache);
        afterCacheHit.Should().BeEquivalentTo(beforeCache);
    }

    [Fact]
    public void Replacement_InvalidatesCachedAdjacency_AndReflectsChangedFacts()
    {
        Upsert("d1", "local", "person:alice");
        var before = _store.GetEntityFactStore();
        before.ScoringAdjacency().EntityToDocs.Should().ContainKey("person:alice");

        _store.Upsert(new ArtifactEntry("d1", "test://d1", 0, 0, DateTimeOffset.UtcNow,
            Keywords: ["place:paris"]), "local");

        var after = _store.GetEntityFactStore();
        after.Should().NotBeSameAs(before);
        after.ScoringAdjacency().EntityToDocs.Should().ContainKey("place:paris");
        after.ScoringAdjacency().EntityToDocs.Should().NotContainKey("person:alice");
    }

    [Fact]
    public async Task ConcurrentFirstAccess_UsesEquivalentMaterializedSnapshots()
    {
        Upsert("d1", "local", "person:alice", "place:paris");
        Upsert("d2", "local", "person:alice");

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _store.GetEntityFactStore())));

        snapshots.Should().OnlyContain(s => s.MentionCount(EntityRef.Of("person", "alice")) == 2);
        snapshots.Should().OnlyContain(s => ReferenceEquals(s, snapshots[0]));
        snapshots.Select(s => s.ScoringAdjacency())
            .Should().AllBeEquivalentTo(snapshots[0].ScoringAdjacency());
    }

    [Fact]
    public void Materializer_PropagatesOriginalDerivationException_AndNextAccessRecovers()
    {
        Upsert("d1", "local", "person:alice");
        var expected = new InvalidOperationException("derivation sentinel");
        var previous = EntityFactDerivation.DeriveForStore;
        bool fail = true;
        try
        {
            EntityFactDerivation.DeriveForStore = item =>
            {
                if (fail) throw expected;
                return EntityFactDerivation.Derive(item);
            };

            Action first = () => _store.GetEntityFactStore();
            first.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
            fail = false;
            _store.GetEntityFactStore().MentionCount(EntityRef.Of("person", "alice")).Should().Be(1);
        }
        finally
        {
            EntityFactDerivation.DeriveForStore = previous;
        }
    }

    [Fact]
    public void ExcludedTopicBridge_DropsTheAssociation()
    {
        // A bridge memory in an excludable topic connects alice<->bob. When that topic is excluded,
        // the bridge is absent from the built store, so bob's memory loses the alice-seeded lift.
        Upsert("d_alice", "local", "person:alice");
        Upsert("d_bob", "local", "person:bob");
        Upsert("bridge", "local:plan", "person:alice", "person:bob"); // the only alice-bob link

        var contributor = new EntityGraphScoreContributor();

        var withBridge = contributor.Score(_store.GetEntityFactStore(), "alice");
        var excluded = contributor.Score(
            EntityFactDerivation.BuildStore(_store.ListScoped(null, excludeTopics: "plan")), "alice");

        withBridge!.Should().ContainKey("local|d_bob");          // reached via the bridge
        (excluded is null || !excluded.ContainsKey("local|d_bob")).Should().BeTrue(); // bridge gone -> no lift
    }
}
```

(If `local:plan` is not how a `plan`-topic scope is spelled for `Upsert`/`excludeTopics` in this store, adjust to the fixture's topic-scope convention — the invariant under test is that the excluded memory is absent from the built adjacency.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityGraphStoreSelectionTests"`
Expected: FAIL — the tests reference `contributor.Score(EntityFactStore, query)` and the new selection behavior (green once Tasks 5 land and these assertions hold).

- [ ] **Step 3: Retype `EntityGraphScoreContext`** in `ISearchScoreContributor.cs` — swap `ISearchScoreContributor?` for `EntityGraphScoreContributor?` in the two fields and both accessors:

```csharp
public static class EntityGraphScoreContext
{
    private static readonly AsyncLocal<EntityGraphScoreContributor?> _current = new();
    private static EntityGraphScoreContributor? _default;

    public static EntityGraphScoreContributor? Current { get => _current.Value ?? _default; set => _current.Value = value; }
    public static EntityGraphScoreContributor? Default { get => _default; set => _default = value; }
}
```

(Setters at `Program.cs:290,441` and `WorkspaceSetup.cs:71` already assign `new EntityGraphScoreContributor()` — no change needed there.)

- [ ] **Step 4: Wire store selection at `PluginPipeline.SearchAsync`** (lines 166-184). REST has no `excludeTopics`, so unfiltered ⇔ `scopes==null`:

```csharp
var contributor = SearchContributorContext.Current;
var entityContributor = EntityGraphScoreContext.Current;

IReadOnlyDictionary<string, double>? supplemental = null;
IReadOnlyDictionary<string, double>? entityScores = null;
if (contributor is not null || entityContributor is not null)
{
    var candidates = store.ListScoped(scopes);
    if (contributor is not null)
        supplemental = await contributor.ComputeScoresAsync(query, candidates, store, ct);
    if (entityContributor is not null)
    {
        var factStore = string.IsNullOrWhiteSpace(scopes)
            ? store.GetEntityFactStore()                       // cached, no rebuild
            : EntityFactDerivation.BuildStore(candidates);     // scoped
        entityScores = entityContributor.Score(factStore, query);
    }
}
```

- [ ] **Step 5: Wire store selection at `MemoryTools.Core`** (lines 880-894). MCP has both knobs, so unfiltered ⇔ `scopes==null && excludeTopics==null`:

```csharp
if (candidates is not null)
{
    if (contributor is not null)
        supplemental = await contributor.ComputeScoresAsync(effectiveQuery, candidates, store, cancellationToken);
    if (entityContributor is not null)
    {
        var factStore = string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics)
            ? store.GetEntityFactStore()                       // cached, no rebuild
            : EntityFactDerivation.BuildStore(candidates);     // scoped / topic-excluded
        entityScores = entityContributor.Score(factStore, query: effectiveQuery);
    }
}
```

(`candidates = store.ListScoped(scopes, excludeTopics)` at line 884 is unchanged — it still feeds embeddings + `surfaceEntityFacts`.)

- [ ] **Step 6: Run the selection tests, the existing entity-graph fusion tests, then the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityGraphStoreSelectionTests|FullyQualifiedName~EntityGraphSourceSearchTests"`
Expected: PASS (six selection/cache/invalidation/concurrency/failure guards + existing fusion tests unchanged).

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: **1140 passed / 0 failed / 4 skipped** (unchanged baseline — the whole point; this full run is the end-to-end ranking backstop).

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/Search/ISearchScoreContributor.cs src/Scrinia.Server/Services/PluginPipeline.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/EntityGraphStoreSelectionTests.cs
rtk git commit -m "feat(search): call-site EntityFactStore selection for entity-graph PPR; unfiltered reuses cache"
```

---

## Self-Review Notes (for the implementer)

- **Ranking is the gate.** Tasks 5 (equivalence) and 6 (e2e ranking + excluded-topic invariant) are the safety net. If either shifts, stop — do not "adjust the baseline."
- **Cache blockers are explicit.** `EntityGraphStoreSelectionTests` now covers scorer equivalence before
  materialization and after a cache hit, same-name replacement invalidation with changed adjacency,
  supported `Task.WhenAll` first access over the materialized immutable snapshot, and original-exception
  propagation plus recovery through the `FileMemoryStore` façade. The failure test uses only the internal
  `DeriveForStore` delegate; it introduces no public seam.
- **`Score` never touches `store`.** Benchmarks and `EntityGraphScoreContributorTests` pass a `null` store through `ComputeScoresAsync`; the delegation must not dereference it (it only calls `BuildStore(candidates)`).
- **Do not delete `EntityGraph`.** It stays for `EntityGraphTests`, the equivalence test, and the benchmark harness; production simply stops calling `Build`.
- **`effectiveQuery`** (MCP) vs `query` (REST) is preserved exactly — the MCP path already scores on `effectiveQuery`; keep passing it.
