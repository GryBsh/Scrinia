# `facts` explicit aggregate tool + CLI (Piece 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `facts` MCP tool (and `scri facts` CLI mirror) with three ops — `entities`, `profile`, `intersect` — for deliberate aggregate/relational queries over the entity fact store.

**Architecture:** A pure `FactsQuery` service projects the existing `EntityFactStore` API into result records (no new store logic). A `FactsResponseFormatter` renders those records to the standard `McpResponse` YAML. One `ScriniaMcpTools.Facts` handler selects the store (cross-scope cached by default; scoped when `scopes`/`excludeTopics` narrow it), dispatches by op, and formats. `scri facts` calls the same handler and prints the YAML — the established one-pipeline-two-surfaces pattern.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, ConsoleAppFramework (CLI).

## Global Constraints

- Target framework `net10.0`. Work from `C:/SynologyDrive/Source/repos/workspace-scrinia/Scrinia`.
- **Build/test the test project directly** (the full solution restore fails on a pre-existing `Scrinia.AppHost` NuGet-audit error): `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "<filter>"`. The final task runs the full suite (no filter).
- The C# LSP emits **phantom** CS errors against new/edited symbols mid-edit (CS0103/CS1739/CS1061/CS8019). Trust `dotnet test`/`dotnet build` output, not the diagnostic stream. A redundant `using Xunit;` in a new test file is a repo-wide convention (no build warning) — at most Minor.
- **No new `EntityFactStore` logic.** `FactsQuery` reuses only the existing public API: `Entities(string? key)`, `MentionCount(EntityRef)`, `Span(EntityRef)`, `Neighbors(EntityRef)`, `Timeline(EntityRef)`, `Facts(EntityRef)`, `MemoriesFor(params EntityRef[])`.
- Pure `FactsQuery` + result records live in namespace `Scrinia.Core.Search`. The formatter and tool live in `Scrinia.Mcp`; the CLI verb in `Scrinia.Commands`.
- Dates render as `yyyy-MM-dd`, `CultureInfo.InvariantCulture` (matching the search result blocks).
- Determinism: stable ordering everywhere (documented per op). Same store + args → same bytes.
- Keys/values lowercased via `EntityRef.Of` — `person:Jolene` and `jolene` resolve identically.
- DRY / YAGNI / TDD. Commit messages use the repo convention (`feat(facts): …`) and MUST end with the trailer line exactly:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

## Reference: existing types this plan builds on

```csharp
// src/Scrinia.Core/Search/EntityFact.cs
public readonly record struct EntityRef(string Key, string Value) { public static EntityRef Of(string key, string value); } // Of() trims + lowercases
public sealed record MemoryEntities(string MemoryId, DateTimeOffset Date, IReadOnlyList<EntityRef> Entities);
public sealed record EntityFact(EntityRef Entity, string MemoryId, DateTimeOffset Date, IReadOnlyList<EntityRef> CoOccurring);

// src/Scrinia.Core/Search/EntityFactStore.cs — all consumed, none modified
IReadOnlyList<EntityFact> Facts(EntityRef e);
int MentionCount(EntityRef e);
IReadOnlyList<string> MemoriesFor(params EntityRef[] entities);      // ids mentioning ALL; ordered by id ordinal
IReadOnlyList<EntityRef> Entities(string? key = null);              // ordered key then value ordinal
IReadOnlyList<EntityFact> Timeline(EntityRef e);                    // ordered Date then MemoryId
(DateTimeOffset First, DateTimeOffset Last)? Span(EntityRef e);
IReadOnlyList<(EntityRef Entity, int Count)> Neighbors(EntityRef e);// ordered Count desc then key/value

// src/Scrinia.Core/IMemoryStore.cs
EntityFactStore GetEntityFactStore();                               // cross-scope, FileMemoryStore caches
List<ScopedArtifact> ListScoped(string? scopes, string? excludeTopics);
string FormatRoundTripQualifiedName(string scope, string subject);
// src/Scrinia.Core/Search/EntityFactDerivation.cs
static MemoryEntities? EntityFactDerivation.Derive(ScopedArtifact item, string memoryId);

// src/Scrinia.Mcp/McpResponse.cs
ResponseBuilder.Success(string? content) / .Warning(string? content) / .Error(string, string code, params string[] recovery)
r.WithAction(string) ; r.ToYaml() ; ErrorCodes.InvalidParameter

// CLI (src/Scrinia/Commands/ScriniaCommands.cs) — int EmitMcpResult(string yaml, bool json, string action)
// WorkspaceSetup.Configure(workspaceRoot); await WorkspaceSetup.LoadPluginsAsync(ct); new ScriniaMcpTools()
```

---

## File map

