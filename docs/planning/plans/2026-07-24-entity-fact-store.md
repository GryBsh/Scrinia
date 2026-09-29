# EntityFactStore Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a persisted, per-scope, aggregatable `EntityFactStore` — a materialized view of the entities already implied by each memory's attributes/keywords + effective date — with a seven-method aggregate query API, additive to `EntityGraph`.

**Architecture:** A pure, in-memory `EntityFactStore` built from compact per-memory records (`MemoryEntities`); entities are derived via the *same* `EntityGraph.ParseEntityKeywords ∪ ParseEntityAttributes` union the graph uses. Persistence is a per-scope JSON sidecar (`facts.json`) that is a *rebuildable cache of derived data* — a validity token (entry count + hash of `(id, effective-date)`) triggers rebuild-on-mismatch, so it is never a competing source of truth. `FileMemoryStore` caches a merged cross-scope view, invalidated on mutation.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, System.Text.Json source-generated context.

## Global Constraints

- Namespace `Scrinia.Core.Search`; project `Scrinia.Core` (no ASP.NET/native deps).
- Build in-scope projects individually or pass `-p:NuGetAudit=false` (pre-existing AppHost audit blocker); do NOT rely on `dotnet build Scrinia.sln`.
- LLM-free, deterministic, stable ordering everywhere.
- Additive only: do NOT modify `EntityGraph`, the scorer, or any consumer. No new extraction from prose.
- `EntityRef` normalization: `Key` and `Value` lowercased (invariant). The parse methods already lower-case; normalize defensively in the `EntityRef` factory.
- Test project: `tests/Scrinia.Tests` (has `InternalsVisibleTo` for `Scrinia.Core`).
- Commit after every task with the repo's trailer:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

---

### Task 1: Data model — `EntityRef`, `MemoryEntities`, `EntityFact`

