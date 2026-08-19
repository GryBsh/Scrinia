using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
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
    /// ArtifactEntry's primary constructor. A SEALED record's synthesized copy constructor is
    /// private, so <see cref="Type.GetConstructors()"/> excludes it — but take the widest one
    /// regardless, so this does not become brittle if the record ever stops being sealed.
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

        // The AssertionScope is load-bearing for diagnosis, not just tidiness. Without it
        // FluentAssertions throws on the FIRST failed member and the run names only that one —
        // which, by primary-constructor order, is CreatedAt. You would see one name and have no
        // idea the other five were also dropped.
        using (new AssertionScope())
        {
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
    }

    [Fact]
    public void Uri_IsRecomputedForTheImportingStore_NotCarriedFromTheBundle()
    {
        var imported = RoundTrip(FullyPopulatedEntry());

        imported.Uri.Should().NotBe("file:///somewhere/else/probe.nmp2");
        imported.Uri.Should().NotBeEmpty("the store rewrites Uri for its own root, it does not blank it");
    }

    // ── Legacy bundles ───────────────────────────────────────────────────────

    // The two legacy attribute carriers are NEVER merged, and these are two tests rather than one
    // because of it. `ArtifactEntry.NormalizeAttributesKey` is a fallback, not a merge —
    // `Attributes ?? _legacyFields` (ArtifactEntry.cs:85-88) — so once the "tags" migration has
    // created an Attributes object, a "fields" payload on the same entry is discarded whole. That
    // matches today's behaviour (the hand-parse never read "fields" at all), so it is not a
    // regression, but a single fixture carrying both keys would assert something that is false.

    [Fact]
    public void ALegacyBundle_ImportsItsDescriptionAndTagsKeys()
    {
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
            }),
        };

        var imported = ImportHandBuiltBundle("legacy-topic", index, "hello world");

        imported.ContentPreview.Should().Be("legacy display text");
        imported.Attributes.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().Equal("alpha");
    }

    [Fact]
    public void ALegacyBundle_WithNoTags_PicksUpThePreRenameFieldsKey()
    {
        // "fields" is folded ONLY when nothing else produced an Attributes object. Deserializing
        // through the context is what picks it up at all — the hand-parse this replaces read
        // "attributes" only, so such a bundle lost its attributes entirely.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "fields-probe",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["fields"] = new JsonObject { ["topic"] = new JsonArray("retrieval") },
            }),
        };

        var imported = ImportHandBuiltBundle("fields-topic", index, "hello world");

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

    [Fact]
    public void AnEntryWithNoChunkCount_DefaultsToOne_NotZero()
    {
        // The hand-parse this replaces defaulted a missing "chunkCount" to 1; source-generated
        // deserialization leaves it at 0. Only pre-Merge-3 and hand-crafted bundles omit the key,
        // but a ChunkCount of 0 is not a value any reader is prepared for.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-chunk-count",
                ["originalBytes"] = 11,
            }),
        };

        var imported = ImportHandBuiltBundle("chunk-topic", index, "hello world");

        imported.ChunkCount.Should().Be(1);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An entry with EVERY member set to a distinctive non-default value. When you add a member to
    /// <see cref="ArtifactEntry"/>, add it here — the guard in the pin will tell you if you forget.
    ///
    /// <para>Keep the collection values in ASCII-ascending order. <c>FileMemoryStore.WriteSidecarCore</c>
    /// sorts Keywords, TermFrequencies, FileHashes and each ChunkEntry's keywords on the way to
    /// disk, so an order-sensitive assertion downstream survives only because the values here are
    /// already sorted. Making them "more distinctive" without re-sorting produces a baffling red.</para>
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
        // Attributes["file"] MUST mirror FileHashes.Keys. That is a store invariant, not a
        // stylistic choice: MemoryDerivation.ForStore derives FileHashes from the `file`
        // attribute, and AttributeMigration.Forward re-derives the attribute from the hash keys
        // on every cold LoadIndex (AttributeMigration.cs:71, called from FileMemoryStore.cs:757).
        // A probe carrying FileHashes without the matching attribute is a state the store's write
        // path cannot produce, and export's own cold load would add the key — making the round
        // trip look lossy when it is the FIXTURE that was impossible.
        Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["topic"] = ["retrieval"],
            ["file"] = ["src/x.cs"],
        },
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
