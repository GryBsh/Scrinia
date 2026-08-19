# Bundle Round-Trip Totality Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a bundle export/import round trip preserve every `ArtifactEntry` member by construction, so a backup restore stops silently losing five fields and resetting every memory's creation date.

**Architecture:** Import stops hand-parsing JSON and deserializes through the same source-generated context that export serializes with. Pre-Merge-3 legacy keys move out of the parse and into a small, bounded key-migration pass over the JSON DOM that knows about exactly two keys. A reflective test enumerates `ArtifactEntry`'s members and asserts each survives the round trip, so a member added later cannot be silently dropped.

**Tech Stack:** .NET 10, C#, `System.Text.Json` (source-generated contexts + `System.Text.Json.Nodes` DOM), xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-18-bundle-round-trip-totality-design.md`

**Branch:** `fix/bundle-round-trip-totality`, based on `main` at `403318b`.

## Global Constraints

- .NET 10, C#. Windows host, PowerShell primary shell. Bash is also available.
- `TreatWarningsAsErrors` is on for every project whose name ends in neither `.Tests` nor `.Benchmarks` (`Directory.Build.props:44-46`). A warning in `Scrinia.Core` or `Scrinia` is a build failure.
- `tests/Scrinia.Tests/GlobalUsings.cs` declares only `global using Xunit;`. **Every new test file needs its own `using FluentAssertions;`** — omitting it is the single most common failure in this repo's recent branches.
- `Scrinia.Core` has `InternalsVisibleTo("Scrinia.Tests")` (`src/Scrinia.Core/Properties/AssemblyInfo.cs:5`), so `internal` types are testable directly.
- Baseline at `403318b`: build green, **2670 passed / 0 failed / 4 skipped**.
- `MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault` is a known intermittent flake — do not chase it.
- The LSP in this workspace emits phantom `CS0535`/`CS1503`/`CS0108` errors during large edits while `dotnet build` is green. **Trust the build, not the diagnostic stream.** Verify by reading real bytes and running real commands.
- `.scrinia/` changes are part of your commits.
- `.claude/agent-memory/` is untracked and not yours. Leave the tree clean.
- Build: `dotnet build Scrinia.sln`. Test: `dotnet test Scrinia.sln`. Single project: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~<TestClass>"`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs` **(create)** | Rewrites two pre-Merge-3 JSON keys to the modern shape. Knows nothing else about `ArtifactEntry`. Deletable in one piece when old bundles age out. |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` **(modify)** | Import deserializes through `BundleJsonContext`; the archive layout gets one definition via two internal writers. |
| `src/Scrinia.Core/FileMemoryStore.cs` **(modify, `:2540-2550`)** | `ImportTopicEntries` stops overwriting `CreatedAt`. |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` **(modify, `:211-241`)** | Calls the shared writers instead of duplicating the archive layout. |
| `tests/Scrinia.Tests/BundleLegacyKeysTests.cs` **(create)** | Unit tests for the key migration, in isolation. |
| `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs` **(create)** | The reflective totality pin, plus legacy-bundle, timestamp and derivation round trips. |
| `CHANGELOG.md` **(modify)** | The user-visible behaviour changes. |

---

### Task 1: The legacy key migration, in isolation

A pure function over JSON with no store, no zip and no wiring. It exists so Task 2's rewrite can be a clean swap rather than a rewrite-plus-port.

**Files:**
- Create: `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`
- Test: `tests/Scrinia.Tests/BundleLegacyKeysTests.cs`

**Interfaces:**
- Consumes: `Scrinia.Core.Search.KnownAttributes.TagKey` (the string `"tag"` — do **not** hard-code it).
- Produces: `internal static class BundleLegacyKeys` with `public static void NormalizeIndex(JsonObject index)`. Task 2 calls exactly this.

**Background — what these two keys are.** Bundles written before "Merge 3" carried an entry's display text under `"description"` and its free-form tags as a top-level `"tags"` array. Modern bundles carry `"contentPreview"` and `"attributes": { "tag": [...] }`. The importer being replaced in Task 2 reads both shapes (`BundleFormatService.cs:220-252`); that behaviour must not be lost.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/BundleLegacyKeysTests.cs`:

