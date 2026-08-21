# Durable Format Versioning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every durable on-disk format one shared version gate, and make the artifact sidecar preserve fields it does not understand instead of deleting them.

**Architecture:** A new `Scrinia.Core.Formats` namespace holds a four-case disposition enum, a `FormatContract` record, one `Evaluate` function, and a registry with one contract per durable format. `ArtifactEntry` gains an `int?` schema version and a `[JsonExtensionData]` bucket. Reads never throw on a version; writes refuse where the entries are already in hand, and the refusal follows the memory rather than the scope.

**Tech Stack:** .NET 10, C#, System.Text.Json source generation, xunit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-21-durable-format-versioning-design.md`

## Global Constraints

- **A v1 sidecar must emit no `"v"` key.** `SchemaVersion` is `int?`, written as `null` while `Current == 1`. Any change that makes existing sidecars re-serialize differently is a defect (spec §B3, test D6/D7).
- **No read path may throw on a version.** The gate is evaluated outside `ReadSidecarRaw`'s `catch`, and read-time forward-fill skips the persist rather than catching a refusal (spec §B4).
- **`[JsonExtensionData]` must be `{ get; set; }`, never `{ get; init; }`.** Source generation throws `InvalidOperationException` at first use otherwise. Verified, not assumed.
- **Corrupt sidecars keep skipping.** The existing `catch { return null; }` behaviour is unchanged (test D13).
- **`facts.json` behaviour must not change.** It rebuilds on older *and* newer, and `AbsentMeans = 0` is what preserves that (spec §B2, test D14).
- **Do not reroute the NMP/2 parser.** It registers a contract and gets a conformance test only (spec §B6/C6).
- **The extension bucket preserves nothing without Task 3b's inheritance rule.** A round-trip of one object keeps unknown fields; no ordinary write is a round-trip. Any test that only round-trips an object is not evidence (spec §B7).
- **No write path may read the scope's index.** `Upsert` forbids it by comment — it re-introduces finding H5's O(N²). The gate goes where entries are already in hand (spec §B5).
- **`FileMemoryStore.Store(...) does not exist.`** Writes are `await store.StoreAsync(new MemoryWrite(name, scope, [body]))` — **Name first, then Scope**. Tests calling it must be `async Task`.
- **Use `TestHelpers.StoreScope`** (`tests/Scrinia.Tests/TestHelpers.cs:97`, exposes `WorkspaceDir` and `TempDir`). Do not write a new temp-workspace helper.
- **Tests touching the extension bucket must use a source-generated context**, not hand-built `JsonSerializerOptions` — reflection does not exercise the `{ get; init; }` failure that makes `{ get; set; }` mandatory. `ArtifactEntrySerializationTests.TestCtx` is the existing fixture.
- Commit after each task. Use a heredoc for multi-line commit messages, never a PowerShell here-string.
- Run tests synchronously. Do not background a test run.

---

## File Structure

**Create:**
- `src/Scrinia.Core/Formats/FormatDisposition.cs` — the four-case enum.
- `src/Scrinia.Core/Formats/FormatContract.cs` — the contract record.
- `src/Scrinia.Core/Formats/FormatVersion.cs` — `Evaluate`, plus `ArtifactSidecarPolicy.IsReadOnly`.
- `src/Scrinia.Core/Formats/DurableFormats.cs` — the registry and `All`.
- `src/Scrinia.Core/Formats/ScopeReadOnlyException.cs` — the refusal type.
- `tests/Scrinia.Tests/Formats/FormatVersionTests.cs` — gate matrix + registry completeness.
- `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` — round-trip, no-churn, read-only enforcement.

**Modify:**
- `src/Scrinia.Core/Models/ArtifactEntry.cs` — two new members.
- `src/Scrinia.Core/FileMemoryStore.cs` — `CachedIndex` ctor, forward-fill skip, `BuildDerivedEntry` inheritance, `WriteSidecars` pre-check, `Upsert` check, `WriteSidecarCore` backstop and bucket sort.
- `src/Scrinia.Core/Search/EntityFactSidecar.cs` — route through the gate.
- `src/Scrinia.Core/Bundles/BundleFormatService.cs` — read the manifest version; refuse newer entries.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs` — version + bucket on `SkillFileMeta` / `AgentFileMeta`.

---

## Task 1: The gate, the registry, and the exception

**Files:**
- Create: `src/Scrinia.Core/Formats/FormatDisposition.cs`, `FormatContract.cs`, `FormatVersion.cs`, `DurableFormats.cs`, `ScopeReadOnlyException.cs`
- Test: `tests/Scrinia.Tests/Formats/FormatVersionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Scrinia.Core.Formats.FormatDisposition` (`Load`, `LoadReadOnly`, `Rebuild`, `Refuse`); `FormatContract(string Name, int Current, int? AbsentMeans, FormatDisposition OnOlder, FormatDisposition OnNewer)`; `static FormatDisposition FormatVersion.Evaluate(FormatContract, int?)`; `static bool ArtifactSidecarPolicy.IsReadOnly(ArtifactEntry)`; `DurableFormats.{ArtifactSidecar, EntityFacts, Nmp2Body, BundleManifest, LegacyIndex, SkillMeta, AgentMeta}` and `DurableFormats.All`; `ScopeReadOnlyException(string scope, string memoryName, int declared, int supported)` with a `Scope` property.

`ArtifactSidecarPolicy.IsReadOnly` is referenced by Tasks 3 and 4 — it must exist after this task even though nothing calls it yet.

- [ ] **Step 1: Write the failing gate tests**

Create `tests/Scrinia.Tests/Formats/FormatVersionTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Formats;

namespace Scrinia.Tests.Formats;

public class FormatVersionTests
{
    private static readonly FormatContract Sample =
        new("sample", Current: 3, AbsentMeans: 1,
            OnOlder: FormatDisposition.Load, OnNewer: FormatDisposition.LoadReadOnly);

    [Fact]
    public void Equal_Loads() =>
        FormatVersion.Evaluate(Sample, 3).Should().Be(FormatDisposition.Load);

    [Fact]
    public void Older_TakesOnOlder() =>
        FormatVersion.Evaluate(Sample, 2).Should().Be(FormatDisposition.Load);

    [Fact]
    public void Newer_TakesOnNewer() =>
        FormatVersion.Evaluate(Sample, 4).Should().Be(FormatDisposition.LoadReadOnly);

    [Fact]
    public void Absent_SubstitutesAbsentMeans_ThenEvaluates() =>
        // AbsentMeans is 1, which is < Current 3, so this must land on OnOlder — NOT on Load
        // by some separate "absent is fine" shortcut. A contract whose OnOlder is Rebuild
        // depends on this substitution happening before the comparison.
        FormatVersion.Evaluate(Sample, null).Should().Be(Sample.OnOlder);

    [Fact]
    public void Absent_WhenAbsentIsInvalid_Refuses()
    {
        var strict = Sample with { AbsentMeans = null };
        FormatVersion.Evaluate(strict, null).Should().Be(FormatDisposition.Refuse);
    }