- Create `src/Scrinia.Core/Search/FactsResult.cs` — result record types.
- Create `src/Scrinia.Core/Search/FactsQuery.cs` — pure query service (resolution + 3 ops).
- Create `src/Scrinia.Mcp/FactsResponseFormatter.cs` — result → YAML.
- Create `src/Scrinia.Mcp/MemoryTools.Facts.cs` — the `facts` MCP tool (partial `ScriniaMcpTools`).
- Create `src/Scrinia/Commands/ScriniaCommands.Facts.cs` — the `scri facts` CLI verb (partial `ScriniaCommands`).
- Create `tests/Scrinia.Tests/FactsQueryTests.cs`, `tests/Scrinia.Tests/FactsResponseFormatterTests.cs`, `tests/Scrinia.Tests/FactsToolTests.cs`.

---

## Task 1: Result types + `FactsQuery` resolution + `entities` op

**Files:**
- Create: `src/Scrinia.Core/Search/FactsResult.cs`
- Create: `src/Scrinia.Core/Search/FactsQuery.cs`
- Test: `tests/Scrinia.Tests/FactsQueryTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct EntityCount(EntityRef Entity, int Count)`
  - `public readonly record struct TimelineEntry(string MemoryId, DateTimeOffset Date)`
  - `public abstract record FactsResult;` with sealed subtypes (defined here + Task 2):
    `EntitiesListResult(IReadOnlyList<EntityCount> Items, int Total, bool Truncated)`,
    `DisambiguationResult(string Arg, IReadOnlyList<EntityCount> Candidates)`,
    `NotFoundResult(string Arg)`.
  - `public static class FactsQuery` with:
    `EntityResolution Resolve(EntityFactStore store, string arg)` where
    `EntityResolution` is `abstract record` + `Resolved(EntityRef Entity)`, `Ambiguous(string Arg, IReadOnlyList<EntityCount> Candidates)`, `NotFound(string Arg)`;
    `FactsResult EntitiesList(EntityFactStore store, string? key, int limit)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/FactsQueryTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class FactsQueryTests
{
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D3 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);

    // jolene: 3 memories (co-occurs france x2, seraphim x1); france: 2; seraphim: 1; also mention:jolene x1.
    private static EntityFactStore Store() => new(
    [
        new MemoryEntities("/f/trip", D1, [R("person", "jolene"), R("place", "france")]),
        new MemoryEntities("/s/mar",  D2, [R("person", "jolene"), R("place", "france"), R("thing", "seraphim")]),
        new MemoryEntities("/f/solo", D3, [R("person", "jolene")]),
        new MemoryEntities("/n/note", D2, [R("mention", "jolene")]),
    ]);

    [Fact]
    public void Resolve_KeyValue_Exact()
    {
        FactsQuery.Resolve(Store(), "person:jolene").Should().BeOfType<EntityResolution.Resolved>()
            .Which.Entity.Should().Be(R("person", "jolene"));
    }

    [Fact]
    public void Resolve_BareValue_Ambiguous_ListsCandidates()
    {
        var r = FactsQuery.Resolve(Store(), "jolene").Should().BeOfType<EntityResolution.Ambiguous>().Which;
        r.Candidates.Select(c => c.Entity).Should().BeEquivalentTo([R("person", "jolene"), R("mention", "jolene")]);
    }

    [Fact]
    public void Resolve_BareValue_Unique()
    {
        FactsQuery.Resolve(Store(), "seraphim").Should().BeOfType<EntityResolution.Resolved>()
            .Which.Entity.Should().Be(R("thing", "seraphim"));
    }

    [Fact]
    public void Resolve_NotFound()
    {
        FactsQuery.Resolve(Store(), "nobody").Should().BeOfType<EntityResolution.NotFound>();
        FactsQuery.Resolve(Store(), "person:nobody").Should().BeOfType<EntityResolution.NotFound>();
    }

    [Fact]
    public void EntitiesList_SortsByCountDesc_AndFiltersByKey()
    {
        var all = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: null, limit: 50);
        all.Items[0].Should().Be(new EntityCount(R("person", "jolene"), 3)); // highest count first
        all.Total.Should().Be(4);
        all.Truncated.Should().BeFalse();

        var people = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: "person", limit: 50);
        people.Items.Should().ContainSingle().Which.Entity.Should().Be(R("person", "jolene"));
    }

    [Fact]
    public void EntitiesList_Truncates_AtLimit()
    {
        var r = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: null, limit: 2);
        r.Items.Should().HaveCount(2);
        r.Total.Should().Be(4);
        r.Truncated.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsQueryTests"`
Expected: FAIL — `FactsQuery` / `EntityResolution` / result types do not exist.

- [ ] **Step 3: Create the result types**

Create `src/Scrinia.Core/Search/FactsResult.cs`:

