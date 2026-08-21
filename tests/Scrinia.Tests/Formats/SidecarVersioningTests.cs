using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Formats;

public sealed partial class SidecarVersioningTests
{
    // Mirrors FileStoreJsonContext's options exactly; the store's context is private. A
    // hand-built JsonSerializerOptions (reflection-based) would NOT exercise the constraint that
    // Unknown must be `{ get; set; }` rather than `{ get; init; }` — reflection-based binding
    // does not go through the same deserialization-constructor path that source generation does,
    // so a reflection test would let that requirement ship untested. Source generation is the
    // whole point of this suite.
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class SidecarTestCtx : JsonSerializerContext;

    [Fact]
    public void UnknownKey_SurvivesReadThenWrite()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","futureThing":{"a":1}}
        """;

        var entry = JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!;
        entry.Unknown.Should().ContainKey("futureThing");

        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry).Should().Contain("futureThing");
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

        var entry = JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!;
        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry)
            .Should().Contain("preserved by the merge driver");
    }

    [Fact]
    public void SchemaVersion_RoundTrips()
    {
        const string json = """
        {"name":"seed","uri":"file:///seed.nmp2","originalBytes":5,"chunkCount":1,
         "createdAt":"2026-01-01T00:00:00+00:00","v":2}
        """;

        JsonSerializer.Deserialize(json, SidecarTestCtx.Default.ArtifactEntry)!.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void V1Entry_EmitsNoVersionKey()
    {
        // The whole no-churn property. If this fails, adopting versioning rewrites every
        // sidecar in every user's store through the merge driver for zero information.
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1, DateTimeOffset.UtcNow);

        entry.SchemaVersion.Should().BeNull();
        JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry).Should().NotContain("\"v\"");
    }

    [Fact]
    public void EntryWithNoUnknowns_EmitsNoExtraKeys()
    {
        var entry = new ArtifactEntry("seed", "file:///seed.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var reparsed = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(entry, SidecarTestCtx.Default.ArtifactEntry));

        reparsed.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("name", "uri", "originalBytes", "chunkCount", "createdAt");
    }

    /// <summary>
    /// Unknown keys are sorted with <see cref="StringComparer.Ordinal"/> on write, alongside the
    /// Keywords / TermFrequencies / FileHashes sorting WriteSidecarCore already does for stable
    /// git diffs.
    ///
    /// <para><b>This test distinguishes the two comparers, rather than merely asserting "sorted".</b>
    /// The three keys are chosen so the expected order is UNREACHABLE under OrdinalIgnoreCase:
    /// ordinal puts every uppercase letter before every lowercase one, giving Alpha, alpha, zeta;
    /// case-insensitive compares Alpha and alpha as equal and yields Alpha/alpha in input order
    /// followed by zeta, so "alpha" before "Alpha" survives. A test using only lowercase keys
    /// would pass under both comparers and pin nothing.</para>
    ///
    /// <para>Ordinal is correct because these keys come from a schema this build cannot reason
    /// about, and JSON keys are case-sensitive: "Alpha" and "alpha" are two different keys, and a
    /// comparer that calls them equal makes their relative order depend on dictionary insertion
    /// order, which is exactly the churn the sorting exists to prevent.</para>
    /// </summary>
    [Fact]
    public void UnknownKeys_AreSortedOrdinally_OnWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        var entry = new ArtifactEntry("sorted", "file:///sorted.nmp2", 5, 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        {
            Unknown = new Dictionary<string, JsonElement>
            {
                ["zeta"] = JsonDocument.Parse("1").RootElement,
                ["alpha"] = JsonDocument.Parse("2").RootElement,
                ["Alpha"] = JsonDocument.Parse("3").RootElement,
            }
        };

        store.Upsert(entry, "local:notes");

        // Read the RAW sidecar text off disk: key ORDER is a property of the bytes, and any
        // assertion that goes back through a Dictionary loses it.
        string raw = File.ReadAllText(
            Path.Combine(scope.TempDir, "notes", "sorted.meta.json"));

        var keys = JsonDocument.Parse(raw).RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n is "zeta" or "alpha" or "Alpha")
            .ToList();

        keys.Should().Equal("Alpha", "alpha", "zeta");
    }

    // ── Read path: a newer sidecar loads, and forward-fill leaves it alone ──────────────

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

        ((FileMemoryStore)store).InvalidateCaches();   // NOT on IMemoryStore -- cast required
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

    /// <summary>
    /// Composes a <c>.meta.json</c> sidecar BY HAND, bypassing <see cref="ArtifactEntry"/>
    /// entirely. That is deliberate: the typed model cannot emit a legacy "fields" key
    /// alongside a "v" key (LegacyFields and SchemaVersion coexist fine on the type, but
    /// nothing in production ever constructs that combination), and this suite needs exactly
    /// that combination to reach the forward-fill write path under a version the current
    /// build must not touch.
    /// </summary>
    private static string WriteRawSidecar(
        TestHelpers.StoreScope scope, string storeScope, string name,
        int? schemaVersion = null, string? extraKey = null, bool legacyShaped = false)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["uri"] = $"file:///{name}.nmp2",
            ["originalBytes"] = 5,
            ["chunkCount"] = 1,
            ["createdAt"] = "2026-01-01T00:00:00+00:00",
        };
        if (schemaVersion is not null)
            obj["v"] = schemaVersion.Value;
        if (legacyShaped)
            obj["fields"] = new JsonObject { ["tag"] = new JsonArray("legacy-tag") };
        if (extraKey is not null)
            obj[extraKey] = "present";

        string path = SidecarPath(scope, storeScope, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    /// <summary>Where a sidecar for <paramref name="name"/> in <paramref name="storeScope"/>
    /// lives on disk, without going through the store at all -- <c>local:notes</c> maps to
    /// <c>{TempDir}/notes/&lt;name&gt;.meta.json</c>.</summary>
    private static string SidecarPath(TestHelpers.StoreScope scope, string storeScope, string name)
    {
        int colon = storeScope.IndexOf(':');
        string sub = colon >= 0 ? storeScope[(colon + 1)..] : storeScope;
        return Path.Combine(scope.TempDir, sub, name + ".meta.json");
    }
}