**Files:**
- Create: `src/Scrinia.Core/Search/EntityFact.cs`
- Test: `tests/Scrinia.Tests/EntityFactTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct EntityRef(string Key, string Value)` + `static EntityRef Of(string key, string value)` (lowercases both).
  - `sealed record MemoryEntities(string MemoryId, DateTimeOffset Date, IReadOnlyList<EntityRef> Entities)` — compact per-memory input/persist unit.
  - `sealed record EntityFact(EntityRef Entity, string MemoryId, DateTimeOffset Date, IReadOnlyList<EntityRef> CoOccurring)` — query-result unit.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactTests
{
    [Fact]
    public void EntityRef_Of_LowercasesKeyAndValue()
    {
        var r = EntityRef.Of("Person", "Maria");
        r.Key.Should().Be("person");
        r.Value.Should().Be("maria");
    }

    [Fact]
    public void EntityRef_ValueEquality_IsOrdinalOnNormalizedFields()
    {
        EntityRef.Of("thing", "Seraphim").Should().Be(EntityRef.Of("THING", "seraphim"));
        EntityRef.Of("thing", "seraphim").Should().NotBe(EntityRef.Of("thing", "susie"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactTests"`
Expected: FAIL — `EntityRef`/`EntityFact.cs` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core.Search;

/// <summary>A single entity: an attribute (key, value) pair. Key and Value are lower-cased and
/// kept whole (values are not tokenized) — identical to <see cref="EntityGraph"/> node identity,
/// so the fact store and the graph never disagree on entity identity.</summary>
public readonly record struct EntityRef(string Key, string Value)
{
    public static EntityRef Of(string key, string value) =>
        new(key.ToLowerInvariant(), value.ToLowerInvariant());
}

/// <summary>Compact per-memory unit: the memory's effective date and its distinct entity set.
/// This is what is derived from an entry and what the sidecar persists (O(entities) per memory).</summary>
public sealed record MemoryEntities(
    string MemoryId,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> Entities);

/// <summary>A fact returned by <see cref="EntityFactStore"/> queries: one entity as seen in one
/// memory, with that memory's date and the other entities co-occurring in it.</summary>
public sealed record EntityFact(
    EntityRef Entity,
    string MemoryId,
    DateTimeOffset Date,
    IReadOnlyList<EntityRef> CoOccurring);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFact.cs tests/Scrinia.Tests/EntityFactTests.cs
git commit -m "feat(facts): EntityRef/MemoryEntities/EntityFact data model"
```

---

### Task 2: Derive a memory's entities from an entry

**Files:**
- Create: `src/Scrinia.Core/Search/EntityFactDerivation.cs`
- Test: `tests/Scrinia.Tests/EntityFactDerivationTests.cs`

**Interfaces:**
- Consumes: `EntityRef`, `MemoryEntities` (Task 1); `EntityGraph.ParseEntityKeywords`, `EntityGraph.ParseEntityAttributes`; `ScopedArtifact(string Scope, ArtifactEntry Entry)`; `ArtifactEntry.{Keywords, Attributes, CreatedAt, UpdatedAt, Name}`.
- Produces: `static MemoryEntities? EntityFactDerivation.Derive(ScopedArtifact item, string memoryId)` — null when the memory has no entities. Date = `Entry.UpdatedAt ?? Entry.CreatedAt`. Entities = distinct union of the two parse sources.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactDerivationTests
{
    private static ScopedArtifact Entry(
        string name, string scope, DateTimeOffset created, DateTimeOffset? updated = null,
        string[]? keywords = null, Dictionary<string, string[]>? attributes = null) =>
        new(scope, new ArtifactEntry(
            Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: created, Keywords: keywords, UpdatedAt: updated, Attributes: attributes));

    [Fact]
    public void Derive_UnionsKeywordAndAttributeEntities_DatesByUpdatedThenCreated()
    {
        var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        var updated = DateTimeOffset.Parse("2023-06-01T00:00:00Z");
        var item = Entry("m1", "local", created, updated,
            keywords: ["person:jolene", "plain-term"],
            attributes: new() { ["place"] = ["france"], ["thing"] = ["seraphim"] });

        var me = EntityFactDerivation.Derive(item, "m1");

        me.Should().NotBeNull();
        me!.Date.Should().Be(updated); // UpdatedAt wins
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
        EntityFactDerivation.Derive(item, "m2").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactDerivationTests"`
Expected: FAIL — `EntityFactDerivation` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Derives a memory's compact entity set from its entry, using the identical
/// <see cref="EntityGraph.ParseEntityKeywords"/> ∪ <see cref="EntityGraph.ParseEntityAttributes"/>
/// union the graph consumes. Fact date is the entry's effective last-authored date
/// (<c>UpdatedAt ?? CreatedAt</c>). Null when the memory carries no entities.</summary>
public static class EntityFactDerivation
{
    public static MemoryEntities? Derive(ScopedArtifact item, string memoryId)
    {
        var e = item.Entry;
        var entities = EntityGraph.ParseEntityKeywords(e.Keywords)
            .Concat(EntityGraph.ParseEntityAttributes(e.Attributes))
            .Select(t => EntityRef.Of(t.Type, t.Value))
            .Distinct()
            .ToArray();

        if (entities.Length == 0) return null;
        return new MemoryEntities(memoryId, e.UpdatedAt ?? e.CreatedAt, entities);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactDerivationTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactDerivation.cs tests/Scrinia.Tests/EntityFactDerivationTests.cs
git commit -m "feat(facts): derive a memory's entity set (graph-identical union)"
```

---

### Task 3: `EntityFactStore` — membership API (`Facts`, `MentionCount`, `MemoriesFor`, `Entities`)

**Files:**
- Create: `src/Scrinia.Core/Search/EntityFactStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactStoreTests.cs`

**Interfaces:**
- Consumes: `EntityRef`, `MemoryEntities`, `EntityFact` (Task 1).
- Produces:
  - `EntityFactStore(IEnumerable<MemoryEntities> memories)` — builds indices.
  - `IReadOnlyList<EntityFact> Facts(EntityRef entity)`
  - `int MentionCount(EntityRef entity)`
  - `IReadOnlyList<string> MemoriesFor(params EntityRef[] entities)` — 1 → mentions; N → intersection; 0 → empty.
  - `IReadOnlyList<EntityRef> Entities(string? key = null)`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactStoreTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    private static readonly DateTimeOffset D2 = DateTimeOffset.Parse("2023-02-01T00:00:00Z");

    private static MemoryEntities Mem(string id, DateTimeOffset date, params (string k, string v)[] ents) =>
        new(id, date, ents.Select(e => EntityRef.Of(e.k, e.v)).ToArray());

    private static EntityFactStore Store(params MemoryEntities[] mems) => new(mems);

    [Fact]
    public void Facts_ReturnsOneFactPerMentioningMemory_WithCoOccurring()
    {
        var store = Store(
            Mem("m1", D1, ("person", "jolene"), ("place", "france")),
            Mem("m2", D2, ("person", "jolene"), ("thing", "seraphim")));

        var facts = store.Facts(EntityRef.Of("person", "jolene"));

        facts.Select(f => f.MemoryId).Should().BeEquivalentTo(["m1", "m2"]);
        facts.Single(f => f.MemoryId == "m1").CoOccurring.Should().ContainSingle()
            .Which.Should().Be(EntityRef.Of("place", "france"));
    }

    [Fact]
    public void MentionCount_CountsMentioningMemories()
    {
        var store = Store(
            Mem("m1", D1, ("person", "jolene")),
            Mem("m2", D2, ("person", "jolene")),
            Mem("m3", D2, ("person", "nate")));
        store.MentionCount(EntityRef.Of("person", "jolene")).Should().Be(2);
        store.MentionCount(EntityRef.Of("person", "unknown")).Should().Be(0);
    }

    [Fact]
    public void MemoriesFor_SingleEntity_ReturnsMentions_ManyEntities_ReturnsIntersection_ZeroArgs_ReturnsEmpty()
    {
        var store = Store(
            Mem("m1", D1, ("topic", "auth"), ("topic", "billing")),
            Mem("m2", D1, ("topic", "auth")),
            Mem("m3", D1, ("topic", "billing")));

        store.MemoriesFor(EntityRef.Of("topic", "auth")).Should().BeEquivalentTo(["m1", "m2"]);
        store.MemoriesFor(EntityRef.Of("topic", "auth"), EntityRef.Of("topic", "billing"))
            .Should().BeEquivalentTo(["m1"]);          // touching ALL
        store.MemoriesFor().Should().BeEmpty();        // zero args = empty, not "everything"
    }

    [Fact]
    public void Entities_DiscoversAllOrByKey_Deterministically()
    {
        var store = Store(Mem("m1", D1, ("person", "nate"), ("person", "jolene"), ("place", "france")));

        store.Entities().Should().Equal(
            EntityRef.Of("person", "jolene"),
            EntityRef.Of("person", "nate"),
            EntityRef.Of("place", "france"));      // key asc, then value asc
        store.Entities("person").Should().Equal(
            EntityRef.Of("person", "jolene"),
            EntityRef.Of("person", "nate"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreTests"`
Expected: FAIL — `EntityFactStore` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// A materialized, aggregatable view over memories' entities. Built from compact
/// <see cref="MemoryEntities"/> records (derived from entries, or loaded from a persisted sidecar).
/// Pure and I/O-free. Every attribute (key, value) pair is an entity, uniformly; predicate = key.
/// </summary>
public sealed class EntityFactStore
{
    // entity -> one row per mentioning memory (memoryId, date, that memory's full entity set)
    private readonly Dictionary<EntityRef, List<MemoryEntities>> _byEntity = new();

    public EntityFactStore(IEnumerable<MemoryEntities> memories)
    {
        foreach (var mem in memories)
            foreach (var entity in mem.Entities.Distinct())
                (_byEntity.TryGetValue(entity, out var list) ? list : _byEntity[entity] = [])
                    .Add(mem);
    }

    public IReadOnlyList<EntityFact> Facts(EntityRef entity)
    {
        if (!_byEntity.TryGetValue(entity, out var mems)) return [];
        return mems
            .Select(m => new EntityFact(
                entity, m.MemoryId, m.Date,
                m.Entities.Where(e => e != entity).ToArray()))
            .ToArray();
    }

    public int MentionCount(EntityRef entity) =>
        _byEntity.TryGetValue(entity, out var mems) ? mems.Count : 0;

    public IReadOnlyList<string> MemoriesFor(params EntityRef[] entities)
    {
        if (entities.Length == 0) return [];
        IEnumerable<string>? acc = null;
        foreach (var e in entities)
        {
            var ids = _byEntity.TryGetValue(e, out var mems)
                ? mems.Select(m => m.MemoryId)
                : [];
            acc = acc is null ? ids : acc.Intersect(ids);
        }
        return acc!.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<EntityRef> Entities(string? key = null) =>
        _byEntity.Keys
            .Where(e => key is null || e.Key == key)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ThenBy(e => e.Value, StringComparer.Ordinal)
            .ToArray();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactStore.cs tests/Scrinia.Tests/EntityFactStoreTests.cs
git commit -m "feat(facts): EntityFactStore membership API (Facts/MentionCount/MemoriesFor/Entities)"
```

---

### Task 4: `EntityFactStore` — analytics API (`Timeline`, `Span`, `Neighbors`)

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityFactStore.cs`
- Test: `tests/Scrinia.Tests/EntityFactStoreAnalyticsTests.cs`

**Interfaces:**
- Produces (added to `EntityFactStore`):
  - `IReadOnlyList<EntityFact> Timeline(EntityRef entity)` — `Facts` ordered by `Date` asc, then `MemoryId` ordinal.
  - `(DateTimeOffset First, DateTimeOffset Last)? Span(EntityRef entity)` — null when no facts.
  - `IReadOnlyList<(EntityRef Entity, int Count)> Neighbors(EntityRef entity)` — co-occurring entities, count = number of memories co-occurring, ordered by count desc, then Key, then Value.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactStoreAnalyticsTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    private static readonly DateTimeOffset D2 = DateTimeOffset.Parse("2023-03-01T00:00:00Z");
    private static readonly DateTimeOffset D3 = DateTimeOffset.Parse("2023-05-01T00:00:00Z");

    private static MemoryEntities Mem(string id, DateTimeOffset date, params (string k, string v)[] ents) =>
        new(id, date, ents.Select(e => EntityRef.Of(e.k, e.v)).ToArray());

    [Fact]
    public void Timeline_OrdersByDate_AndSpanReturnsFirstLast()
    {
        var store = new EntityFactStore([
            Mem("m2", D2, ("person", "jolene")),
            Mem("m1", D1, ("person", "jolene")),
            Mem("m3", D3, ("person", "jolene")),
        ]);

        store.Timeline(EntityRef.Of("person", "jolene")).Select(f => f.MemoryId)
            .Should().Equal("m1", "m2", "m3");
        store.Span(EntityRef.Of("person", "jolene")).Should().Be((D1, D3));
        store.Span(EntityRef.Of("person", "nobody")).Should().BeNull();
    }

    [Fact]
    public void Neighbors_RanksCoOccurringEntitiesByMemoryCount()
    {
        var store = new EntityFactStore([
            Mem("m1", D1, ("person", "jolene"), ("place", "france")),
            Mem("m2", D2, ("person", "jolene"), ("place", "france")),
            Mem("m3", D3, ("person", "jolene"), ("thing", "seraphim")),
        ]);

        store.Neighbors(EntityRef.Of("person", "jolene")).Should().Equal(
            (EntityRef.Of("place", "france"), 2),
            (EntityRef.Of("thing", "seraphim"), 1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreAnalyticsTests"`
Expected: FAIL — `Timeline`/`Span`/`Neighbors` not defined.

- [ ] **Step 3: Write minimal implementation**

Add these methods to `EntityFactStore` (before the closing brace):

```csharp
    public IReadOnlyList<EntityFact> Timeline(EntityRef entity) =>
        Facts(entity)
            .OrderBy(f => f.Date)
            .ThenBy(f => f.MemoryId, StringComparer.Ordinal)
            .ToArray();

    public (DateTimeOffset First, DateTimeOffset Last)? Span(EntityRef entity)
    {
        if (!_byEntity.TryGetValue(entity, out var mems) || mems.Count == 0) return null;
        return (mems.Min(m => m.Date), mems.Max(m => m.Date));
    }

    public IReadOnlyList<(EntityRef Entity, int Count)> Neighbors(EntityRef entity)
    {
        if (!_byEntity.TryGetValue(entity, out var mems)) return [];
        return mems
            .SelectMany(m => m.Entities.Where(e => e != entity))
            .GroupBy(e => e)
            .Select(g => (Entity: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Entity.Key, StringComparer.Ordinal)
            .ThenBy(t => t.Entity.Value, StringComparer.Ordinal)
            .ToArray();
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStoreAnalyticsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactStore.cs tests/Scrinia.Tests/EntityFactStoreAnalyticsTests.cs
git commit -m "feat(facts): EntityFactStore analytics API (Timeline/Span/Neighbors)"
```

---

### Task 5: Sidecar — serialization + validity token + rebuild-on-mismatch

**Files:**
- Create: `src/Scrinia.Core/Search/EntityFactSidecar.cs`
- Test: `tests/Scrinia.Tests/EntityFactSidecarTests.cs`

**Interfaces:**
- Consumes: `MemoryEntities`, `EntityRef` (Task 1); `EntityFactDerivation.Derive` (Task 2); `ScopedArtifact`, `ArtifactEntry`.
- Produces:
  - `const int EntityFactSidecar.Version = 1`
  - `static string EntityFactSidecar.ComputeToken(IEnumerable<(string Id, DateTimeOffset EffectiveDate)> entries)` — order-independent hash.
  - `static string EntityFactSidecar.Serialize(string token, IReadOnlyList<MemoryEntities> memories)`
  - `static (string Token, IReadOnlyList<MemoryEntities> Memories)? EntityFactSidecar.TryLoad(string json)` — null on missing/old-version/corrupt (caller then rebuilds).

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactSidecarTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

    [Fact]
    public void SerializeThenTryLoad_RoundTrips()
    {
        var mems = new[]
        {
            new MemoryEntities("m1", D1, [EntityRef.Of("person", "jolene"), EntityRef.Of("place", "france")]),
        };
        string token = EntityFactSidecar.ComputeToken([("m1", D1)]);

        string json = EntityFactSidecar.Serialize(token, mems);
        var loaded = EntityFactSidecar.TryLoad(json);

        loaded.Should().NotBeNull();
        loaded!.Value.Token.Should().Be(token);
        loaded.Value.Memories.Should().BeEquivalentTo(mems);
    }

    [Fact]
    public void ComputeToken_IsOrderIndependent_AndChangesWithContent()
    {
        var a = EntityFactSidecar.ComputeToken([("m1", D1), ("m2", D1)]);
        var b = EntityFactSidecar.ComputeToken([("m2", D1), ("m1", D1)]);
        a.Should().Be(b);                                     // order-independent
        EntityFactSidecar.ComputeToken([("m1", D1)]).Should().NotBe(a); // content-sensitive
    }

    [Fact]
    public void TryLoad_RejectsWrongVersion()
    {
        string bad = """{"version":999,"token":"x","memories":[]}""";
        EntityFactSidecar.TryLoad(bad).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactSidecarTests"`
Expected: FAIL — `EntityFactSidecar` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Search;

/// <summary>
/// JSON persistence for a scope's <see cref="MemoryEntities"/> set — a rebuildable cache of derived
/// data, guarded by a validity <see cref="ComputeToken">token</see>. The store is NEVER a competing
/// source of truth: a missing / wrong-version / token-mismatch sidecar is rebuilt from the entries.
/// </summary>
public static class EntityFactSidecar
{
    public const int Version = 1;

    /// <summary>Order-independent, content-sensitive token over a scope's (id, effective-date) pairs.
    /// Effective date = <c>UpdatedAt ?? CreatedAt</c>. Sorting makes it independent of entry order.</summary>
    public static string ComputeToken(IEnumerable<(string Id, DateTimeOffset EffectiveDate)> entries)
    {
        var lines = entries
            .Select(e => $"{e.Id} {e.EffectiveDate.UtcDateTime:O}")
            .OrderBy(s => s, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexStringLower(hash);
    }

    public static string Serialize(string token, IReadOnlyList<MemoryEntities> memories)
    {
        var dto = new Dto(
            Version, token,
            memories.Select(m => new MemDto(
                m.MemoryId, m.Date,
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
                m.MemoryId, m.Date,
                m.Entities.Select(r => new EntityRef(r.Key, r.Value)).ToArray()))
            .ToArray();
        return (dto.Token, mems);
    }

    internal sealed record Dto(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("memories")] MemDto[] Memories);

    internal sealed record MemDto(
        [property: JsonPropertyName("id")] string MemoryId,
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("entities")] RefDto[] Entities);

    internal sealed record RefDto(
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("v")] string Value);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EntityFactSidecar.Dto))]
internal partial class SidecarJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactSidecarTests"`
Expected: PASS (6 tests), including replacement invalidation, concurrent first access, and failure recovery.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactSidecar.cs tests/Scrinia.Tests/EntityFactSidecarTests.cs
git commit -m "feat(facts): sidecar serialization + validity token"
```

---

### Task 6: `FileMemoryStore` integration — persisted per-scope sidecars + cached merged view

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (add fact-store fields, `GetEntityFactStore()`, sidecar load/rebuild; invalidate in `Upsert` ~L1032 and `Remove` ~L1076)
- Test: `tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`

**Interfaces:**
- Consumes: `EntityFactDerivation.Derive` (T2), `EntityFactStore` (T3/T4), `EntityFactSidecar` (T5); existing `LoadIndex(scope)`, `GetStoreDirForScope(scope)`, `ListScoped(string? scopes)`, `FormatRoundTripQualifiedName(scope, subject)`.
- Produces: `public EntityFactStore GetEntityFactStore()` on `FileMemoryStore` — merged cross-scope store; builds lazily, loads each scope's `facts.json` when its token matches the current index, else rebuilds + rewrites it; caches the merged view; invalidated on any `Upsert`/`Remove`.

**Design notes for the implementer:**
- The sidecar path for a scope is `Path.Combine(GetStoreDirForScope(scope), "facts.json")`.
- Enumerate scopes + entries with `ListScoped(null)` grouped by `Scope` (reuses the existing, cache-warm index load). For each group: compute the token from `(FormatRoundTripQualifiedName(scope, e.Name), e.UpdatedAt ?? e.CreatedAt)`; if the on-disk `facts.json` loads and its token matches, use its `MemoryEntities`; otherwise `Derive` each entry, write the sidecar, and use the derived set. Concatenate all scopes' `MemoryEntities` and construct one `EntityFactStore`.
- Caching: hold a `private EntityFactStore? _factStore;` guarded by a lock. `Upsert`/`Remove` set `_factStore = null` (inside their existing index-lock critical section, after the index write). Rebuild-on-mismatch means mutation only needs to invalidate the merged cache; stale sidecars are corrected on the next `GetEntityFactStore()` via the token check.
- Writing `facts.json` uses the same UTF-8 `File.WriteAllText` pattern as the meta sidecars; wrap in try/catch and treat a write failure as non-fatal (the store is still correct in memory).

**Required failure/concurrency coverage:** add the following internal seam and tests to this task. In
`src/Scrinia.Core/Search/EntityFactDerivation.cs`, define
`internal static Func<ScopedArtifact, MemoryEntities?> DeriveForStore { get; set; } = Derive;`.
`BuildStore` and `FileMemoryStore.GetEntityFactStore()` must call `DeriveForStore`; no public API changes
and the production default remains `Derive`. In
`tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs`, add:

```csharp
[Fact]
public void Replacement_InvalidatesCachedView_AndAdjacencyReflectsChangedFacts()
{
    using var scope = new TestHelpers.StoreScope();
    var store = (FileMemoryStore)MemoryStoreContext.Current!;
    var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"] }), "local");
    var before = store.GetEntityFactStore();

    store.Upsert(Entry("a", d, attributes: new() { ["place"] = ["paris"] }), "local");

    var after = store.GetEntityFactStore();
    after.Should().NotBeSameAs(before);
    after.MentionCount(EntityRef.Of("person", "maria")).Should().Be(0);
    after.MentionCount(EntityRef.Of("place", "paris")).Should().Be(1);
    after.Entities().Should().Contain(EntityRef.Of("place", "paris"));
    after.Entities().Should().NotContain(EntityRef.Of("person", "maria"));
}

[Fact]
public async Task ConcurrentFirstAccess_ReturnsEquivalentImmutableSnapshots_WithoutCorruption()
{
    using var scope = new TestHelpers.StoreScope();
    var store = (FileMemoryStore)MemoryStoreContext.Current!;
    var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"], ["place"] = ["paris"] }), "local");
    store.Upsert(Entry("b", d, attributes: new() { ["person"] = ["maria"] }), "local:findings");

    var snapshots = await Task.WhenAll(Enumerable.Range(0, 16)
        .Select(_ => Task.Run(() => store.GetEntityFactStore())));

    snapshots.Should().OnlyContain(s => s.MentionCount(EntityRef.Of("person", "maria")) == 2);
    snapshots.Should().OnlyContain(s => s.MentionCount(EntityRef.Of("place", "paris")) == 1);
    snapshots.Should().OnlyContain(s => ReferenceEquals(s, snapshots[0]));
    snapshots.Select(s => s.Entities()).Should().AllBeEquivalentTo(snapshots[0].Entities());
    snapshots.Select(s => s.MemoriesFor(EntityRef.Of("person", "maria")))
        .Should().AllBeEquivalentTo(snapshots[0].MemoriesFor(EntityRef.Of("person", "maria")));
}

[Fact]
public void DerivationFailure_PropagatesOriginalException_AndLaterAccessRecovers()
{
    using var scope = new TestHelpers.StoreScope();
    var store = (FileMemoryStore)MemoryStoreContext.Current!;
    var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"] }), "local");
    var expected = new InvalidOperationException("derivation sentinel");
    bool fail = true;
    var previous = EntityFactDerivation.DeriveForStore;
    try
    {
        EntityFactDerivation.DeriveForStore = item =>
        {
            if (fail) throw expected;
            return EntityFactDerivation.Derive(item);
        };

        Action first = () => store.GetEntityFactStore();
        first.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
        fail = false;
        store.GetEntityFactStore().MentionCount(EntityRef.Of("person", "maria")).Should().Be(1);
    }
    finally
    {
        EntityFactDerivation.DeriveForStore = previous;
    }
}
```

The first test must use FileMemoryStore's existing same-name replacement path, not manually clear a
cache. The second must use `Task.WhenAll`/`Task.Run` and assert equivalent results from the immutable
materialized snapshots. The third is also the exact façade/materializer failure-propagation test: the
same exception instance must cross `GetEntityFactStore()` unchanged, and a later successful build must
prove the failed first build did not poison `_factStore`.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactStorePersistenceTests
{
    private static ArtifactEntry Entry(string name, DateTimeOffset created,
        string[]? keywords = null, Dictionary<string, string[]>? attributes = null) =>
        new(Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: created, Keywords: keywords, Attributes: attributes);

    [Fact]
    public void GetEntityFactStore_AggregatesAcrossScopes_AndWritesSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"] }), "local");
        store.Upsert(Entry("b", d, attributes: new() { ["person"] = ["maria"] }), "local:findings");

        var facts = store.GetEntityFactStore();

        facts.MentionCount(EntityRef.Of("person", "maria")).Should().Be(2); // cross-scope merge
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "facts.json")).Should().BeTrue();
    }

    [Fact]
    public void GetEntityFactStore_RebuildsWhenSidecarTokenStale()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        _ = store.GetEntityFactStore(); // writes facts.json

        // Corrupt the sidecar with a stale token; the index no longer matches it.
        string sidecar = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        File.WriteAllText(sidecar, EntityFactSidecar.Serialize("STALE_TOKEN", []));

        var facts = store.GetEntityFactStore(); // must rebuild from entries, not trust the file
        facts.MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);
    }

    [Fact]
    public void Mutation_InvalidatesCachedMergedView()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);

        store.Upsert(Entry("b", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(2); // fresh
    }
}
```

> Note: confirm the `TestHelpers.StoreScope` helper name/shape against existing tests (e.g. `ScriniaMcpToolsTests.cs`) and match it; it configures `MemoryStoreContext.Current` to a temp workspace and disposes it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStorePersistenceTests"`
Expected: FAIL — `GetEntityFactStore` not defined.

- [ ] **Step 3: Write minimal implementation**

Add to `FileMemoryStore` (field near the other caches; method in the search/query region):

```csharp
    private readonly object _factStoreLock = new();
    private EntityFactStore? _factStore;

    /// <summary>
    /// The merged cross-scope <see cref="EntityFactStore"/>. Built lazily and cached; invalidated on
    /// any Upsert/Remove. Each scope's facts persist in a <c>facts.json</c> sidecar, reused when its
    /// validity token matches the current index and rebuilt-and-rewritten otherwise (never trusted blindly).
    /// </summary>
    public EntityFactStore GetEntityFactStore()
    {
        lock (_factStoreLock)
        {
            if (_factStore is not null) return _factStore;

            var merged = new List<MemoryEntities>();
            foreach (var group in ListScoped(null).GroupBy(sa => sa.Scope, StringComparer.Ordinal))
            {
                string scope = group.Key;
                var items = group.ToList();
                string token = EntityFactSidecar.ComputeToken(
                    items.Select(sa => (FormatRoundTripQualifiedName(scope, sa.Entry.Name),
                                        sa.Entry.UpdatedAt ?? sa.Entry.CreatedAt)));

                string sidecarPath = Path.Combine(GetStoreDirForScope(scope), "facts.json");
                IReadOnlyList<MemoryEntities>? mems = null;
                if (File.Exists(sidecarPath))
                {
                    var loaded = EntityFactSidecar.TryLoad(SafeReadAllText(sidecarPath));
                    if (loaded is { } l && l.Token == token) mems = l.Memories;
                }

                if (mems is null)
                {
                    mems = items
                        .Select(sa => EntityFactDerivation.Derive(
                            sa, FormatRoundTripQualifiedName(scope, sa.Entry.Name)))
                        .Where(m => m is not null)
                        .Select(m => m!)
                        .ToArray();
                    TryWriteAllText(sidecarPath, EntityFactSidecar.Serialize(token, mems));
                }

                merged.AddRange(mems);
            }

            return _factStore = new EntityFactStore(merged);
        }
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch { return string.Empty; }
    }

    private static void TryWriteAllText(string path, string content)
    {
        try { File.WriteAllText(path, content); } catch { /* in-memory store is still correct */ }
    }
```

Then, inside `Upsert` (after the index write, within the `_indexLock`/critical section) and inside `Remove` (same place), add:

```csharp
            lock (_factStoreLock) { _factStore = null; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityFactStorePersistenceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the FULL suite (guard against regressions in the mutation path)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS — all prior tests plus the new ones (no regressions from the `Upsert`/`Remove` hook).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/EntityFactStorePersistenceTests.cs
git commit -m "feat(facts): FileMemoryStore persisted per-scope sidecars + cached merged fact store"
```

---

## Self-Review

**Spec coverage:**
- Materialized view of the graph's entity union + effective date → Tasks 1, 2. ✓
- Seven-method API (Facts/Timeline/MentionCount/MemoriesFor(params)/Neighbors/Span/Entities) → Tasks 3, 4. ✓
- `Date = UpdatedAt ?? CreatedAt` → Task 2 (derive) + Task 5 (token uses same). ✓
- Per-scope sidecar, cache-on-read + invalidate-on-mutation → Task 6. ✓
- Rebuild-on-token-mismatch, never migrate → Task 5 (`TryLoad` version gate + `ComputeToken`) + Task 6 (token compare → rebuild). ✓
- Cross-scope merged view → Task 6. ✓
- Additive to `EntityGraph`; shared entity definition → Task 2 uses `ParseEntityKeywords ∪ ParseEntityAttributes`; nothing in `EntityGraph` modified. ✓
- `MemoriesFor()` zero-arg → empty → Task 3 test + impl. ✓

**Non-goals honored:** no extraction from prose, no consumer (tool/auto-surface), no scorer/graph change, no per-attribute provenance. ✓

**Placeholder scan:** none — every step has complete code and exact commands.

**Type consistency:** `EntityRef`, `MemoryEntities`, `EntityFact`, `EntityFactStore` ctor `IEnumerable<MemoryEntities>`, `EntityFactDerivation.Derive(ScopedArtifact)`, internal `EntityFactDerivation.DeriveForStore(Func<ScopedArtifact, MemoryEntities?>)`, `EntityFactSidecar.{Version, ComputeToken, Serialize, TryLoad}`, `FileMemoryStore.GetEntityFactStore()` — consistent across tasks. The one place to verify against the codebase is the `TestHelpers.StoreScope` helper name (flagged in Task 6) and the exact insertion points in `Upsert`/`Remove` (line refs given).

**Final integration base discipline:** capture `INTENDED_BASE` immediately before execution with
`INTENDED_BASE=$(git rev-parse HEAD)` (PowerShell equivalent:
`$env:INTENDED_BASE = git rev-parse HEAD`). Use that fixed SHA for every range or scope comparison; never
infer a base from `origin/main` or another remote branch. Preserve the approved plan-set requirements for
supported RIDs, the external output root, the exact MCP probe, workflow fixture/parser/job gating, commit
boundaries, dependencies/order, citations, headers, historical-document protection, and untracked-artifact
exclusions.