```csharp
namespace Scrinia.Core.Search;

/// <summary>An entity paired with a count (mention count, or shared-memory count for co-occurrence).</summary>
public readonly record struct EntityCount(EntityRef Entity, int Count);

/// <summary>One memory on an entity's timeline / in an intersection: its id and effective date.</summary>
public readonly record struct TimelineEntry(string MemoryId, DateTimeOffset Date);

/// <summary>Result of resolving one entity argument against the store's vocabulary.</summary>
public abstract record EntityResolution
{
    public sealed record Resolved(EntityRef Entity) : EntityResolution;
    public sealed record Ambiguous(string Arg, IReadOnlyList<EntityCount> Candidates) : EntityResolution;
    public sealed record NotFound(string Arg) : EntityResolution;
}

/// <summary>Discriminated result of a facts query, rendered by FactsResponseFormatter.</summary>
public abstract record FactsResult;

public sealed record EntitiesListResult(IReadOnlyList<EntityCount> Items, int Total, bool Truncated) : FactsResult;

public sealed record ProfileResult(
    EntityRef Entity, int MentionCount, DateTimeOffset? First, DateTimeOffset? Last,
    IReadOnlyList<EntityCount> Neighbors, IReadOnlyList<TimelineEntry> Timeline, bool TimelineTruncated) : FactsResult;

public sealed record IntersectionResult(
    IReadOnlyList<EntityRef> Members, IReadOnlyList<TimelineEntry> Memories, int Count) : FactsResult;

/// <summary>A bare value matched more than one entity — the op is not performed.</summary>
public sealed record DisambiguationResult(string Arg, IReadOnlyList<EntityCount> Candidates) : FactsResult;

/// <summary>An entity argument matched nothing.</summary>
public sealed record NotFoundResult(string Arg) : FactsResult;
```

- [ ] **Step 4: Create `FactsQuery` with resolution + the `entities` op**

Create `src/Scrinia.Core/Search/FactsQuery.cs`:

```csharp
namespace Scrinia.Core.Search;

/// <summary>Pure projection of an <see cref="EntityFactStore"/> into <see cref="FactsResult"/>s for
/// the explicit `facts` tool. Reuses only the store's public query API; adds no store logic.
/// Owns entity-argument resolution (key:value exact, or bare value across keys). Deterministic.</summary>
public static class FactsQuery
{
    /// <summary>Resolves one entity argument. "key:value" → exact; bare "value" → all entities with
    /// that value (1 → Resolved, 0 → NotFound, &gt;1 → Ambiguous). Keys/values lowercased.</summary>
    public static EntityResolution Resolve(EntityFactStore store, string arg)
    {
        string trimmed = (arg ?? string.Empty).Trim();
        int colon = trimmed.IndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1)
        {
            var e = EntityRef.Of(trimmed[..colon], trimmed[(colon + 1)..]);
            return store.MentionCount(e) > 0 ? new EntityResolution.Resolved(e) : new EntityResolution.NotFound(trimmed);
        }

        string value = trimmed.ToLowerInvariant();
        var matches = store.Entities()
            .Where(e => e.Value == value)
            .Select(e => new EntityCount(e, store.MentionCount(e)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Entity.Key, StringComparer.Ordinal)
            .ToArray();

        return matches.Length switch
        {
            0 => new EntityResolution.NotFound(trimmed),
            1 => new EntityResolution.Resolved(matches[0].Entity),
            _ => new EntityResolution.Ambiguous(trimmed, matches),
        };
    }

    /// <summary>Lists the entity vocabulary (optionally one key), by mention count desc then
    /// key/value ordinal, capped at <paramref name="limit"/>.</summary>
    public static FactsResult EntitiesList(EntityFactStore store, string? key, int limit)
    {
        string? k = string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();
        var all = store.Entities(k)
            .Select(e => new EntityCount(e, store.MentionCount(e)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Entity.Key, StringComparer.Ordinal)
            .ThenBy(c => c.Entity.Value, StringComparer.Ordinal)
            .ToArray();

        int cap = limit < 0 ? 0 : limit;
        var items = all.Take(cap).ToArray();
        return new EntitiesListResult(items, all.Length, all.Length > items.Length);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsQueryTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Search/FactsResult.cs src/Scrinia.Core/Search/FactsQuery.cs tests/Scrinia.Tests/FactsQueryTests.cs
git commit -m "$(cat <<'EOF'
feat(facts): FactsQuery result types, entity resolution, entities op

Pure projection over EntityFactStore's public API: the FactsResult record
hierarchy, key:value/bare-value entity resolution (exact, unique, ambiguous,
not-found), and the `entities` vocabulary op (count-desc, key filter, limit).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `FactsQuery` `profile` + `intersect` ops

**Files:**
- Modify: `src/Scrinia.Core/Search/FactsQuery.cs`
- Test: `tests/Scrinia.Tests/FactsQueryTests.cs` (add cases)

**Interfaces:**
- Consumes: `EntityResolution`, `ProfileResult`, `IntersectionResult`, `DisambiguationResult`, `NotFoundResult`, `EntityCount`, `TimelineEntry` (Task 1).
- Produces:
  - `FactsResult FactsQuery.Profile(EntityFactStore store, string entityArg, int timelineLimit)`
  - `FactsResult FactsQuery.Intersect(EntityFactStore store, IReadOnlyList<string> entityArgs)`
  - `const int FactsQuery.ProfileNeighborLimit = 10`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Tests/FactsQueryTests.cs` (inside the class; reuses `Store()`/`R`/`D1..D3`):