```csharp
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core.Bundles;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Pins the two pre-Merge-3 bundle keys that <see cref="BundleLegacyKeys"/> migrates. These are
/// the ONLY legacy keys it knows about; every other member of an entry passes through untouched,
/// which is what lets bundle deserialization stay total by construction.
/// </summary>
public sealed class BundleLegacyKeysTests
{
    private static JsonObject IndexWith(JsonObject entry) =>
        new() { ["entries"] = new JsonArray(entry) };

    [Fact]
    public void LegacyDescription_BecomesContentPreview()
    {
        var index = IndexWith(new JsonObject { ["name"] = "a", ["description"] = "old text" });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["contentPreview"]!.GetValue<string>().Should().Be("old text");
        index["entries"]![0]!["description"].Should().BeNull("the legacy key is consumed, not left behind");
    }

    [Fact]
    public void ModernContentPreview_WinsOverLegacyDescription()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["description"] = "old text",
            ["contentPreview"] = "new text",
        });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["contentPreview"]!.GetValue<string>().Should().Be("new text");
    }

    [Fact]
    public void LegacyTags_BecomeTheTagAttribute()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("alpha", "beta"),
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var tag = index["entries"]![0]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("alpha", "beta");
        index["entries"]![0]!["tags"].Should().BeNull();
    }

    [Fact]
    public void ModernTagAttribute_WinsOverLegacyTags()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("legacy"),
            ["attributes"] = new JsonObject { [KnownAttributes.TagKey] = new JsonArray("modern") },
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var tag = index["entries"]![0]!["attributes"]![KnownAttributes.TagKey]!.AsArray();
        tag.Select(t => t!.GetValue<string>()).Should().Equal("modern");
    }

    [Fact]
    public void LegacyTags_FoldIntoAnExistingAttributesObject_WithoutDisturbingOtherKeys()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("alpha"),
            ["attributes"] = new JsonObject { ["topic"] = new JsonArray("retrieval") },
        });

        BundleLegacyKeys.NormalizeIndex(index);

        var attributes = index["entries"]![0]!["attributes"]!.AsObject();
        attributes["topic"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("retrieval");
        attributes[KnownAttributes.TagKey]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("alpha");
    }

    [Fact]
    public void BlankAndNonStringTags_AreDropped_AndAnAllBlankArrayCreatesNoAttribute()
    {
        var index = IndexWith(new JsonObject
        {
            ["name"] = "a",
            ["tags"] = new JsonArray("", "  ", null, 7),
        });

        BundleLegacyKeys.NormalizeIndex(index);

        index["entries"]![0]!["attributes"].Should().BeNull(
            "an all-blank tags array must fold to nothing, not to an empty attributes object");
    }

    [Fact]
    public void AModernEntry_IsLeftByteIdenticalExceptForNothing()
    {
        var entry = new JsonObject
        {
            ["name"] = "a",
            ["contentPreview"] = "text",
            ["agentKeywords"] = new JsonArray("curated"),
            ["codeRefs"] = new JsonObject { ["src/x.cs"] = "deadbeef" },
        };
        var index = IndexWith(entry);
        string before = index.ToJsonString();

        BundleLegacyKeys.NormalizeIndex(index);

        index.ToJsonString().Should().Be(before, "a modern index must pass through untouched");
    }

    [Fact]
    public void AnIndexWithNoEntriesArray_IsANoOp()
    {
        var index = new JsonObject { ["somethingElse"] = 1 };

        Action act = () => BundleLegacyKeys.NormalizeIndex(index);

        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleLegacyKeysTests"`

Expected: FAIL to **compile** — `BundleLegacyKeys` does not exist (`CS0103`/`CS0246`). That is the correct red for this step.

- [ ] **Step 3: Write the implementation**

Create `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`:

```csharp
using System.Text.Json.Nodes;
using Scrinia.Core.Search;

namespace Scrinia.Core.Bundles;

/// <summary>
/// Migrates a bundle index's pre-Merge-3 JSON keys to the modern <see cref="Models.ArtifactEntry"/>
/// shape, so <see cref="BundleFormatService.ImportTopicsFromZip"/> can deserialize through
/// <see cref="BundleJsonContext"/> instead of reading members by hand.
///
/// <para><b>Why this is a separate pass rather than holder properties on the entry.</b> The
/// obvious alternative — <c>[JsonPropertyName]</c> holder properties on <c>ArtifactEntry</c>,
/// following the <c>LegacyFields</c> precedent — was rejected because <c>ArtifactEntry</c> also
/// serializes into every scope's <c>index.json</c>, and these two keys are bundle-only. That
/// shape would push bundle-format history into a type the store shares.</para>
///
/// <para>This class knows about exactly TWO keys and nothing else about an entry. Adding a member
/// to <c>ArtifactEntry</c> requires no change here — that is the whole point. When pre-Merge-3
/// bundles stop mattering, delete this file and its single call site.</para>
///
/// <para>Every migration is conditional on the modern key being ABSENT. A bundle carrying both
/// keeps the modern value and ignores the legacy one; the two are never merged.</para>
/// </summary>
internal static class BundleLegacyKeys
{
    /// <summary>
    /// Rewrites legacy keys on every entry of a bundle topic index, in place. No-op for an index
    /// with no <c>entries</c> array, and for entries that already carry only modern keys.
    /// </summary>
    public static void NormalizeIndex(JsonObject index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (index["entries"] is not JsonArray entries)
            return;

        foreach (JsonNode? entryNode in entries)
        {
            if (entryNode is JsonObject entry)
                NormalizeEntry(entry);
        }
    }

    private static void NormalizeEntry(JsonObject entry)
    {
        // "description" was the display carrier before "contentPreview" existed. DeepClone is
        // required: a JsonNode has one parent, so the value cannot be re-attached in place.
        if (entry["contentPreview"] is null && entry["description"] is JsonNode description)
            entry["contentPreview"] = description.DeepClone();
        entry.Remove("description");

        if (entry["tags"] is JsonArray tags)
        {
            // Matches the hand-parse this replaces: blank entries were dropped, and an empty or
            // all-blank array folded to nothing rather than to an empty attribute. Non-string
            // elements are skipped rather than throwing, which the old JsonElement.GetString()
            // path did not manage.
            var values = new JsonArray();
            foreach (JsonNode? tag in tags)
            {
                if (tag is JsonValue value
                    && value.TryGetValue(out string? text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    values.Add(JsonValue.Create(text));
                }
            }

            if (values.Count > 0)
            {
                if (entry["attributes"] is not JsonObject attributes)
                {
                    attributes = [];
                    entry["attributes"] = attributes;
                }

                attributes[KnownAttributes.TagKey] ??= values;
            }

            entry.Remove("tags");
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleLegacyKeysTests"`

Expected: PASS, 8 tests.

- [ ] **Step 5: Falsify the tests by mutation**

A test you have not seen fail is not evidence. Make this single edit:

```csharp
        if (entry["contentPreview"] is null && entry["description"] is JsonNode description)
```
→
```csharp
        if (entry["description"] is JsonNode description)   // MUTATION: drop the modern-wins guard
```

