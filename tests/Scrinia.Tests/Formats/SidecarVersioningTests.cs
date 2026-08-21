using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
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
}