```csharp
    [Fact]
    public void Profile_ReturnsCountSpanNeighborsAndTimeline()
    {
        var p = (ProfileResult)FactsQuery.Profile(Store(), "person:jolene", timelineLimit: 50);
        p.Entity.Should().Be(R("person", "jolene"));
        p.MentionCount.Should().Be(3);
        p.First.Should().Be(D1);
        p.Last.Should().Be(D3);
        p.Neighbors.Should().Contain(new EntityCount(R("place", "france"), 2));
        // timeline chronological by date then id
        p.Timeline.Select(t => t.MemoryId).Should().Equal("/f/trip", "/s/mar", "/f/solo");
        p.TimelineTruncated.Should().BeFalse();
    }

    [Fact]
    public void Profile_TimelineTruncates_AtLimit()
    {
        var p = (ProfileResult)FactsQuery.Profile(Store(), "person:jolene", timelineLimit: 2);
        p.Timeline.Should().HaveCount(2);
        p.TimelineTruncated.Should().BeTrue();
    }

    [Fact]
    public void Profile_BareAmbiguous_ReturnsDisambiguation()
    {
        FactsQuery.Profile(Store(), "jolene", 50).Should().BeOfType<DisambiguationResult>()
            .Which.Candidates.Select(c => c.Entity).Should().Contain(R("mention", "jolene"));
    }

    [Fact]
    public void Profile_NotFound()
    {
        FactsQuery.Profile(Store(), "person:nobody", 50).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Intersect_ReturnsMemoriesMentioningAll_WithDates()
    {
        var r = (IntersectionResult)FactsQuery.Intersect(Store(), ["person:jolene", "place:france"]);
        r.Count.Should().Be(2);
        r.Memories.Select(m => m.MemoryId).Should().Equal("/f/trip", "/s/mar"); // date asc then id
        r.Memories[0].Date.Should().Be(D1);
    }

    [Fact]
    public void Intersect_EmptyResult_WhenNoSharedMemory()
    {
        var r = (IntersectionResult)FactsQuery.Intersect(Store(), ["thing:seraphim", "person:jolene"]);
        r.Count.Should().Be(1); // only /s/mar has both
        var none = (IntersectionResult)FactsQuery.Intersect(Store(), ["thing:seraphim", "place:france"]);
        none.Count.Should().Be(1); // /s/mar again
    }

    [Fact]
    public void Intersect_AmbiguousMember_ReturnsDisambiguation()
    {
        FactsQuery.Intersect(Store(), ["jolene", "place:france"]).Should().BeOfType<DisambiguationResult>();
    }

    [Fact]
    public void Intersect_NotFoundMember_ReturnsNotFound()
    {
        FactsQuery.Intersect(Store(), ["person:nobody", "place:france"]).Should().BeOfType<NotFoundResult>();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsQueryTests"`
Expected: FAIL — `FactsQuery.Profile` / `FactsQuery.Intersect` do not exist.

- [ ] **Step 3: Add the two ops to `FactsQuery`**

Append inside the `FactsQuery` class in `src/Scrinia.Core/Search/FactsQuery.cs`:

```csharp
    public const int ProfileNeighborLimit = 10;

    /// <summary>Deep-dive on one entity: count, span, top co-occurrences, and the chronological
    /// timeline (capped at <paramref name="timelineLimit"/>). Ambiguous/not-found bare args surface
    /// as Disambiguation/NotFound results.</summary>
    public static FactsResult Profile(EntityFactStore store, string entityArg, int timelineLimit)
    {
        var resolution = Resolve(store, entityArg);
        if (resolution is not EntityResolution.Resolved resolved)
            return FromFailedResolution(resolution);

        var e = resolved.Entity;
        var span = store.Span(e);
        var neighbors = store.Neighbors(e)
            .Take(ProfileNeighborLimit)
            .Select(n => new EntityCount(n.Entity, n.Count))
            .ToArray();

        var full = store.Timeline(e);   // already Date asc then MemoryId ordinal
        int cap = timelineLimit < 0 ? 0 : timelineLimit;
        var timeline = full.Take(cap).Select(f => new TimelineEntry(f.MemoryId, f.Date)).ToArray();

        return new ProfileResult(e, store.MentionCount(e), span?.First, span?.Last,
            neighbors, timeline, full.Count > timeline.Length);
    }

    /// <summary>Memories mentioning ALL given entities. Any ambiguous/not-found member short-circuits
    /// to that member's Disambiguation/NotFound result (op not performed).</summary>
    public static FactsResult Intersect(EntityFactStore store, IReadOnlyList<string> entityArgs)
    {
        var refs = new List<EntityRef>(entityArgs.Count);
        foreach (var arg in entityArgs)
        {
            var resolution = Resolve(store, arg);
            if (resolution is not EntityResolution.Resolved r)
                return FromFailedResolution(resolution);
            refs.Add(r.Entity);
        }

        var ids = store.MemoriesFor(refs.ToArray());
        // Dates: every intersecting memory appears in each member's facts, so the first member's
        // facts carry the dates. No new store method needed.
        var idSet = new HashSet<string>(ids, StringComparer.Ordinal);
        var dateById = store.Facts(refs[0])
            .Where(f => idSet.Contains(f.MemoryId))
            .GroupBy(f => f.MemoryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Date, StringComparer.Ordinal);

        var memories = ids
            .Select(id => new TimelineEntry(id, dateById.TryGetValue(id, out var d) ? d : default))
            .OrderBy(m => m.Date)
            .ThenBy(m => m.MemoryId, StringComparer.Ordinal)
            .ToArray();

        return new IntersectionResult(refs, memories, memories.Length);
    }

    /// <summary>Maps an already-computed non-Resolved resolution to the matching FactsResult.</summary>
    private static FactsResult FromFailedResolution(EntityResolution resolution) =>
        resolution switch
        {
            EntityResolution.Ambiguous a => new DisambiguationResult(a.Arg, a.Candidates),
            EntityResolution.NotFound n => new NotFoundResult(n.Arg),
            _ => throw new InvalidOperationException("FromFailedResolution called with a Resolved resolution."),
        };
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsQueryTests"`
Expected: PASS (all FactsQueryTests, ~14).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/FactsQuery.cs tests/Scrinia.Tests/FactsQueryTests.cs
git commit -m "$(cat <<'EOF'
feat(facts): FactsQuery profile and intersect ops