Re-run. Expected: `ModernContentPreview_WinsOverLegacyDescription` FAILS. **Revert the mutation** and confirm green. Paste both outputs into your report.

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build Scrinia.sln`

Expected: `Build succeeded.` 0 errors. `Scrinia.Core` has `TreatWarningsAsErrors`, so any warning here fails the build.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleLegacyKeys.cs tests/Scrinia.Tests/BundleLegacyKeysTests.cs
git commit -m "feat(core): migrate pre-Merge-3 bundle keys in one bounded pass"
```

---

### Task 2: Import deserializes through the context, and timestamps survive

The heart of the branch. After this task the round trip is total, and the reflective pin proves it.

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:196-291` (the per-topic index read and the hand-parse loop)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:2543-2548` (`ImportTopicEntries`)
- Test: `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs` (create)

**Interfaces:**
- Consumes: `BundleLegacyKeys.NormalizeIndex(JsonObject)` from Task 1.
- Produces: nothing new by name. Task 3 modifies the loop this task rewrites, so its shape matters: the loop body ends with `entries.Add(entry);` and has `artifactContents` populated **before** that point.

**Background the implementer needs:**

- `ArtifactEntry` (`src/Scrinia.Core/Models/ArtifactEntry.cs`) has **13** primary-constructor members. Export writes all 13 through `BundleJsonContext`; the loop you are deleting reads 7.
- `ImportTopicEntries` (`FileMemoryStore.cs:2521`) currently overwrites both `Uri` **and** `CreatedAt`. `Uri` must keep being overwritten — the exporting machine's URI is wrong here. `CreatedAt` must not.
- `FileMemoryStore.CopyMemory` (`:2384-2385`) also sets `CreatedAt = DateTimeOffset.UtcNow`. **That one is correct** — a copy is a new memory. Do not touch it.
- After deserialization, call `ArtifactEntry.NormalizeAttributesKey()` on each entry. The hand-parse never did, so a bundle carrying the pre-rename `"fields"` key loses its attributes today; deserializing through the context picks it up via `LegacyFields` and this seam folds it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs`:

```csharp
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// The load-bearing pin for finding M4. Export serializes <see cref="ArtifactEntry"/> through a
/// source-generated context — every member, automatically — while import used to read members by
/// hand, so the two drifted and five members were silently dropped on every backup restore.
///
/// <para>The field list is NOT the defect; the shape is. So the pin below enumerates
/// <c>ArtifactEntry</c>'s members reflectively rather than naming today's, and a member added
/// later is covered without anyone remembering to add it here.</para>
/// </summary>
public sealed class BundleRoundTripTotalityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleRoundTripTotalityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-bundletotality-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _store = new FileMemoryStore(_workspaceRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Reflection helpers ───────────────────────────────────────────────────

    /// <summary>
    /// ArtifactEntry's primary constructor. Records also synthesize a PROTECTED copy constructor,
    /// which <see cref="Type.GetConstructors()"/> excludes by default — but take the widest one
    /// regardless, so this does not become brittle if that ever changes.
    /// </summary>
    private static ParameterInfo[] EntryMembers() =>
        typeof(ArtifactEntry).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters();

    private static object? Read(ArtifactEntry entry, string member) =>
        typeof(ArtifactEntry).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(entry);

    /// <summary>True when a value is distinguishable from its type's default/empty form.</summary>
    private static bool IsPopulated(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        DateTimeOffset d => d != default,
        long l => l != 0,
        int i => i != 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    // ── The pin ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryArtifactEntryMember_SurvivesABundleRoundTrip()
    {
        // Members the store deliberately recomputes on import. Adding a name here is a DECISION:
        // it asserts "this member is not expected to survive", and needs a justification beside it.
        var recomputedByDesign = new HashSet<string>(StringComparer.Ordinal)
        {
            // ImportTopicEntries rewrites Uri for THIS machine's store root. The exporting
            // machine's URI is wrong here, so preserving it would be the bug.
            nameof(ArtifactEntry.Uri),
        };

        var probe = FullyPopulatedEntry();

        // THE GUARD THAT MAKES THIS TEST BITE. Without it, a member added to ArtifactEntry that
        // the probe never sets is never compared, and the test passes vacuously while the new
        // member is dropped on every restore — the exact failure this pin exists to prevent.
        string[] unpopulated = EntryMembers()
            .Select(p => p.Name!)
            .Where(name => !IsPopulated(Read(probe, name)))
            .ToArray();
        unpopulated.Should().BeEmpty(
            "the probe must set EVERY ArtifactEntry member, or this test cannot detect a dropped " +
            "one. If you just added a member, populate it in FullyPopulatedEntry().");

        var imported = RoundTrip(probe);

        foreach (var member in EntryMembers())
        {
            string name = member.Name!;
            if (recomputedByDesign.Contains(name))
                continue;

            Read(imported, name).Should().BeEquivalentTo(
                Read(probe, name),
                $"ArtifactEntry.{name} must survive a bundle round trip");
        }
    }

    [Fact]
    public void Uri_IsRecomputedForTheImportingStore_NotCarriedFromTheBundle()
    {
        var imported = RoundTrip(FullyPopulatedEntry());

        imported.Uri.Should().NotBe("file:///somewhere/else/probe.nmp2");
        imported.Uri.Should().NotBeEmpty("the store rewrites Uri for its own root, it does not blank it");
    }

    // ── Legacy bundles ───────────────────────────────────────────────────────

    [Fact]
    public void ALegacyBundle_ImportsItsDescriptionTagsAndFieldsKeys()
    {
        // A hand-built pre-Merge-3 index: display text under "description", free-form "tags", and
        // attributes under the pre-rename "fields" key.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "legacy-probe",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["createdAt"] = "2024-03-04T05:06:07+00:00",
                ["description"] = "legacy display text",
                ["tags"] = new JsonArray("alpha"),
                ["fields"] = new JsonObject { ["topic"] = new JsonArray("retrieval") },
            }),
        };

        var imported = ImportHandBuiltBundle("legacy-topic", index, "hello world");

        imported.ContentPreview.Should().Be("legacy display text");
        imported.Attributes.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().Equal("alpha");
        imported.Attributes.Should().ContainKey("topic")
            .WhoseValue.Should().Equal("retrieval");
    }

    // ── Timestamps ───────────────────────────────────────────────────────────

    [Fact]
    public void CreatedAtAndUpdatedAt_ArePreservedFromTheBundle()
    {
        var created = new DateTimeOffset(2023, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updated = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.Zero);

        var imported = RoundTrip(FullyPopulatedEntry() with { CreatedAt = created, UpdatedAt = updated });

        imported.CreatedAt.Should().Be(created, "a restore reinstates the memory as it was");
        imported.UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public void AnEntryWithNoCreatedAt_FallsBackToNow()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-timestamp",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
            }),
        };

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var imported = ImportHandBuiltBundle("stamp-topic", index, "hello world");

        imported.CreatedAt.Should().BeOnOrAfter(before,
            "a bundle carrying no timestamp gets one now rather than the year 1");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An entry with EVERY member set to a distinctive non-default value. When you add a member to
    /// <see cref="ArtifactEntry"/>, add it here — the guard in the pin will tell you if you forget.
    /// </summary>
    private static ArtifactEntry FullyPopulatedEntry() => new(
        Name: "probe",
        Uri: "file:///somewhere/else/probe.nmp2",
        OriginalBytes: 4321,
        ChunkCount: 2,
        CreatedAt: new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero),
        ContentPreview: "a distinctive preview",
        Keywords: ["alpha", "beta"],
        TermFrequencies: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = 3 },
        UpdatedAt: new DateTimeOffset(2022, 3, 4, 5, 6, 7, TimeSpan.Zero),
        ChunkEntries:
        [
            new ChunkEntry(0, "chunk zero preview", ["alpha"],
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = 1 }),
        ],
        FileHashes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["src/x.cs"] = "deadbeef" },
        Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["topic"] = ["retrieval"] },
        AgentKeywords: ["alpha"]);

    /// <summary>Seeds one entry, exports it to a zip, imports it into a fresh scope, reads it back.</summary>
    private ArtifactEntry RoundTrip(ArtifactEntry seed)
    {
        string sourceScope = MemoryNaming.BuildScopedTopicScope("totality-source");
        _store.Upsert(seed, sourceScope);
        _store.WriteArtifactAsync(seed.Name, sourceScope, Nmp2ChunkedEncoder.Encode("hello world"))
            .GetAwaiter().GetResult();

        string zipPath = Path.Combine(_tempDir, $"totality-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _store, ["totality-source"]);

        // Import into a SECOND workspace, so nothing survives by having never left.
        string otherRoot = Path.Combine(_tempDir, "target-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherRoot);
        using var target = new FileMemoryStore(otherRoot);

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, target, filterTopics: null, overwrite: true);

        return target.LoadIndex(MemoryNaming.BuildScopedTopicScope("totality-source"))
            .Single(e => e.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds a bundle zip from a hand-written index — the only way to test shapes this
    /// codebase no longer exports — imports it, and returns the single entry.</summary>
    private ArtifactEntry ImportHandBuiltBundle(string topic, JsonObject index, string content)
    {
        string entryName = index["entries"]![0]!["name"]!.GetValue<string>();
        string zipPath = Path.Combine(_tempDir, $"handbuilt-{Guid.NewGuid():N}.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry($"topics/{topic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(index.ToJsonString());

            var artifactEntry = zip.CreateEntry($"topics/{topic}/{_store.SanitizeName(entryName)}.nmp2");
            using (var writer = new StreamWriter(artifactEntry.Open()))
                writer.Write(Nmp2ChunkedEncoder.Encode(content));

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray(topic),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        return _store.LoadIndex(MemoryNaming.BuildScopedTopicScope(topic))
            .Single(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail — and record WHICH members fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleRoundTripTotalityTests"`

Expected FAIL, and the failure message names the dropped members. **Copy the exact list into your report** — it is the measured evidence for the branch, and it should read `Keywords`, `TermFrequencies`, `UpdatedAt`, `FileHashes`, `AgentKeywords`, plus `CreatedAt` from the timestamp test.

If a member you did not expect appears, stop and report it before implementing — the spec's count would be wrong and that matters more than the fix.

- [ ] **Step 3: Replace the hand-parse in `ImportTopicsFromZip`**

In `src/Scrinia.Core/Bundles/BundleFormatService.cs`, add `using System.Text.Json.Nodes;` to the file's usings.

Replace everything from `using var indexDoc = JsonDocument.Parse(indexJson);` (`:204`) through the closing brace of the `foreach (var entryEl in ...)` loop — **`:300`**, which is the second of two consecutive `}` lines; `:299` closes the inner `if (artifactZipEntry is not null)`. The next non-blank line after your replacement must be `if (entries.Count > 0)` at `:302`. Replace with:

```csharp
            // Deserialize through the SAME source-generated context that ExportTopicsToZip
            // serializes with, so the round trip is total by construction. This replaced a
            // hand-written member-by-member parse that read 7 of ArtifactEntry's 13 members and
            // silently dropped the rest on every backup restore (design review finding M4).
            // Legacy keys are migrated first, in one bounded pass that knows about exactly two
            // of them — see BundleLegacyKeys for why they do not live on the entry itself.
            if (JsonNode.Parse(indexJson) is not JsonObject indexObject)
                continue;

            BundleLegacyKeys.NormalizeIndex(indexObject);

            var index = indexObject.Deserialize(BundleJsonContext.Default.BundleIndex);
            if (index?.Entries is not { Count: > 0 })
                continue;

            var entries = new List<ArtifactEntry>(index.Entries.Count);
            var artifactContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var deserialized in index.Entries)
            {
                // A nameless entry is unaddressable. The parse this replaced threw for a missing
                // "name" key and skipped a null one; skipping both is strictly kinder and loses
                // nothing, since neither could be stored.
                if (string.IsNullOrEmpty(deserialized.Name))
                    continue;

                // Folds a pre-rename "fields" key into Attributes and clears the holder. The old
                // hand-parse never did this, so such a bundle lost its attributes entirely.
                var entry = deserialized.NormalizeAttributesKey();

                string artifactEntryName = $"topics/{bundleTopic}/{store.SanitizeName(entry.Name)}.nmp2";
                var artifactZipEntry = zip.GetEntry(artifactEntryName);
                if (artifactZipEntry is not null)
                {
                    using var reader = new StreamReader(artifactZipEntry.Open());
                    artifactContents[entry.Name] = reader.ReadToEnd();
                }

                entries.Add(entry);
            }
```

Leave the `if (entries.Count > 0) { store.ImportTopicEntries(...); ... }` block that follows exactly as it is.

**After editing, verify by reading real bytes** that no fragment of the old loop survives:

```bash
grep -n 'entryEl\|legacyTags\|TryGetProperty("contentPreview"\|TryGetProperty("tags"' src/Scrinia.Core/Bundles/BundleFormatService.cs
```

Expected: no output. The manifest read above still uses `JsonDocument`/`TryGetProperty` for `"topics"` — that is correct and stays; the grep pattern above is written not to match it.

- [ ] **Step 4: Stop overwriting `CreatedAt` on import**

In `src/Scrinia.Core/FileMemoryStore.cs`, in `ImportTopicEntries` (`:2521`), replace:

```csharp
            var updatedEntry = entry with
            {
                Uri = ArtifactUri(entry.Name, topicScope),
                CreatedAt = DateTimeOffset.UtcNow
            };
```

with:

```csharp
            // Uri is recomputed deliberately: it encodes THIS store's root, so the exporting
            // machine's value is wrong here. CreatedAt is NOT — a restore reinstates the memory
            // as it was, and overwriting it reset every memory's creation date on every restore
            // (design review finding M4). A bundle carrying no timestamp deserializes to
            // default(DateTimeOffset), which is the only case that still gets "now".
            var updatedEntry = entry with
            {
                Uri = ArtifactUri(entry.Name, topicScope),
                CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
            };
```

Do **not** touch `CopyMemory` at `:2384-2385`; a copy is a new memory and resetting there is correct.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleRoundTripTotalityTests"`

Expected: PASS, 5 tests.

- [ ] **Step 6: Falsify the pin by mutation**

Two mutations, run one at a time, reverting between:

1. In `BundleFormatService.ExportTopicsToZip`, wrap the serialized entries so one member is stripped — change `new BundleIndex(entries.ToList())` to `new BundleIndex(entries.Select(e => e with { AgentKeywords = null }).ToList())`.
   Expected: `EveryArtifactEntryMember_SurvivesABundleRoundTrip` FAILS naming `AgentKeywords`.
2. In `ImportTopicEntries`, restore `CreatedAt = DateTimeOffset.UtcNow`.
   Expected: `CreatedAtAndUpdatedAt_ArePreservedFromTheBundle` FAILS.

Then falsify **the guard**, which is what makes the pin bite for a *future* member — comment out the `Keywords: [...]` argument in `FullyPopulatedEntry()`:
   Expected: the `unpopulated.Should().BeEmpty(...)` assertion FAILS naming `Keywords`, **not** a silent pass.

Revert all three. Paste each output into your report.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test Scrinia.sln`

Expected: 0 failed. Baseline was 2670 passed / 4 skipped; you have added 13 tests across Tasks 1 and 2. Existing bundle tests (`BundleV2Tests`, `BundleEndpointTests`, `BundleImportCategoryAllowlistTests`) must all still pass — if one fails, it is telling you something real about the rewrite, not something to adjust.

- [ ] **Step 8: Verify trim safety with a real publish**

The spec makes this a gate, not a formality: `System.Text.Json.Nodes` is expected to be trim-safe, but expectation is not evidence.

Run: `pwsh -File publish.ps1 -OutputDir artifacts/trimcheck -Platform win-x64`

Expected: succeeds. It publishes with `PublishTrimmed=true`, `SuppressTrimAnalysisWarnings=false` and `TreatWarningsAsErrors=true` (`publish.ps1:34-37`), so a trim warning fails it.

If it **does** warn on `JsonNode`, stop and report rather than working around it — the fallback is the holder-property shape the spec rejected, and that is a spec change, not an implementation detail.

Delete `artifacts/trimcheck` afterwards and confirm `git status` is clean.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs
git commit -m "fix(core)!: bundle import preserves every entry member and its timestamps"
```

The `!` is deliberate — restored memories now carry different metadata than before. Task 5 writes the CHANGELOG entry.

---

### Task 3: Derivation fills only what the bundle omits

`scri bundle` packages loose files that were never memories, so its entries carry no `Keywords` and no `TermFrequencies`, and import derives nothing — those memories arrive unsearchable by keyword. Round-tripping restores what cannot be re-derived; deriving supplies what was never there. The two cover disjoint gaps.

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs` (the loop Task 2 wrote, plus one private helper)
- Test: `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs` (add to the file Task 2 created)

**Interfaces:**
- Consumes: the loop shape from Task 2 — `artifactContents[entry.Name]` is populated before `entries.Add(entry)`.
- Consumes: `MemoryDerivation.ForStore(string[] content, string? description = null, IReadOnlyDictionary<string, string[]>? attributes = null, string[]? keywords = null, bool isEphemeral = false, Func<string, string?>? fileHasher = null)` returning `DerivedMetadata(string[] Keywords, Dictionary<string, int> TermFrequencies, Dictionary<string, string[]>? Attributes, ChunkEntry[]? ChunkEntries, string ContentPreview, Dictionary<string, string>? FileHashes = null, string[]? AgentKeywords = null)`.
- Consumes: `Nmp2ChunkedEncoder.GetChunkCount(string artifact)` and `Nmp2ChunkedEncoder.DecodeChunk(string artifact, int chunkIndex)` — **`chunkIndex` is 1-BASED** and throws `ArgumentOutOfRangeException` outside `[1, ChunkCount]`.

- [ ] **Step 1: Write the failing tests**

Append these to `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs`, inside the class, above the `// ── Fixtures ──` region:

```csharp
    // ── Derivation ───────────────────────────────────────────────────────────

    [Fact]
    public void AnEntryWithNoKeywords_HasThemDerivedFromItsContent()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "underived",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
                ["contentPreview"] = "a preview",
            }),
        };

        var imported = ImportHandBuiltBundle(
            "derive-topic", index, "kubernetes scheduling latency investigation");

        imported.Keywords.Should().NotBeNullOrEmpty(
            "a bundle of loose files carries no keywords, so import must derive them or the " +
            "memory is unsearchable");
        imported.Keywords.Should().Contain(k => k.Contains("kubernetes", StringComparison.OrdinalIgnoreCase));
        imported.TermFrequencies.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnEntryThatAlreadyCarriesKeywords_KeepsExactlyThose()
    {
        var imported = RoundTrip(FullyPopulatedEntry());

        imported.Keywords.Should().Equal("alpha", "beta",
            "derivation must not run over an entry the bundle already derived");
    }

    [Fact]
    public void AnEmptyKeywordsArray_IsARealAnswer_AndIsNotOverwritten()
    {
        // Null means "never derived"; empty means "derived, found nothing". Gating on
        // `is not { Length: > 0 }` instead of `is null` would silently conflate the two.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "empty-keywords",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
                ["keywords"] = new JsonArray(),
            }),
        };

        var imported = ImportHandBuiltBundle(
            "empty-topic", index, "kubernetes scheduling latency investigation");

        imported.Keywords.Should().BeEmpty(
            "an empty keywords array is a derived answer, not a missing one");
    }

    [Fact]
    public void DerivationDoesNotFabricateADriftBaselineFromThisMachinesFiles()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-hashes",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
            }),
        };

        var imported = ImportHandBuiltBundle(
            "hash-topic", index, "see src/Scrinia.Core/FileMemoryStore.cs for details");

        imported.FileHashes.Should().BeNullOrEmpty(
            "hashing THIS machine's files against paths captured on the exporting machine would " +
            "fabricate a drift baseline that never existed");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleRoundTripTotalityTests"`

Expected: `AnEntryWithNoKeywords_HasThemDerivedFromItsContent` FAILS (keywords null). The other three should already PASS — they pin behaviour Task 2 established or that must not change, and their job is to fail if Step 3 overreaches.

- [ ] **Step 3: Derive into the gaps**

In `src/Scrinia.Core/Bundles/BundleFormatService.cs`, add `using Scrinia.Core.Encoding;` if it is not already present (check first — `Nmp2ChunkedEncoder` lives in `src/Scrinia.Core/Encoding/`).

In the loop Task 2 wrote, insert this immediately **after** the `artifactContents[entry.Name] = reader.ReadToEnd();` block and **before** `entries.Add(entry);`:

```csharp
                // A null Keywords means this entry was never derived — `scri bundle` packages
                // loose files that were never memories, so nothing ever ran over them. An EMPTY
                // Keywords array is a different fact: derivation ran and found nothing, and must
                // be left alone. Gating on `is null` rather than on length is the whole of that
                // distinction.
                //
                // Round-tripping and deriving cover disjoint gaps. Round-tripping restores what
                // CANNOT be re-derived (AgentKeywords, FileHashes, timestamps); this fills what
                // was never there. Attributes are never rewritten here — the bundle's are
                // authoritative and round-trip intact.
                if (entry.Keywords is null && artifactContents.TryGetValue(entry.Name, out string? artifact))
                {
                    // fileHasher is null deliberately: hashing this machine's files against paths
                    // captured on the exporting machine would fabricate a drift baseline that
                    // never existed. A bundle's own FileHashes is preserved as-is above.
                    var derived = MemoryDerivation.ForStore(
                        DecodeChunks(artifact),
                        keywords: entry.AgentKeywords,
                        fileHasher: null);

                    entry = entry with
                    {
                        Keywords = derived.Keywords,
                        TermFrequencies = derived.TermFrequencies,
                        ChunkEntries = entry.ChunkEntries ?? derived.ChunkEntries,
                        AgentKeywords = entry.AgentKeywords ?? derived.AgentKeywords,
                    };
                }
```

Add this private helper to `BundleFormatService`, beside the other private helpers below the `// ── File entity helpers ──` divider:

```csharp
    /// <summary>
    /// Decodes an NMP/2 artifact to its chunk texts, one element per independently retrievable
    /// chunk — the shape <see cref="MemoryDerivation.ForStore"/> expects.
    /// </summary>
    private static string[] DecodeChunks(string artifact)
    {
        int count = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        var chunks = new string[count];
        for (int i = 0; i < count; i++)
            chunks[i] = Nmp2ChunkedEncoder.DecodeChunk(artifact, i + 1);  // DecodeChunk is 1-based
        return chunks;
    }
```

**`ContentPreview` is deliberately not filled here**, even when absent: both `scri bundle` and every store write always set it, so there is no real gap, and adding a fourth derived field the spec does not list is the drift this branch exists to stop.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleRoundTripTotalityTests"`

Expected: PASS, 9 tests.

- [ ] **Step 5: Falsify by mutation**

Change the gate from `entry.Keywords is null` to `entry.Keywords is not { Length: > 0 }`.

Expected: `AnEmptyKeywordsArray_IsARealAnswer_AndIsNotOverwritten` FAILS. This is the mutation that matters — it is the mistake an implementer is most likely to make, and the test exists precisely to catch it. Revert and confirm green. Paste the output.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test Scrinia.sln` — expected 0 failed.

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs
git commit -m "fix(core): import derives keywords only for entries that never had them"
```

---

### Task 4: One archive writer

The archive layout is written twice — once by `BundleFormatService.ExportTopicsToZip`, once by `ScriniaCommands.Bundle` — so a change to the layout has to be made in two places and nothing detects a miss.

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs` — `ExportTopicsToZip`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:211-241`

**Line numbers in this task are anchors, not addresses.** Tasks 2 and 3 both edit `BundleFormatService.cs`, so every line number in it has moved by the time you get here. Locate the code by the comment text quoted below, and confirm you have the right block by reading it before you replace it.

**Interfaces:**
- Produces: two `internal static` methods on `BundleFormatService`:
  - `void WriteTopic(ZipArchive zip, IMemoryStore store, string bundleTopic, IReadOnlyList<ArtifactEntry> entries, IEnumerable<(string Name, string Content)> artifacts)`
  - `void WriteManifest(ZipArchive zip, List<string> topics, int totalEntries, Dictionary<string, List<string>>? fileEntities)`
- `Scrinia` (the CLI assembly, named `scri`) already has `InternalsVisibleTo` from `Scrinia.Core` (`AssemblyInfo.cs:3`), so `internal` is reachable from `ScriniaCommands.Bundle`.

**Why two methods and not one:** the index write happens once per topic, the manifest once per archive, and the two callers differ on both axes — the CLI has one topic and never any file entities, while `ExportTopicsToZip` has many topics and may have both. A single combined method needs a parameter shape that fits neither.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs`, inside the class:

```csharp
    // ── Layout identity ──────────────────────────────────────────────────────

    [Fact]
    public void BothWriters_ProduceTheSameArchiveLayout()
    {
        // The CLI used to duplicate the archive layout inline. This pins that whatever writes a
        // bundle, an importer finds the same three things in the same places.
        var seed = FullyPopulatedEntry();
        string scope = MemoryNaming.BuildScopedTopicScope("layout-topic");
        _store.Upsert(seed, scope);
        _store.WriteArtifactAsync(seed.Name, scope, Nmp2ChunkedEncoder.Encode("hello world"))
            .GetAwaiter().GetResult();

        string zipPath = Path.Combine(_tempDir, "layout.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _store, ["layout-topic"]);

        using var read = new ZipArchive(new FileStream(zipPath, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
        read.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
        [
            "topics/layout-topic/index.json",
            $"topics/layout-topic/{_store.SanitizeName(seed.Name)}.nmp2",
            "manifest.json",
        ]);
    }
```

- [ ] **Step 2: Run it to verify it passes already**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "BothWriters_ProduceTheSameArchiveLayout"`

Expected: PASS. This one is a **characterization test** — it captures today's layout so the refactor in Step 3 cannot silently change it. A refactor's test is allowed to be green before the change; that is what makes it a safety net rather than a spec.

- [ ] **Step 3: Extract the two writers**

In `src/Scrinia.Core/Bundles/BundleFormatService.cs`, add these two methods to the class:

```csharp
    /// <summary>
    /// Writes one topic's <c>index.json</c> and its <c>.nmp2</c> artifact entries into an open
    /// archive. The single definition of the topic half of the bundle layout — both
    /// <see cref="ExportTopicsToZip"/> (which reads from a store) and the CLI's <c>scri bundle</c>
    /// (which reads loose files that were never memories) call it, so the two cannot drift.
    /// </summary>
    /// <param name="artifacts">Entry name → artifact text. Enumerated lazily, so a caller
    /// streaming from disk does not have to hold every artifact in memory at once.</param>
    internal static void WriteTopic(
        ZipArchive zip,
        IMemoryStore store,
        string bundleTopic,
        IReadOnlyList<ArtifactEntry> entries,
        IEnumerable<(string Name, string Content)> artifacts)
    {
        string indexJson = JsonSerializer.Serialize(
            new BundleIndex(entries.ToList()), BundleJsonContext.Default.BundleIndex);
        var indexEntry = zip.CreateEntry($"topics/{bundleTopic}/index.json");
        using (var writer = new StreamWriter(indexEntry.Open()))
            writer.Write(indexJson);

        foreach (var (name, content) in artifacts)
        {
            var zipEntry = zip.CreateEntry($"topics/{bundleTopic}/{store.SanitizeName(name)}.nmp2");
            using var writer = new StreamWriter(zipEntry.Open());
            writer.Write(content);
        }
    }

    /// <summary>
    /// Writes the archive manifest, including the version rule: v2 when the bundle carries
    /// disk-based file entities, v1 otherwise. The single definition of that rule — the CLI used
    /// to hard-code a literal 1, which happened to be right for what it writes and would have
    /// stopped being right the moment it gained file entities.
    /// </summary>
    internal static void WriteManifest(
        ZipArchive zip,
        List<string> topics,
        int totalEntries,
        Dictionary<string, List<string>>? fileEntities)
    {
        bool hasFileEntities = fileEntities is { Count: > 0 };
        var manifest = new BundleManifest(
            hasFileEntities ? 2 : 1,
            DateTimeOffset.UtcNow.ToString("o"),
            topics,
            totalEntries,
            hasFileEntities ? fileEntities : null);

        string manifestJson = JsonSerializer.Serialize(manifest, BundleJsonContext.Default.BundleManifest);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(manifestJson);
    }
```

Then in `ExportTopicsToZip`, replace the index write (the block whose comment begins *"Write index.json for this topic"*) and the artifact-file loop that follows it (*"Write artifact files"*) with:

```csharp
            WriteTopic(zip, store, bundleTopic, entries,
                artifacts.Select(a => (a.Name, File.ReadAllText(a.FilePath))));
            totalEntries += artifacts.Count;
```

`entries` is `store.LoadIndex(topicScope)`, an `IReadOnlyList<ArtifactEntry>` that is a **live view of the index cache** — the deleted code called `.ToList()` on it for that reason, and `WriteTopic` now does that internally when it builds the `BundleIndex`. Do not add a second `.ToList()` here.

and replace the manifest block (from the comment *"Write manifest — use v2 when file entities are present"* through the `using` block that writes it) with:

```csharp
        // Export disk-based file entities (skills, agent)
        var fileEntities = ExportFileEntities(zip, store);
        WriteManifest(zip, exportedTopics, totalEntries, fileEntities);

        return (exportedTopics, totalEntries);
```

**Watch the entry count.** The loop being replaced incremented `totalEntries` once per artifact *file written*, not once per index entry. `artifacts.Count` preserves that exactly. Do not switch it to `entries.Count` — an entry with no artifact file on disk would change the reported total.

- [ ] **Step 4: Point the CLI at the shared writers**

In `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, replace the whole `using (var stream = ...) using (var zip = ...) { ... }` block at `:211-241` with:

```csharp
        // Create the bundle zip. The archive layout has ONE definition, in BundleFormatService —
        // this command used to duplicate it inline, along with a hard-coded manifest version.
        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Scrinia.Core.Bundles.BundleFormatService.WriteTopic(
                zip, WorkspaceSetup.Store, sanitizedTopic, entries,
                artifactContents.Select(kv => (kv.Key, kv.Value)));

            Scrinia.Core.Bundles.BundleFormatService.WriteManifest(
                zip, [sanitizedTopic], entries.Count, fileEntities: null);
        }
```

- [ ] **Step 5: Verify no layout code survives in the CLI**

```bash
grep -n 'CreateEntry\|BundleIndex\|BundleManifest' src/Scrinia/Commands/ScriniaCommands.Bundle.cs
```

Expected: only the two `BundleFormatService.Write*` calls. Any remaining `zip.CreateEntry` in that file means the duplication is still there.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Scrinia.sln`

Expected: 0 failed. `BothWriters_ProduceTheSameArchiveLayout`, `BundleV2Tests` and `BundleExitCodeTests` are the ones that would catch a layout change — if any goes red, the refactor changed behaviour and must be corrected rather than the test adjusted.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs
git commit -m "refactor(core): one definition of the bundle archive layout"
```

---

### Task 5: Close the branch

**Files:**
- Modify: `CHANGELOG.md`
- Verify: everything

- [ ] **Step 1: Verify every breaking commit has an entry**

```bash
git log --oneline main..HEAD
git log --oneline main..HEAD --grep='!:'
```

Task 2's commit carries `!`. Confirm each `!` commit maps to a CHANGELOG entry you write below.

- [ ] **Step 2: Write the CHANGELOG entry**

Add under the unreleased heading, matching the file's existing format (read the top 40 lines first and follow it exactly):

```markdown
### Fixed

- **Bundle import no longer drops entry metadata.** Export serialized every `ArtifactEntry`
  member through a source-generated context while import re-read seven of them by hand, so a
  backup restore silently lost `Keywords`, `TermFrequencies`, `UpdatedAt`, `FileHashes` and
  `AgentKeywords`. `AgentKeywords` was the unrecoverable one — the curated-vs-auto keyword
  distinction cannot be reconstructed after the fact. Import now deserializes through the same
  context that serializes, so the round trip is total by construction.
- **Restoring a backup no longer resets every memory's creation date.** `CreatedAt` was parsed
  from the bundle and then overwritten with the import time. Both `CreatedAt` and `UpdatedAt`
  are now preserved; a bundle carrying no timestamp still gets one at import.
- **Memories bundled from loose files are searchable on import.** `scri bundle` packages files
  that were never memories, so its entries carry no keywords and import derived none. Import now
  derives keywords and term frequencies for entries that have none — and only for those, leaving
  an entry that was already derived exactly as the bundle recorded it.

### Changed

- **BREAKING (behaviour):** a memory restored from a bundle now carries the metadata the bundle
  recorded rather than a partially-reconstructed copy. Anything depending on restored memories
  having a fresh `CreatedAt`, or on their keyword lists being empty, will observe the difference.
  No API signature changed; no caller needs recompiling.
```

- [ ] **Step 3: Full verification**

```bash
dotnet build Scrinia.sln
dotnet test Scrinia.sln
git status --short
```

Expected: `Build succeeded.` 0 errors; 0 failed tests; the only untracked paths are `.claude/agent-memory/` directories, which are not yours.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: record the bundle round-trip fixes"
```

---

## Verification summary

At branch close, all of the following must be true and **each must have been observed, not assumed**:

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test Scrinia.sln` — 0 failed. Baseline 2670 passed / 4 skipped, plus 18 new tests.
3. The reflective pin fails when any member is stripped from the writer (Task 2, Step 6).
4. The pin's **guard** fails when the probe stops populating a member — this is what covers members added in future (Task 2, Step 6).
5. The derivation gate fails when changed from `is null` to a length check (Task 3, Step 5).
6. A trimmed publish succeeds with trim warnings as errors (Task 2, Step 8).
7. `grep` confirms no fragment of the old hand-parse survives, and no archive-layout code remains in the CLI.

Anything you could not verify goes into your report as **CANNOT-VERIFY**, named explicitly. Do not report a step as done on the strength of expecting it to work.