    [Fact]
    public void EveryRegisteredContract_HasADistinctName()
    {
        var names = DurableFormats.All.Select(c => c.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Registry_CoversEveryDurableFormat()
    {
        // Guard, not trivia: adding a durable format without registering a contract must fail
        // a test rather than pass silently. Bump this deliberately when a format is added.
        DurableFormats.All.Should().HaveCount(7);
    }

    [Theory]
    [InlineData("artifact-sidecar", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("entity-facts", 3, FormatDisposition.Rebuild, FormatDisposition.Rebuild)]
    [InlineData("nmp2-body", 2, FormatDisposition.Refuse, FormatDisposition.Refuse)]
    [InlineData("bundle-manifest", 2, FormatDisposition.Load, FormatDisposition.Refuse)]
    [InlineData("legacy-index", 3, FormatDisposition.Load, FormatDisposition.Load)]
    [InlineData("skill-meta", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("agent-meta", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    public void RegisteredContracts_MatchTheSpecTable(
        string name, int current, FormatDisposition onOlder, FormatDisposition onNewer)
    {
        var c = DurableFormats.All.Single(x => x.Name == name);
        c.Current.Should().Be(current);
        c.OnOlder.Should().Be(onOlder);
        c.OnNewer.Should().Be(onNewer);
    }

    [Fact]
    public void EntityFacts_AbsentMeansZero_SoAMissingKeyRebuilds() =>
        // EntityFactSidecar.Dto.Version is a non-nullable int: a missing "version" key
        // deserializes to 0, which must route through OnOlder to Rebuild. AbsentMeans = null
        // would return Refuse and change facts.json's contract.
        FormatVersion.Evaluate(DurableFormats.EntityFacts, 0)
            .Should().Be(FormatDisposition.Rebuild);
}
```

- [ ] **Step 2: Run the tests and confirm they fail to compile**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.FormatVersionTests`
Expected: build error — `Scrinia.Core.Formats` does not exist.

- [ ] **Step 3: Write `FormatDisposition.cs`**

```csharp
namespace Scrinia.Core.Formats;

/// <summary>What a reader should do with a durable file whose declared version has been
/// compared against the version this build writes.</summary>
public enum FormatDisposition
{
    /// <summary>Proceed normally.</summary>
    Load,

    /// <summary>Readable, but writes to the owning unit must refuse. For a source-of-truth
    /// format, where discarding the file is not an option.</summary>
    LoadReadOnly,

    /// <summary>Discard and re-derive. Valid ONLY for a rebuildable cache, never for a format
    /// that is the only copy of its data.</summary>
    Rebuild,

    /// <summary>Do not load at all.</summary>
    Refuse
}
```

- [ ] **Step 4: Write `FormatContract.cs`**

```csharp
namespace Scrinia.Core.Formats;

/// <summary>
/// One durable format's version contract. Every format that persists bytes this project reads
/// back must have one, registered in <see cref="DurableFormats"/>.
///
/// <para><b>Why <see cref="OnOlder"/> and <see cref="OnNewer"/> are separate knobs.</b> The
/// obvious design is one policy for "version mismatch". It cannot express the one format in
/// this repository whose gate already works: <c>facts.json</c> rebuilds on an OLDER version
/// too, because its version tracks the DERIVATION RULE rather than the file's shape, so a
/// cache written by a previous build is a path around the new rule. Collapsing the two knobs
/// would force that format out of the registry.</para>
/// </summary>
/// <param name="Name">Stable identifier, used in diagnostics and in the registry tests.</param>
/// <param name="Current">The version this build writes.</param>
/// <param name="AbsentMeans">The version implied by a missing version field, or null when a
/// missing field makes the file invalid (a format whose version is a required sentinel).</param>
public sealed record FormatContract(
    string Name,
    int Current,
    int? AbsentMeans,
    FormatDisposition OnOlder,
    FormatDisposition OnNewer);
```

- [ ] **Step 5: Write `FormatVersion.cs`**

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Formats;

/// <summary>The one place that decides what a declared format version means.</summary>
public static class FormatVersion
{
    /// <summary>
    /// Compares a file's declared version against its contract.
    ///
    /// <para>The arithmetic is deliberately trivial. The value is not the arithmetic — it is
    /// that a durable format cannot exist without answering four questions, and that the
    /// answers live in one readable table instead of inline at seven read sites.</para>
    /// </summary>
    public static FormatDisposition Evaluate(FormatContract contract, int? declared)
    {
        ArgumentNullException.ThrowIfNull(contract);

        // Absent is SUBSTITUTED, not short-circuited: a contract whose AbsentMeans is below
        // Current must still land on OnOlder. Returning Load here would silently exempt every
        // unversioned file from the older-version policy.
        int effective;
        if (declared is null)
        {
            if (contract.AbsentMeans is null) return FormatDisposition.Refuse;
            effective = contract.AbsentMeans.Value;
        }
        else
        {
            effective = declared.Value;
        }

        if (effective == contract.Current) return FormatDisposition.Load;
        return effective < contract.Current ? contract.OnOlder : contract.OnNewer;
    }
}

/// <summary>The artifact sidecar's half of the gate, in one place so no caller re-derives it.</summary>
public static class ArtifactSidecarPolicy
{
    /// <summary>True when this entry was written by a build newer than ours, so its sidecar
    /// must not be rewritten. Reading it is always allowed.</summary>
    public static bool IsReadOnly(ArtifactEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return FormatVersion.Evaluate(DurableFormats.ArtifactSidecar, entry.SchemaVersion)
            == FormatDisposition.LoadReadOnly;
    }
}
```

Note: `ArtifactSidecarPolicy` references `ArtifactEntry.SchemaVersion`, added in Task 2. Add the member in Task 2 before this compiles; for this task, `IsReadOnly` may temporarily take `int?` instead. **Preferred: do Task 2's `SchemaVersion` addition first if the compiler objects** — the two tasks share this one seam and reordering the member's introduction is cheaper than a shim.

- [ ] **Step 6: Write `DurableFormats.cs`**

```csharp
namespace Scrinia.Core.Formats;

/// <summary>
/// Every durable format this project reads back, with its version contract.
///
/// <para><b>Adding a format to this file is not optional.</b> A durable format with no
/// contract is exactly the state the 2026-08-21 review found: three formats with no version,
/// two writing a version nothing read, and two that were never enumerated at all. The
/// registry-count test in <c>FormatVersionTests</c> is what makes the omission fail loudly.</para>
///
/// <para><c>config.json</c> is deliberately ABSENT: it is a free-form
/// <c>Dictionary&lt;string,string&gt;</c>, so unknown keys already survive a round-trip and
/// there is no schema to version. Recorded so its absence does not read as an oversight.</para>
/// </summary>
public static class DurableFormats
{
    /// <summary>The <c>.meta.json</c> artifact sidecar — the store's source of truth. Newer
    /// files load read-only: discarding is not available for the only copy of the data.</summary>
    public static readonly FormatContract ArtifactSidecar =
        new("artifact-sidecar", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    /// <summary><c>facts.json</c> — a rebuildable cache. Rebuilds in BOTH directions because
    /// its version tracks the derivation rule, not the file shape. AbsentMeans is 0 rather
    /// than null because Dto.Version is a non-nullable int, so a missing key deserializes to
    /// 0 and must route through OnOlder to Rebuild — preserving today's behaviour exactly.</summary>
    public static readonly FormatContract EntityFacts =
        new("entity-facts", Current: 3, AbsentMeans: 0,
            FormatDisposition.Rebuild, FormatDisposition.Rebuild);

    /// <summary>The NMP/2 artifact body. Its version is the header sentinel, already enforced
    /// by the parser; this contract documents and tests that behaviour rather than replacing
    /// it. A body with no sentinel is not an old artifact, it is not an artifact.</summary>
    public static readonly FormatContract Nmp2Body =
        new("nmp2-body", Current: 2, AbsentMeans: null,
            FormatDisposition.Refuse, FormatDisposition.Refuse);

    /// <summary>A bundle's <c>manifest.json</c>. Transport, not storage — refusing a newer
    /// bundle costs the user a re-export, not their data.
    ///
    /// <para><b>Current is 2, not 1.</b> WriteManifest already writes <c>hasFileEntities ? 2 : 1</c>,
    /// so Current:1 with OnNewer:Refuse would make this build refuse bundles it had itself just
    /// exported. The 1/2 pair is a CAPABILITY marker — 2 means "carries file entities" — which
    /// happens to be monotonic, so a linear contract models it. Anything above 2 is genuinely
    /// newer.</para></summary>
    public static readonly FormatContract BundleManifest =
        new("bundle-manifest", Current: 2, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.Refuse);

    /// <summary>The legacy <c>index.json</c>. No longer written; read only by the layout
    /// migration. The contract is documentary — a "newer" one cannot exist.
    ///
    /// <para>AbsentMeans is 3 and is UNREACHABLE: IndexFile.Version is a non-nullable int with a
    /// `= 3` property initializer, so a missing key deserializes to 3, never to absent. Set to 3
    /// so the contract states something true.</para></summary>
    public static readonly FormatContract LegacyIndex =
        new("legacy-index", Current: 3, AbsentMeans: 3,
            FormatDisposition.Load, FormatDisposition.Load);

    /// <summary><c>.scrinia/skills/{name}.meta.json</c>.</summary>
    public static readonly FormatContract SkillMeta =
        new("skill-meta", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    /// <summary>An agent config file's <c>.meta.json</c>.</summary>
    public static readonly FormatContract AgentMeta =
        new("agent-meta", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    public static IReadOnlyList<FormatContract> All { get; } =
    [
        ArtifactSidecar, EntityFacts, Nmp2Body, BundleManifest, LegacyIndex, SkillMeta, AgentMeta
    ];
}
```

- [ ] **Step 7: Write `ScopeReadOnlyException.cs`**

```csharp
namespace Scrinia.Core.Formats;

/// <summary>
/// Thrown when a write targets a topic scope holding a sidecar written by a newer build.
///
/// <para>One type for both refusal levels — the scope gate that stops ordinary write verbs
/// early, and the <c>WriteSidecarCore</c> backstop — so a caller has one thing to catch
/// regardless of which fired.</para>
/// </summary>
public sealed class ScopeReadOnlyException : InvalidOperationException
{
    public string Scope { get; }
    public string MemoryName { get; }
    public int DeclaredVersion { get; }
    public int SupportedVersion { get; }

    public ScopeReadOnlyException(string scope, string memoryName, int declared, int supported)
        : base($"Scope '{scope}' is read-only: memory '{memoryName}' declares schema version " +
               $"{declared}, but this build of scri supports up to {supported}. " +
               "Reading and searching still work. Upgrade scri to write to this scope.")
    {
        Scope = scope;
        MemoryName = memoryName;
        DeclaredVersion = declared;
        SupportedVersion = supported;
    }
}
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.FormatVersionTests`
Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/Formats tests/Scrinia.Tests/Formats
git commit -F - <<'MSG'
feat(formats): one version gate, and a registry that makes omission loud

Every durable format now declares what its current version is, what an
absent version means, and what happens on older and on newer. Two knobs
rather than one because facts.json -- the only format whose gate already
works -- rebuilds in both directions.

Nothing calls this yet.
MSG
```

---

## Task 2: `ArtifactEntry` carries a version and keeps what it does not understand

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`WriteSidecarCore`'s `sorted` expression only)
- Test: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 at runtime; `ArtifactSidecarPolicy.IsReadOnly` compiles against the `SchemaVersion` member this task adds.
- Produces: `ArtifactEntry.SchemaVersion` (`int?`, JSON key `"v"`, trailing optional positional parameter) and `ArtifactEntry.Unknown` (`Dictionary<string, JsonElement>?`, `[JsonExtensionData]`, **`{ get; set; }`**).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (Task 1 creates only
`FormatVersionTests.cs` in that directory, so this file does not exist yet — later tasks say
"append to" and mean this file):

```csharp
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Formats;

public class SidecarVersioningTests
{
    // Mirrors FileStoreJsonContext's options exactly; the store's context is private.
    private static readonly JsonSerializerOptions StoreLike = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void UnknownKey_SurvivesReadThenWrite()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","futureThing":{"a":1}}
        """;

        var entry = JsonSerializer.Deserialize<ArtifactEntry>(json, StoreLike)!;
        entry.Unknown.Should().ContainKey("futureThing");

        JsonSerializer.Serialize(entry, StoreLike).Should().Contain("futureThing");
    }

    [Fact]
    public void Description_TheLiveMergeDriverInstance_SurvivesReadThenWrite()
    {
        // MetaJsonMerger three-way-merges a "description" key that ArtifactEntry has had no
        // member for since the description -> contentPreview rename. Today the merge driver
        // preserves it and the store deletes it on the next read-write. This is that bug.
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","description":"preserved by the merge driver"}
        """;

        var entry = JsonSerializer.Deserialize<ArtifactEntry>(json, StoreLike)!;
        JsonSerializer.Serialize(entry, StoreLike)
            .Should().Contain("preserved by the merge driver");
    }

    [Fact]
    public void SchemaVersion_RoundTrips()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","v":2}
        """;

        JsonSerializer.Deserialize<ArtifactEntry>(json, StoreLike)!.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void V1Entry_EmitsNoVersionKey()
    {
        // The whole no-churn property. If this fails, adopting versioning rewrites every
        // sidecar in every user's store through the merge driver for zero information.
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1, DateTimeOffset.UtcNow);

        entry.SchemaVersion.Should().BeNull();
        JsonSerializer.Serialize(entry, StoreLike).Should().NotContain("\"v\"");
    }

    [Fact]
    public void EntryWithNoUnknowns_EmitsNoExtraKeys()
    {
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var reparsed = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(entry, StoreLike));

        reparsed.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("name", "uri", "originalBytes", "chunkCount", "createdAt");
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: build error — `ArtifactEntry` has no `Unknown` or `SchemaVersion`.

- [ ] **Step 3: Add the two members to `ArtifactEntry`**

Append `SchemaVersion` as the **last** positional parameter, immediately after `AgentKeywords`:

```csharp
    [property: System.Text.Json.Serialization.JsonPropertyName("agentKeywords")]
    string[]? AgentKeywords = null,
    /// <summary>
    /// The sidecar schema version this entry was written under. Null means "not stated", which
    /// is definitionally version 1 — every sidecar written before versioning existed is a v1
    /// sidecar, so there is no migration to run and no file to write one in.
    ///
    /// <para><b>Deliberately not written while Current is 1.</b> The store's serializer sets
    /// <c>WhenWritingNull</c>, so a v1 entry emits no <c>"v"</c> key and is byte-identical to
    /// one written before this member existed. Writing <c>"v": 1</c> explicitly would rewrite
    /// every sidecar in every workspace, push a whole-store diff through the merge driver, and
    /// communicate nothing that <c>absent ≡ 1</c> does not already say.</para>
    /// </summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("v")]
    int? SchemaVersion = null)
```

Then add the bucket inside the record body, next to `LegacyFields`:

```csharp
    /// <summary>
    /// Keys present in the sidecar that this build has no member for, preserved verbatim so a
    /// read-write cycle cannot delete them. System.Text.Json source generation drops unmapped
    /// members by default; before this bucket existed, an older build reading a newer sidecar
    /// destroyed the unknown field on its next write — and "its next write" includes read-time
    /// forward-fill, so a plain search() was enough.
    ///
    /// <para><b>Mutable by necessity.</b> STJ refuses an init-only extension-data property on a
    /// record with a parameterized constructor ("the extension data property cannot bind with a
    /// parameter in the deserialization constructor"), so this cannot be <c>{ get; init; }</c>.
    /// It therefore participates in the synthesized Equals/GetHashCode exactly as
    /// <c>_legacyFields</c> does, with the same defusing argument: ArtifactEntry equality is
    /// already non-value-semantic (its Dictionary/array members compare by reference) and
    /// nothing relies on it.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
```

- [ ] **Step 4: Sort the bucket on write**

In `FileMemoryStore.WriteSidecarCore`, extend the existing `sorted` expression — the one that already orders `Keywords`, `TermFrequencies`, `FileHashes` and `ChunkEntries` for stable git diffs (G-29) — with one more member:

```csharp
            Unknown = entry.Unknown?
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
```

Use `StringComparer.Ordinal`, not `OrdinalIgnoreCase`: unknown keys come from a schema this build cannot reason about, and JSON keys are case-sensitive, so two keys differing only in case are two keys.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: all PASS.

- [ ] **Step 6: Run the full suite — this is the churn check**

Run: `dotnet test Scrinia.sln`
Expected: 0 failures. `ArtifactEntrySerializationTests` is the one to watch: it asserts exact serialized output, so it is the existing test that would catch an accidental `"v": 1`.

- [ ] **Step 7: Verify no sidecar on disk changed**

First force a real re-serialize of this repo's own sidecars, or the check passes vacuously — nothing in the earlier steps reads and rewrites them:

```bash
dotnet run --project src/Scrinia -- list --json > /dev/null
git status --porcelain .scrinia/
```

Expected: **empty**. If anything in `.scrinia/` re-serialized differently, the no-churn constraint is broken and the `int?` decision in Step 3 has regressed. Restore `.scrinia/memories/**/facts.json` if only the derived fact caches moved — those rebuild on read and are not evidence either way.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/Models/ArtifactEntry.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs
git commit -F - <<'MSG'
feat(store): sidecars carry a version and keep fields they do not understand

The extension bucket is what preserves; the version is what will let a
future build refuse. Both, because either alone fails: a version without a
bucket still deletes the bytes, and a bucket without a version cannot tell
a reader that its understanding of the KNOWN fields has expired.

A v1 entry emits no "v" key, so every existing sidecar is byte-identical.

Closes the live instance: MetaJsonMerger preserves a "description" key the
store then deleted on the next read-write. It now round-trips, with a test.
MSG
```

---

## Task 3: Read path — evaluate outside the catch, and stop forward-fill from writing

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`CachedIndex` ctor; `LoadIndexUnsafe`'s forward-fill loop)
- Test: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (append)

**Interfaces:**
- Consumes: `ArtifactSidecarPolicy.IsReadOnly(ArtifactEntry)` from Task 1; `ArtifactEntry.SchemaVersion` from Task 2.
- Produces: nothing new.

**`CachedIndex` is NOT modified.** An earlier draft of this plan added a computed `BlockingEntry`
to it, for a scope gate in Task 4 that no longer exists — Task 4's gate reads the entry it is
about to write, not a cached scope summary. Adding an unread member would be dead code on arrival.
`CachedIndex` stays exactly as it is.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs`. These need a real store on disk; follow the pattern in `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` for temp-workspace setup.

```csharp
    [Fact]
    public async Task NewerSidecar_IsNotDroppedFromTheIndex()
    {
        // The C1-class regression guard. If the version gate is evaluated INSIDE
        // ReadSidecarRaw's blanket `catch { return null; }`, a newer sidecar is silently
        // dropped from the index -- which is the previous Critical's failure mode, rebuilt
        // by hand inside the fix for this one.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("ordinary", "local:notes", ["body"]));

        WriteRawSidecar(scope, "local:notes", "from-the-future", schemaVersion: 2);

        ((FileMemoryStore)store).InvalidateCaches();   // NOT on IMemoryStore — cast required
        store.LoadIndex("local:notes").Select(e => e.Name)
            .Should().Contain("from-the-future");
    }

    [Fact]
    public void NewerSidecar_IsStillReadable_WithItsUnknownFieldsIntact()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        WriteRawSidecar(scope, "local:notes", "from-the-future",
            schemaVersion: 2, extraKey: "futureThing");

        store.LoadIndex("local:notes").Single(e => e.Name == "from-the-future")
            .Unknown.Should().ContainKey("futureThing");
    }

    [Fact]
    public void ForwardFill_DoesNotRewriteANewerSidecar_AndDoesNotThrow()
    {
        // The sidecar MUST be legacy-shaped (a "fields" key rather than "attributes") so
        // AttributeMigration.MigratesTo returns true. That is the only condition under which
        // the forward-fill loop reaches a write at all -- without it this test passes
        // vacuously and proves nothing.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string path = WriteRawSidecar(scope, "local:notes", "legacy-and-newer",
            schemaVersion: 2, legacyShaped: true);

        byte[] before = File.ReadAllBytes(path);

        var act = () => store.LoadIndex("local:notes");
        act.Should().NotThrow("a version refusal must never escape a plain read path");

        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public void ForwardFill_StillRewritesALegacySidecarAtTheCurrentVersion()
    {
        // The control for the test above: prove the skip is conditional on the VERSION, not
        // on the migration having quietly stopped working.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string path = WriteRawSidecar(scope, "local:notes", "legacy-only",
            schemaVersion: null, legacyShaped: true);

        byte[] before = File.ReadAllBytes(path);
        store.LoadIndex("local:notes");

        File.ReadAllBytes(path).Should().NotEqual(before);
    }

    [Fact]
    public async Task CorruptSidecar_IsStillSkipped_NotRefused()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("ordinary", "local:notes", ["body"]));
        File.WriteAllText(SidecarPath(scope, "local:notes", "broken"), "{ not json");

        store.LoadIndex("local:notes").Select(e => e.Name).Should().Contain("ordinary");
    }
```

Use the EXISTING `TestHelpers.StoreScope` (`tests/Scrinia.Tests/TestHelpers.cs:97`) — it exposes `WorkspaceDir` and `TempDir` and installs `MemoryStoreContext.Current`, and `TransactionalRestoreTests` uses exactly this pattern. Do not write a new temp-workspace type; the two `TempWorkspace` classes in the migration tests are private nested classes and are not reusable.

Write only `WriteRawSidecar` and `SidecarPath`. `WriteRawSidecar` composes the JSON **by hand** — it must be able to emit a `"v"` key and a legacy `"fields"` key, which the typed model deliberately cannot produce.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: `ForwardFill_DoesNotRewriteANewerSidecar_AndDoesNotThrow` FAILS — the sidecar is rewritten.

- [ ] **Step 3: Skip the persist in the forward-fill loop**

In `LoadIndexUnsafe`, change the loop body:

```csharp
        for (int i = 0; i < entries.Count; i++)
        {
            if (AttributeMigration.MigratesTo(entries[i], out var upgraded))
            {
                entries[i] = upgraded;

                // The in-memory upgrade above stands; only the PERSIST is skipped. A sidecar
                // written by a newer build must not be rewritten by this one, and the check
                // has to happen HERE rather than as a refusal thrown from WriteSidecar: this
                // loop's catch is filtered to IOException and UnauthorizedAccessException, so
                // a ScopeReadOnlyException would propagate out of a plain read and break
                // search() for the whole scope. Attempting a write already known to be refused
                // and then widening the catch to swallow it is the worse repair.
                if (ArtifactSidecarPolicy.IsReadOnly(upgraded)) continue;

                try { WriteSidecar(upgraded, storeDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort; see above */ }
            }
        }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: all PASS, including the control test that a current-version legacy sidecar IS still rewritten.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs
git commit -F - <<'MSG'
fix(store): a newer sidecar loads, and read-time forward-fill leaves it alone

Two properties, and the second is the one that would have bitten.

Forward-fill's write-back is wrapped in a catch filtered to IOException and
UnauthorizedAccessException. A version refusal thrown from inside that loop
is neither, so it would have propagated out of a plain read and broken
search() for the whole scope -- a data-preservation fix causing a
data-availability outage. The loop checks first and skips the persist.

The gate is evaluated outside ReadSidecarRaw's blanket catch, so a newer
sidecar is never silently dropped from the index. That is the previous
Critical's failure mode and it has its own regression test.
MSG
```

---

## Task 3b: Inheritance on overwrite — without this the bucket preserves nothing

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`BuildDerivedEntry` and its two callers)
- Test: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (append)

**Interfaces:**
- Consumes: `ArtifactEntry.SchemaVersion` / `.Unknown` from Task 2.
- Produces: `BuildDerivedEntry(..., ArtifactEntry? prior)` — a **required** trailing parameter.

**Why this task exists.** Task 2 proves a sidecar round-trips its unknown keys. That is a
round-trip of one *object*, and no ordinary write is one: `remember` re-derives everything and
assembles a brand-new `ArtifactEntry` with an empty bucket and a null version. Without this task,
the first `remember` after the stream ships deletes exactly the fields the stream exists to
preserve — and every Task 2 test still passes. Read spec §B7 before starting.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task ReStoringAMemory_WithEntirelyNewContent_KeepsItsUnknownKeys()
    {
        // NOT a round-trip: StoreAsync re-derives and builds a NEW entry. This is the test that
        // separates a working extension bucket from a decorative one.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        AddUnknownKeyToSidecarOnDisk(scope, "local:notes", "seed", "futureThing");

        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["totally new content"]));

        ReadSidecarJson(scope, "local:notes", "seed").Should().Contain("futureThing");
    }

    [Fact]
    public async Task AppendingToAMemory_KeepsItsUnknownKeys()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        AddUnknownKeyToSidecarOnDisk(scope, "local:notes", "seed", "futureThing");

        await store.AppendAsync(new MemoryWrite("seed", "local:notes", ["second"]));

        ReadSidecarJson(scope, "local:notes", "seed").Should().Contain("futureThing");
    }

    [Fact]
    public async Task ReStoringAV2Memory_IsRefused_BecauseTheRebuiltEntryInheritsItsVersion()
    {
        // This is the mechanism that makes a PER-ENTRY gate sufficient in place of a whole-scope
        // lockout: the refusal follows the memory, because the version is inherited.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        var act = async () => await store.StoreAsync(
            new MemoryWrite("seed", "local:notes", ["overwrite attempt"]));

        await act.Should().ThrowAsync<ScopeReadOnlyException>();
    }

    [Fact]
    public async Task AfterThatRefusal_TheBodyOnDiskIsUnchanged()
    {
        // The property that makes leaving body writes ungated safe: StoreAsync's rollback puts
        // the previous content back, so content and metadata still agree after the refusal.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);
        byte[] before = File.ReadAllBytes(store.ArtifactPath("seed", "local:notes"));

        try { await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["overwrite"])); }
        catch (ScopeReadOnlyException) { }

        File.ReadAllBytes(store.ArtifactPath("seed", "local:notes")).Should().Equal(before);
    }
```

- [ ] **Step 2: Run and watch the first three fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: the unknown-key tests FAIL (the key is gone after the re-store) and the refusal test
FAILS (no refusal — the rebuilt entry has a null version).

- [ ] **Step 3: Thread `prior` through `BuildDerivedEntry`**

```csharp
    private ArtifactEntry BuildDerivedEntry(
        string subject, string scope, long originalBytes, int chunkCount,
        DateTimeOffset createdAt, DateTimeOffset? updatedAt, DerivedMetadata derived,
        ArtifactEntry? prior)
        => AttributeMigration.Forward(new ArtifactEntry(
            /* ...every existing named argument, unchanged... */
            AgentKeywords: derived.AgentKeywords,
            // Carried forward, not re-derived: this build cannot re-derive a field it has no
            // member for, and re-deriving the version would silently downgrade a v2 sidecar to
            // v1 on the first rewrite.
            SchemaVersion: prior?.SchemaVersion)
        {
            // COPIED, never aliased: LoadIndex hands cached entries out BY REFERENCE, so
            // assigning prior.Unknown directly would make this entry and the cached one share
            // one Dictionary, and WriteSidecarCore's sort would then mutate the cache.
            Unknown = prior?.Unknown is { } u ? new Dictionary<string, JsonElement>(u) : null
        });
```

**`prior` takes no default.** An optional parameter lets a future write path omit it and quietly
resume deleting unknown fields; a required one forces every new caller to answer the question.
Same reasoning that kept a `bool sweepOrphans` flag out of the transactional-restore work.

- [ ] **Step 4: Pass `prior` at both call sites**

`FileMemoryStore.cs:1613` (`StoreAsync`) already has `existingEntry` in scope — pass it.
`:1725` (`AppendAsync`) binds `existingEntry` at `:1669` — pass that. Neither needs a new read.

**`AppendAsync`'s may be null, and that is correct.** It gates on the `.nmp2` artifact, not on
the index entry, and tolerates an artifact with no sidecar (`:1674`). `prior` is `ArtifactEntry?`
precisely so that is expressible — an orphaned artifact has no sidecar and therefore no unknown
fields to inherit. Do not "fix" it by loading the index.

- [ ] **Step 5: Verify the `with`-based rebuilds need no change**

`TryUpdateEntry`, the CLI's `consolidate`, and `BundleFormatService.cs:327` rebuild entries with
`with`, which copies every member it does not name — so both new members survive automatically.
**Verify this rather than assuming it**: add one assertion that a `TryUpdateEntry` on a memory
with an unknown key preserves the key.

- [ ] **Step 6: Run the tests, then the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats`
then `dotnet test Scrinia.sln`
Expected: all PASS, 0 failures.

- [ ] **Step 7: Commit**

Commit message subject: `fix(store): carry unknown fields and the version across a rewrite`.
Body must record that the bucket preserves nothing on its own, that no ordinary write is a
round-trip, that `BuildDerivedEntry` is the sole assembler and both callers already hold the
prior entry, and that the parameter is required rather than optional so a future write path
cannot quietly resume deleting.

---

## Task 4: Write refusal, where the entries already are

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`WriteSidecars`, `Upsert`, `WriteSidecarCore`)
- Test: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` (append)

**Interfaces:**
- Consumes: `ScopeReadOnlyException`, `DurableFormats`, `ArtifactSidecarPolicy` from Task 1.
- Produces: nothing new.

**Read spec §B5 first.** An earlier draft of this plan put an `AssertScopeWritableUnsafe` inside
`WithExclusiveLock`. **Do not do that.** It calls `LoadIndexUnsafe`, which (a) re-introduces
finding H5's O(N²) that `Upsert`'s own comment explicitly forbids, (b) is not read-only — it runs
forward-fill sidecar rewrites — and (c) resolves the wrong directory for `PersistMigratedEntry`.
The gate goes where the entries are already in hand, at zero extra I/O.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void WriteSidecars_RefusesAListContainingANewerEntry_BeforeWritingAnyOfIt()
    {
        // The one that matters. A mid-loop refusal would leave a partial write, which is the
        // failure class the transactional-restore work just closed.
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var writable = new ArtifactEntry("writable", "file:///writable.nmp2", 5, 1, DateTimeOffset.UtcNow);
        var newer = writable with { Name = "newer", SchemaVersion = 2 };

        var act = () => store.InvokeWriteSidecarsForTest([writable, newer], scope.TempDir);

        act.Should().Throw<ScopeReadOnlyException>();
        File.Exists(Path.Combine(scope.TempDir, "writable.meta.json"))
            .Should().BeFalse("nothing in the list may be written once one entry is refused");
    }

    [Fact]
    public async Task RememberingAnUnrelatedMemory_IntoTheSameScope_Succeeds()
    {
        // Blast radius: a write refuses when it would TOUCH a newer sidecar, not merely because
        // a sibling is newer. Remembering something unrelated touches nothing.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        var act = async () => await store.StoreAsync(
            new MemoryWrite("unrelated", "local:notes", ["body"]));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Forget_OnANewerMemory_Succeeds()
    {
        // Deletion is allowed: it loses nothing the newer build wrote unexpectedly, and refusing
        // it strands the user until they upgrade.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("seed", "local:notes", ["first"]));
        SetSidecarVersionOnDisk(scope, "local:notes", "seed", 2);

        var act = () => store.Remove("seed", "local:notes");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SearchAndRecall_StillWorkAlongsideANewerMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        await store.StoreAsync(new MemoryWrite("ordinary", "local:notes", ["findable body text"]));
        WriteRawSidecar(scope, "local:notes", "from-the-future", schemaVersion: 2);
        ((FileMemoryStore)store).InvalidateCaches();   // NOT on IMemoryStore — cast required

        store.Invoking(x => x.LoadIndex("local:notes")).Should().NotThrow();
        store.LoadIndex("local:notes").Should().HaveCount(2);
    }

    [Fact]
    public void WriteSidecarCore_RefusesANewerEntry_EvenWhenReachedDirectly()
    {
        // The backstop. After Task 3 and this task it should be unreachable from any enumerated
        // path -- which is exactly why it is worth a test. The previous Critical happened because
        // a trigger was closed and the mechanism behind it was left reachable another way.
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var newer = new ArtifactEntry("direct", "file:///direct.nmp2", 5, 1, DateTimeOffset.UtcNow)
            { SchemaVersion = 2 };

        var act = () => store.InvokeWriteSidecarForTest(newer, scope.TempDir);

        act.Should().Throw<ScopeReadOnlyException>();
    }
```

Add `internal void InvokeWriteSidecarForTest(...)` and `internal void InvokeWriteSidecarsForTest(...)`
to `FileMemoryStore`, matching the existing `InvokeWithExclusiveLockForTest` convention.
`TestHelpers.Store` is typed `IMemoryStore`, so tests calling concrete-only members need a
`(FileMemoryStore)` cast. **`InvalidateCaches()` is one of them** — it is not on the interface,
and calling it uncast is a CS1061 build break. `StoreAsync`, `AppendAsync`, `LoadIndex`,
`Remove` and `ArtifactPath` ARE on the interface.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.SidecarVersioningTests`
Expected: the refusal tests FAIL — no refusal happens.

- [ ] **Step 3: The whole-list pre-check in `WriteSidecars`**

```csharp
    private void WriteSidecars(List<ArtifactEntry> entries, string storeDir)
    {
        // Refuse the WHOLE list before writing any of it. Checking per iteration would refuse
        // mid-loop and leave a partial write -- the exact failure class the transactional-restore
        // work closed. This is O(n) over a list already in hand and reads nothing from disk.
        if (entries.Find(ArtifactSidecarPolicy.IsReadOnly) is { } blocking)
            throw new ScopeReadOnlyException(
                storeDir, blocking.Name, blocking.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        foreach (var entry in entries)
            WriteSidecar(entry, storeDir);
    }
```

- [ ] **Step 3b: The user-facing check at the top of `StoreAsync`**

**Ruling, from the Task 3b review.** `StoreAsync` binds `existingEntry` at `:1588` and calls
`ArchiveVersion` at `:1631`. Without a check between them, a write destined to be refused at
`Upsert` has already deposited a `versions/` archive entry for a memory it never changed, and
relies on the rollback path to restore the body — a rollback that decodes and re-encodes rather
than restoring bytes, so byte-identity becomes a property of the encoder rather than of the
refusal.

Refuse immediately after `existingEntry` is bound, before `ArchiveVersion`:

```csharp
        if (existingEntry is not null && ArtifactSidecarPolicy.IsReadOnly(existingEntry))
            throw new ScopeReadOnlyException(
                scope, existingEntry.Name, existingEntry.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);
```

Same principle as `WriteSidecars`' whole-list pre-check — refuse before the first side effect
rather than partway through a sequence of them — applied at the outermost caller. `Upsert`'s
check (Step 4) stays: it is the backstop for every other route into a sidecar write.

Do the equivalent in `AppendAsync` if it archives too — check, do not assume.

- [ ] **Step 4: The single-entry check in `Upsert`**

Immediately before `Upsert`'s `WriteSidecar(entry, storeDir)` call, refuse when
`ArtifactSidecarPolicy.IsReadOnly(entry)`. **No index read** — the entry is the parameter, and
Task 3b is what puts the inherited version on it.

- [ ] **Step 5: The backstop in `WriteSidecarCore`**

At the top of `WriteSidecarCore`, before the `NormalizeAttributesKey` call:

```csharp
        // Unconditional backstop, deliberately below every caller. After Task 3's forward-fill
        // skip and this task's two gates it should be unreachable from any path we enumerated --
        // and the previous Critical is precisely what happens when a trigger is closed and the
        // mechanism behind it is left reachable by a route nobody listed.
        if (ArtifactSidecarPolicy.IsReadOnly(entry))
            throw new ScopeReadOnlyException(
                storeDir, entry.Name, entry.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);
```

`WriteSidecarCore` is static and takes a directory rather than a scope, so it reports the
directory. `Upsert`'s gate is what produces the memory-named message in the common case.

- [ ] **Step 6: Pre-check `ImportTopicEntries`'s commit loop**

Not optional. That loop (`FileMemoryStore.cs:2523-2527`) renames a body and writes its sidecar
**per entry**, calling `WriteSidecar` directly — so `WriteSidecars`' pre-check does not cover it,
and a refusal from the backstop would abort mid-loop after N bodies had already been renamed.
That is the exact partial-restore failure class the transactional-restore work closed, re-created
by its own successor. Add the same whole-list pre-check over `staged`, **before the first
`File.Move`**.

- [ ] **Step 7: Make `TryUpdateEntry` SKIP rather than throw**

`TryUpdateEntry` (`:1440`) writes a sidecar directly at `:1488`, bypassing `Upsert`, and its
callers are detached background handlers — `MaintenanceEventSink.cs:109,162`,
`EntityTypingEventHandler.cs:82` — where a thrown `ScopeReadOnlyException` has no handler and
becomes an unobserved fault.

**Ruling: derived, best-effort writes SKIP; user-initiated writes REFUSE.** `TryUpdateEntry`
returns `false` and writes nothing when `ArtifactSidecarPolicy.IsReadOnly(updated)`, exactly as
it already does when the entry is missing. Its contract is already "may not have updated"; a
newer sidecar is one more reason. This is the same rule Task 3's forward-fill skip follows.

- [ ] **Step 8: Confirm `Remove` is NOT gated**

`Remove` deletes `.meta.json` files rather than writing them, so it never reaches
`WriteSidecarCore` and needs no change. Verify by reading it — do not assume.

- [ ] **Step 9: Run the tests, then the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats`
then `dotnet test Scrinia.sln`
Expected: 0 failures. A failure in an unrelated write test means a path writes a list containing
an entry it should not — investigate rather than relaxing the gate.

- [ ] **Step 10: Commit**

Commit message subject: `fix(store): refuse writes that would overwrite a sidecar from a newer build`.
Body must record where the gate sits and why it is not inside `WithExclusiveLock` (H5's O(N²),
forward-fill on the write path, wrong directory for `PersistMigratedEntry`), that `WriteSidecars`
refuses the whole list before writing any of it, and that deletion and unrelated writes stay
allowed because the refusal follows the memory rather than the scope.

---

## Task 5: Route `facts.json`, the bundle manifest and the legacy index through the gate

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityFactSidecar.cs`
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs`
- Test: `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs` (create)

**Interfaces:**
- Consumes: `FormatVersion.Evaluate`, `DurableFormats` from Task 1.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Formats;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Formats;

public class RegisteredFormatsTests
{
    [Theory]
    [InlineData(2)]  // older
    [InlineData(4)]  // newer
    [InlineData(0)]  // absent -> deserializes to 0
    public void FactsSidecar_RebuildsOnAnyVersionThatIsNotCurrent(int version)
    {
        // Behaviour must be IDENTICAL to the pre-refactor inline `dto.Version != Version`
        // check. Rebuilding on OLDER is deliberate: this version tracks the derivation rule,
        // so a cache written by a previous build is a path around the new rule.
        string json = $$"""{"version":{{version}},"token":"t","memories":[]}""";
        EntityFactSidecar.TryLoad(json).Should().BeNull();
    }

    [Fact]
    public void FactsSidecar_LoadsAtTheCurrentVersion()
    {
        string json = $$"""
        {"version":{{DurableFormats.EntityFacts.Current}},"token":"t","memories":[]}
        """;
        EntityFactSidecar.TryLoad(json).Should().NotBeNull();
    }

    [Fact]
    public void FactsSidecarVersionConstant_MatchesItsContract() =>
        EntityFactSidecar.Version.Should().Be(DurableFormats.EntityFacts.Current);

    // CanDecode (Nmp2Strategy.cs:205-207) requires BOTH the "NMP/2 " prefix AND an "NMP/END"
    // marker, so a fixture carrying only the header is rejected and would make the true case
    // fail. Build these from a real encode where possible rather than by hand.
    [Theory]
    [InlineData("NMP/2 5B CRC32:00000000\n<payload>\nNMP/END", true)]
    [InlineData("NMP/2 5B CRC32:00000000", false)]
    [InlineData("NMP/1 5B CRC32:00000000\n<payload>\nNMP/END", false)]
    [InlineData("plain text, no sentinel", false)]
    public void Nmp2Parser_MatchesItsDeclaredContract(string header, bool shouldParse)
    {
        // Conformance only. The parser is NOT routed through the gate -- its sentinel check
        // already enforces this and it is a hot, heavily-tested path. This test is what keeps
        // the registry's claim about it honest.
        DurableFormats.Nmp2Body.OnOlder.Should().Be(FormatDisposition.Refuse);
        DurableFormats.Nmp2Body.OnNewer.Should().Be(FormatDisposition.Refuse);
        // assert the parser's accept/reject on `header` matches `shouldParse`
    }
}
```

Fill in the NMP/2 assertion against whatever `Nmp2Strategy`'s public decode entry point is; the point of the test is that the parser's behaviour and the registry's claim about it cannot drift apart.

- [ ] **Step 2: Run and watch the facts tests pass already**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.RegisteredFormatsTests`
Expected: the `facts.json` tests PASS before the refactor. **This is intended** — they are characterization tests pinning current behaviour so Step 3 cannot change it. Do not "fix" them to fail first.

- [ ] **Step 3: Route `EntityFactSidecar.TryLoad` through the gate**

Replace:

```csharp
        if (dto is null || dto.Version != Version) return null;
```

with:

```csharp
        if (dto is null) return null;

        // The rule itself is unchanged; it moves into DurableFormats where it is visible next
        // to every other format's, instead of living inline where nothing else can see it.
        if (FormatVersion.Evaluate(DurableFormats.EntityFacts, dto.Version)
            != FormatDisposition.Load)
            return null;
```

- [ ] **Step 4: Read the bundle manifest's version**

In `BundleFormatService`'s import path, immediately after `var root = manifestDoc.RootElement;` and **before** the topics check at `:169` — so a newer bundle is refused before any validation work and long before any write:

```csharp
        int? declaredVersion = root.TryGetProperty("version", out var versionEl)
            && versionEl.TryGetInt32(out int v) ? v : null;

        if (FormatVersion.Evaluate(DurableFormats.BundleManifest, declaredVersion)
            == FormatDisposition.Refuse)
            throw new InvalidOperationException(
                $"Invalid bundle — manifest declares version {declaredVersion}, but this build " +
                $"of scri supports up to {DurableFormats.BundleManifest.Current}. " +
                "Re-export the bundle with a matching version, or upgrade scri.");
```

- [ ] **Step 5: Refuse a bundle entry from a newer build**

In the per-entry loop, alongside the existing nameless-entry refusal, refuse a `deserialized` entry whose `SchemaVersion` exceeds `DurableFormats.ArtifactSidecar.Current`, with the same "refuse before any write" reasoning the surrounding code already documents.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats`
Expected: all PASS, with the `facts.json` characterization tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Search/EntityFactSidecar.cs src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs
git commit -F - <<'MSG'
refactor(formats): route facts.json and the bundle manifest through the gate

facts.json's behaviour is unchanged by construction, and characterization
tests written BEFORE the refactor pin that.

The bundle manifest starts reading the version it has always written. A
version nothing enforces is worse than no version at all, because the file
looks protected.
MSG
```

---

## Task 6: Skill and agent sidecars

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs`
- Test: `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs` (append)

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: `SkillFileMeta.SchemaVersion` / `.Unknown`, `AgentFileMeta.SchemaVersion` / `.Unknown`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void SkillMeta_PreservesUnknownKeys()
    {
        const string json = """{"role":"reviewer","futureThing":42}""";
        var meta = JsonSerializer.Deserialize(json, ScriniaMcpJsonContext.Default.SkillFileMeta)!;

        meta.Unknown.Should().ContainKey("futureThing");
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.SkillFileMeta)
            .Should().Contain("futureThing");
    }

    [Fact]
    public void SkillMeta_AtV1_EmitsNoVersionKey()
    {
        var meta = new SkillFileMeta(null, "reviewer", null, null, null, null);
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.SkillFileMeta)
            .Should().NotContain("\"v\"");
    }
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats.RegisteredFormatsTests`
Expected: build error — no `Unknown` member.

- [ ] **Step 3: Add the members**

To both `SkillFileMeta` and `AgentFileMeta`, add a trailing `[property: JsonPropertyName("v")] int? SchemaVersion = null` parameter and, in each record's body, the same `{ get; set; }` extension bucket as `ArtifactEntry`:

```csharp
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
```

Same `{ get; set; }` constraint applies — these are positional records too.

- [ ] **Step 4: Gate the read**

In `ReadSidecarMeta<T>`, after a successful deserialize, evaluate the matching contract. `T` is one of two types, so select the contract by `typeof(T)`:

```csharp
        // Two types, two contracts, chosen here rather than passed in so no caller can pair a
        // type with the wrong contract.
        var contract = typeof(T) == typeof(SkillFileMeta)
            ? DurableFormats.SkillMeta
            : DurableFormats.AgentMeta;
```

Refuse to *write* when the disposition is `LoadReadOnly`, mirroring the artifact sidecar. Reads still return the object.

**Do not throw inside `ReadSidecarMeta`.** Unlike `ReadSidecarRaw`'s blanket catch, it catches only `JsonException` (`MemoryTools.Skills.cs:105`) — an `IOException` already escapes it today, so a version throw added there would escape too. Evaluate the disposition after the deserialize returns, exactly as the artifact sidecar does.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Formats`
Expected: all PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Scrinia.sln`
Expected: 0 failures.

- [ ] **Step 7: Update the CHANGELOG**

Add an entry under the unreleased heading describing: version + extension bucket on every durable format; newer sidecars load read-only per topic scope; unknown fields now round-trip; no on-disk change for existing stores. Note explicitly that **no format's version was bumped** — this ships the gate, not a migration.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Skills.cs tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs CHANGELOG.md
git commit -F - <<'MSG'
feat(skills): version and preserve skill and agent sidecars too

These two formats were not in the review's finding at all -- they turned up
by enumerating every durable format instead of sampling the one that was
reported. Seven formats, not one.

Their non-atomic File.WriteAllText is a separate defect, recorded in the
spec's out-of-scope section and deliberately not fixed here: it is an
atomicity change wearing a versioning change's clothes.
MSG
```

---

## Self-Review

**Spec coverage.** §B1 → Task 1. §B2 → Task 1. §B3 → Task 2. §B4 → Task 3. §B7 → Task 3b.
§B5 → Task 4. §B6 → Tasks 5 and 6. §F's facts are folded into Global Constraints and the tasks
that touch them. Verification: D1–2, D15 → Task 1; D3–7 → Task 2; D11, D11a, D12, D13 → Task 3;
D3a–3e → Task 3b; D8, D8a, D8b, D9, D9a, D10 → Task 4; D14 → Task 5. No spec requirement is
unassigned.

**One defect this review caught.** The revision that moved the gate out of `WithExclusiveLock`
left Task 3 producing a `CachedIndex.BlockingEntry` that nothing consumed any more — dead code on
arrival, mandated by the plan. Task 3 no longer touches `CachedIndex` at all.

**Placeholders.** Three steps deliberately leave a body for the implementer, each because the
exact local API is knowledge the brief cannot fix from here, and each naming what the code must
prove: the NMP/2 fixtures in Task 5 Step 1 (build them from a real encode — `CanDecode` needs the
`NMP/END` footer), `AppendAsync`'s real signature in Task 3b Step 1, and the `WriteRawSidecar` /
`SidecarPath` / `SetSidecarVersionOnDisk` / `AddUnknownKeyToSidecarOnDisk` / `ReadSidecarJson` /
`ArtifactPath` helpers, which compose sidecar JSON **by hand** because the typed model cannot
emit a `"v"` key or a legacy `"fields"` key. No other step defers work.

**Type consistency.** `SchemaVersion` is `int?` everywhere. `Unknown` is
`Dictionary<string, JsonElement>?` with `{ get; set; }` in all three records.
`ScopeReadOnlyException`'s four-argument constructor is used identically at Task 4's three call
sites. `BuildDerivedEntry`'s `prior` parameter is `ArtifactEntry?` and required in Task 3b and in
both call sites it names.

**Cross-task dependencies, called out rather than left to be discovered.**
1. Task 1's `ArtifactSidecarPolicy.IsReadOnly` reads `ArtifactEntry.SchemaVersion`, which Task 2
   adds. Task 1 Step 5 gives the resolution: add the member first.
2. Task 4's per-entry gate is only sufficient **because** Task 3b makes a rebuilt entry inherit
   its predecessor's version. Running Task 4 before Task 3b would produce a gate that never fires
   on the case that matters, and a green suite. Task 3b runs first, and its name says why.