profile: count + span + top-10 co-occurrences + chronological timeline
(capped). intersect: memories mentioning all members, dates joined from the
first member's facts, empty set is a valid answer. Ambiguous/not-found args
surface as Disambiguation/NotFound results. Reuses store API only.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `FactsResponseFormatter`

**Files:**
- Create: `src/Scrinia.Mcp/FactsResponseFormatter.cs`
- Test: `tests/Scrinia.Tests/FactsResponseFormatterTests.cs`

**Interfaces:**
- Consumes: all `FactsResult` subtypes + `EntityCount`/`TimelineEntry` (Tasks 1-2); `ResponseBuilder`, `McpResponseExtensions.ToYaml` (existing).
- Produces: `static string FactsResponseFormatter.ToYaml(FactsResult result)` — the full YAML `McpResponse` string, `action: facts`. Success for data results; warning for `DisambiguationResult`/`NotFoundResult`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/FactsResponseFormatterTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class FactsResponseFormatterTests
{
    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entities_RendersCountsAndTruncation()
    {
        var yaml = FactsResponseFormatter.ToYaml(new EntitiesListResult(
            [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("place", "france"), 5)], total: 9, truncated: true));
        yaml.Should().Contain("status: success").And.Contain("action: facts");
        yaml.Should().Contain("person:jolene").And.Contain("(7)");
        yaml.Should().Contain("showing 2 of 9");
    }

    [Fact]
    public void Profile_RendersSpanNeighborsTimeline()
    {
        var yaml = FactsResponseFormatter.ToYaml(new ProfileResult(
            R("person", "jolene"), 3, D1, D2,
            [new EntityCount(R("place", "france"), 2)],
            [new TimelineEntry("/f/trip", D1), new TimelineEntry("/f/solo", D2)], timelineTruncated: false));
        yaml.Should().Contain("profile: person:jolene");
        yaml.Should().Contain("mentions: 3");
        yaml.Should().Contain("2026-01-04 → 2026-07-20");
        yaml.Should().Contain("place:france (2)");
        yaml.Should().Contain("2026-01-04").And.Contain("/f/trip");
    }

    [Fact]
    public void Intersection_RendersMembersAndMemories()
    {
        var yaml = FactsResponseFormatter.ToYaml(new IntersectionResult(
            [R("person", "jolene"), R("place", "france")],
            [new TimelineEntry("/f/trip", D1)], count: 1));
        yaml.Should().Contain("status: success");
        yaml.Should().Contain("person:jolene").And.Contain("place:france");
        yaml.Should().Contain("memories: 1");
        yaml.Should().Contain("/f/trip");
    }

    [Fact]
    public void EmptyIntersection_IsSuccess_WithZero()
    {
        var yaml = FactsResponseFormatter.ToYaml(new IntersectionResult(
            [R("person", "jolene"), R("thing", "seraphim")], [], count: 0));
        yaml.Should().Contain("status: success");
        yaml.Should().Contain("memories: 0");
    }

    [Fact]
    public void Disambiguation_IsWarning_WithCandidates()
    {
        var yaml = FactsResponseFormatter.ToYaml(new DisambiguationResult(
            "jolene", [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("mention", "jolene"), 2)]));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("person:jolene (7)").And.Contain("mention:jolene (2)");
    }

    [Fact]
    public void NotFound_IsWarning()
    {
        var yaml = FactsResponseFormatter.ToYaml(new NotFoundResult("zzz"));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("zzz");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsResponseFormatterTests"`
Expected: FAIL — `FactsResponseFormatter` does not exist.

- [ ] **Step 3: Create the formatter**

Create `src/Scrinia.Mcp/FactsResponseFormatter.cs`:

```csharp
using System.Globalization;
using System.Text;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

/// <summary>Renders a <see cref="FactsResult"/> into the standard McpResponse YAML (action: facts).
/// Data results are `success`; disambiguation / not-found are `warning` with actionable guidance.</summary>
public static class FactsResponseFormatter
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string Date(DateTimeOffset d) => d.ToString("yyyy-MM-dd", Ci);
    private static string Key(EntityRef e) => $"{e.Key}:{e.Value}";

    public static string ToYaml(FactsResult result) => result switch
    {
        EntitiesListResult r => Success(Entities(r)),
        ProfileResult r => Success(Profile(r)),
        IntersectionResult r => Success(Intersection(r)),
        DisambiguationResult r => Warning(Disambiguation(r)),
        NotFoundResult r => Warning($"not found: no entity named \"{r.Arg}\""),
        _ => Success("no result"),
    };

    private static string Success(string content) =>
        ResponseBuilder.Success(content).WithAction("facts").ToYaml();
    private static string Warning(string content) =>
        ResponseBuilder.Warning(content).WithAction("facts").ToYaml();

    private static string Entities(EntitiesListResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"entities: showing {r.Items.Count} of {r.Total}\n");
        foreach (var i in r.Items)
            sb.Append(Ci, $"  {Key(i.Entity)} ({i.Count})\n");
        return sb.ToString().TrimEnd();
    }

    private static string Profile(ProfileResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"profile: {Key(r.Entity)}\n");
        sb.Append(Ci, $"  mentions: {r.MentionCount}\n");
        if (r.First is { } f && r.Last is { } l)
            sb.Append(Ci, $"  span: {Date(f)} → {Date(l)}\n");
        if (r.Neighbors.Count > 0)
            sb.Append(Ci, $"  co-occurring: {string.Join(", ", r.Neighbors.Select(n => $"{Key(n.Entity)} ({n.Count})"))}\n");
        sb.Append(Ci, $"  timeline ({r.Timeline.Count}{(r.TimelineTruncated ? "+" : "")}):\n");
        foreach (var t in r.Timeline)
            sb.Append(Ci, $"    {Date(t.Date)}  {t.MemoryId}\n");
        return sb.ToString().TrimEnd();
    }

    private static string Intersection(IntersectionResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"intersect: {string.Join(" AND ", r.Members.Select(Key))}\n");
        sb.Append(Ci, $"  memories: {r.Count}\n");
        foreach (var m in r.Memories)
            sb.Append(Ci, $"    {Date(m.Date)}  {m.MemoryId}\n");
        return sb.ToString().TrimEnd();
    }

    private static string Disambiguation(DisambiguationResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"ambiguous: \"{r.Arg}\" matches {r.Candidates.Count} entities — specify key:value\n");
        foreach (var c in r.Candidates)
            sb.Append(Ci, $"  {Key(c.Entity)} ({c.Count})\n");
        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsResponseFormatterTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/FactsResponseFormatter.cs tests/Scrinia.Tests/FactsResponseFormatterTests.cs
git commit -m "$(cat <<'EOF'
feat(facts): FactsResponseFormatter renders results to McpResponse YAML

entities/profile/intersect render as success; disambiguation and not-found
render as warning with actionable candidate lists. Dates yyyy-MM-dd,
action: facts.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: MCP `facts` tool (store selection + dispatch + validation)

**Files:**
- Create: `src/Scrinia.Mcp/MemoryTools.Facts.cs`
- Test: `tests/Scrinia.Tests/FactsToolTests.cs`

**Interfaces:**
- Consumes: `FactsQuery` (Tasks 1-2), `FactsResponseFormatter` (Task 3); `CurrentStore`, `ResponseBuilder`, `ErrorCodes` (existing); `EntityFactDerivation.Derive`, `IMemoryStore.GetEntityFactStore/ListScoped/FormatRoundTripQualifiedName`.
- Produces (partial `ScriniaMcpTools`):
  `Task<string> Facts(string op, string? entity = null, string[]? entities = null, string? key = null, string? scopes = null, string? excludeTopics = null, int limit = 50, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/FactsToolTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class FactsToolTests
{
    private static ArtifactEntry Note(string name, params (string Key, string[] Vals)[] attrs)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in attrs) d[k] = v;
        return new ArtifactEntry(name, "uri", 1, 1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "prose", Attributes: d);
    }

    private static void Seed(IMemoryStore store)
    {
        store.Upsert(Note("trip", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("solo", ("person", ["jolene"])), "local:findings");
        store.Upsert(Note("plan1", ("person", ["jolene"])), "local:plan");
    }

    [Fact]
    public async Task Entities_ListsVocabulary()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("entities");
        yaml.Should().Contain("status: success").And.Contain("person:jolene");
    }

    [Fact]
    public async Task Profile_ReturnsDeepDive()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("profile", entity: "person:jolene");
        yaml.Should().Contain("profile: person:jolene").And.Contain("mentions: 3"); // trip+solo+plan1 cross-scope
    }

    [Fact]
    public async Task Intersect_ReturnsSharedMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("intersect", entities: ["person:jolene", "place:france"]);
        yaml.Should().Contain("memories: 1").And.Contain("trip");
    }

    [Fact]
    public async Task Scopes_Narrow_ExcludeTopic_DropsPlanScope()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("profile", entity: "person:jolene", excludeTopics: "plan");
        yaml.Should().Contain("mentions: 2"); // plan1 excluded
    }

    [Fact]
    public async Task UnknownOp_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("bogus");
        yaml.Should().Contain("status: error").And.Contain("INVALID_PARAMETER");
    }

    [Fact]
    public async Task Profile_MissingEntity_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("profile");
        yaml.Should().Contain("status: error");
    }

    [Fact]
    public async Task Intersect_FewerThanTwo_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("intersect", entities: ["person:jolene"]);
        yaml.Should().Contain("status: error");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsToolTests"`
Expected: FAIL — `ScriniaMcpTools.Facts` does not exist.

- [ ] **Step 3: Create the tool**

Create `src/Scrinia.Mcp/MemoryTools.Facts.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    [McpServerTool(Name = "facts"), Description(
        "Query the entity fact store directly (aggregate/relational questions search can't express). " +
        "op 'entities' lists the known entity vocabulary (optional key filter); 'profile' returns one " +
        "entity's count, date span, co-occurrences, and per-memory timeline; 'intersect' returns memories " +
        "mentioning ALL given entities. Entities are 'key:value' (e.g. person:jolene) or a bare value " +
        "(resolved across keys; ambiguous values return a disambiguation list). Cross-scope by default; " +
        "scopes/excludeTopics narrow it like search. " +
        "Example: facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] }).")]
    public Task<string> Facts(
        [Description("Operation: 'entities', 'profile', or 'intersect'.")] string op,
        [Description("profile: the target entity — 'key:value' or a bare value.")] string? entity = null,
        [Description("intersect: the entities (>=2) — each 'key:value' or a bare value.")] string[]? entities = null,
        [Description("entities: optional key filter, e.g. 'person'.")] string? key = null,
        [Description("Optional comma-separated scope order to narrow to (same semantics as search).")] string? scopes = null,
        [Description("Optional comma-separated topic names to exclude (same semantics as search).")] string? excludeTopics = null,
        [Description("Max rows for the entities list / profile timeline (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var facts = SelectFactStore(CurrentStore, scopes, excludeTopics);

        FactsResult result;
        switch ((op ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "entities":
                result = FactsQuery.EntitiesList(facts, key, limit);
                break;
            case "profile":
                if (string.IsNullOrWhiteSpace(entity))
                    return Task.FromResult(ResponseBuilder.Error(
                        "facts profile requires 'entity'.", ErrorCodes.InvalidParameter,
                        "facts({ op: 'profile', entity: 'person:jolene' })").ToYaml());
                result = FactsQuery.Profile(facts, entity, limit);
                break;
            case "intersect":
                if (entities is null || entities.Length < 2)
                    return Task.FromResult(ResponseBuilder.Error(
                        "facts intersect requires 'entities' with at least 2 members.", ErrorCodes.InvalidParameter,
                        "facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })").ToYaml());
                result = FactsQuery.Intersect(facts, entities);
                break;
            default:
                return Task.FromResult(ResponseBuilder.Error(
                    $"unknown facts op '{op}'. Use 'entities', 'profile', or 'intersect'.",
                    ErrorCodes.InvalidParameter,
                    "facts({ op: 'entities' })").ToYaml());
        }

        return Task.FromResult(FactsResponseFormatter.ToYaml(result));
    }

    /// <summary>Cross-scope cached store when unfiltered; a scoped store built from the
    /// scope+excludeTopics-filtered candidates otherwise (same derivation as the auto-surface path).</summary>
    private static EntityFactStore SelectFactStore(IMemoryStore store, string? scopes, string? excludeTopics)
    {
        if (string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics))
            return store.GetEntityFactStore();

        var memories = store.ListScoped(scopes, excludeTopics)
            .Select(sa => EntityFactDerivation.Derive(sa, store.FormatRoundTripQualifiedName(sa.Scope, sa.Entry.Name)))
            .Where(m => m is not null)
            .Select(m => m!);
        return new EntityFactStore(memories);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsToolTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Facts.cs tests/Scrinia.Tests/FactsToolTests.cs
git commit -m "$(cat <<'EOF'
feat(facts): the facts MCP tool — store selection, op dispatch, validation

facts({ op, entity, entities, key, scopes, excludeTopics, limit }) selects
the cross-scope cached store by default (scopes/excludeTopics build a scoped
store), dispatches to FactsQuery, and formats. Unknown op / missing entity /
<2 intersect members return INVALID_PARAMETER with corrective examples.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `scri facts` CLI mirror

**Files:**
- Create: `src/Scrinia/Commands/ScriniaCommands.Facts.cs`
- Test: `tests/Scrinia.Tests/FactsToolTests.cs` (add a CLI case)

**Interfaces:**
- Consumes: `ScriniaMcpTools.Facts` (Task 4); `WorkspaceSetup.Configure/LoadPluginsAsync`, `EmitMcpResult` (existing).
- Produces (partial `ScriniaCommands`):
  `Task<int> Facts(string op, string? target = null, string? key = null, string? scopes = null, string? excludeTopics = null, int limit = 50, string? workspaceRoot = null, bool json = false, CancellationToken cancellationToken = default)`.
  CLI collapses entity args into `target`: for `intersect`, `target` is comma-separated (`person:jolene,place:france`); for `profile`, `target` is the single entity; for `entities`, `target` is ignored.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/FactsToolTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Cli_Facts_Entities_PrintsYaml()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var prevOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            int code = await new Scrinia.Commands.ScriniaCommands()
                .Facts("entities", workspaceRoot: scope.WorkspaceDir);
            code.Should().Be(0);
        }
        finally { Console.SetOut(prevOut); }

        sw.ToString().Should().Contain("action: facts").And.Contain("person:jolene");
    }

    [Fact]
    public async Task Cli_Facts_Intersect_SplitsCommaSeparatedTarget()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var prevOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            int code = await new Scrinia.Commands.ScriniaCommands()
                .Facts("intersect", target: "person:jolene,place:france", workspaceRoot: scope.WorkspaceDir);
            code.Should().Be(0);
        }
        finally { Console.SetOut(prevOut); }

        sw.ToString().Should().Contain("memories: 1").And.Contain("trip");
    }
```

Note: `TestHelpers.StoreScope` exposes `WorkspaceDir`; passing it as `workspaceRoot` makes `WorkspaceSetup.Configure` target the same store the test seeded. (`StoreScope` also sets `MemoryStoreContext.Current`, but the CLI reconfigures from `workspaceRoot`, so they must point at the same dir — they do.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsToolTests.Cli"`
Expected: FAIL — `ScriniaCommands.Facts` does not exist.

- [ ] **Step 3: Create the CLI verb**

Create `src/Scrinia/Commands/ScriniaCommands.Facts.cs`:

```csharp
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Query the entity fact store: entities | profile | intersect.</summary>
    /// <param name="op">Operation: 'entities', 'profile', or 'intersect'.</param>
    /// <param name="target">profile: the entity ('key:value' or bare value). intersect: comma-separated entities (e.g. 'person:jolene,place:france'). Ignored for 'entities'.</param>
    /// <param name="key">entities: optional key filter, e.g. 'person'.</param>
    /// <param name="scopes">Comma-separated scopes to narrow to (same semantics as search).</param>
    /// <param name="excludeTopics">Comma-separated topic names to exclude (same semantics as search).</param>
    /// <param name="limit">Max rows for the entities list / profile timeline.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of raw YAML.</param>
    public async Task<int> Facts(
        [Argument] string op,
        [Argument] string? target = null,
        string? key = null,
        string? scopes = null,
        string? excludeTopics = null,
        int limit = 50,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        string normalizedOp = (op ?? string.Empty).Trim().ToLowerInvariant();
        string? entity = normalizedOp == "profile" ? target : null;
        string[]? entities = normalizedOp == "intersect"
            ? (target ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        string yaml = await new ScriniaMcpTools().Facts(
            op, entity, entities, key, scopes, excludeTopics, limit, cancellationToken);
        return EmitMcpResult(yaml, json, "facts");
    }
}
```

- [ ] **Step 4: Run the CLI tests, then the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~FactsToolTests"`
Expected: PASS (all FactsToolTests including the 2 CLI cases).

Then the full regression gate:
Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS — full suite green (baseline was 1106 passed / 0 failed / 4 skipped before this piece; expect +~30 new tests, 0 failures). Report the counts.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.Facts.cs tests/Scrinia.Tests/FactsToolTests.cs
git commit -m "$(cat <<'EOF'
feat(facts): scri facts CLI mirror

scri facts <op> [target] calls the same ScriniaMcpTools.Facts handler and
prints the YAML (--json wraps it). intersect takes comma-separated entities;
profile takes the single entity; entities ignores target. One pipeline, two
surfaces — the established MCP-passthrough pattern.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Self-review notes (traceability to spec)

- **One `facts` tool, ops entities/profile/intersect** → Tasks 1-2 (FactsQuery), Task 4 (tool), Task 5 (CLI).
- **profile = deep-dive + timeline** → Task 2 `Profile` (count/span/top-10 neighbors/timeline).
- **cross-scope default; scopes/excludeTopics narrow** → Task 4 `SelectFactStore` (cached vs scoped build); `Scopes_Narrow_ExcludeTopic_DropsPlanScope` guard.
- **uniform key:value / bare-value addressing + disambiguation** → Task 1 `Resolve`; Task 2 ops surface `DisambiguationResult`/`NotFoundResult`; Task 3 renders them as `warning`.
- **intersect date-join from `Facts(refs[0])`, no new store method** → Task 2 `Intersect`.
- **one pipeline, two surfaces** → Task 5 CLI calls the Task 4 handler; `EmitMcpResult` for `--json`.
- **no new store logic / no ranking or search change** → nothing in `EntityFactStore`/`search`/`EntityGraph` is touched; verified by the full-suite gate in Task 5.
- **Deferred (graph unification)** → not implemented; Non-goal in the spec.
```
