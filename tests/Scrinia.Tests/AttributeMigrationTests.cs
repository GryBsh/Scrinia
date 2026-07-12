using System.Globalization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AttributeMigrationTests
{
    // Mirrors FileMemoryStoreTests' temp-dir + FileMemoryStore(root) construction/disposal.
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _tempDir;
        private FileMemoryStore? _store;

        public TempWorkspace()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-attrmig-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        public FileMemoryStore NewStore() => _store = new FileMemoryStore(_tempDir);

        public void Dispose()
        {
            _store?.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static ArtifactEntry MakeEntry(
        string[]? tags = null, string[]? anchors = null,
        Dictionary<string, string>? codeRefs = null,
        DateTimeOffset? reviewAfter = null, string? reviewWhen = null,
        string[]? keywords = null, Dictionary<string, string[]>? fields = null) =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "desc",
            Tags: tags, Keywords: keywords, RetrievalAnchors: anchors,
            ReviewAfter: reviewAfter, ReviewWhen: reviewWhen, CodeRefs: codeRefs, Fields: fields);

    [Fact]
    public void Forward_MapsTagsToTagAttribute_AnchorsToKeywords()
    {
        var e = MakeEntry(tags: ["security", "auth"], anchors: ["how do I log in"]);
        var m = AttributeMigration.Forward(e);
        m.Fields!["tag"].Should().BeEquivalentTo("security", "auth");
        m.Keywords.Should().Contain("how do I log in");
        // additive: legacy fields preserved
        m.Tags.Should().BeEquivalentTo("security", "auth");
        m.RetrievalAnchors.Should().BeEquivalentTo("how do I log in");
        m.Description.Should().Be("desc");
    }

    [Fact]
    public void Forward_MapsCodeRefPathsToFile_AndReviewToReview()
    {
        var e = MakeEntry(
            codeRefs: new() { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" },
            reviewAfter: DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
            reviewWhen: "when auth changes");
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs", "src/B.cs");   // paths, not hashes
        m.Fields!["review"].Should().Contain(v => v.StartsWith("2026-08-01"))
            .And.Contain("when auth changes");
    }

    [Fact]
    public void Forward_UnionsIntoExistingFields_WithoutDuplicating()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" },
            fields: new() { ["file"] = ["src/A.cs"], ["person"] = ["maria"] });
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs");   // union dedupes
        m.Fields!["person"].Should().BeEquivalentTo("maria");    // untouched
    }

    [Fact]
    public void MigratesTo_IsIdempotent_SecondPassReportsNoChange()
    {
        var e = MakeEntry(tags: ["x"], anchors: ["y"]);
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // already forward-filled
    }

    [Fact]
    public void MigratesTo_NoLegacyData_ReportsNoChange()
    {
        var e = MakeEntry(fields: new() { ["topic"] = ["auth"] });
        AttributeMigration.MigratesTo(e, out _).Should().BeFalse();
    }

    [Fact]
    public void MigrateStore_ForwardFillsLegacyEntries_AndIsIdempotent()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        store.Upsert(MakeEntry(tags: ["security"], anchors: ["how to log in"]) with { Name = "a" }, "local");
        store.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "b" }, "local");

        var first = AttributeMigration.MigrateStore(store);
        first.Total.Should().Be(2);
        first.Migrated.Should().Be(1);    // only "a" had legacy data
        first.Skipped.Should().Be(1);     // "b" already attributes-only

        var reread = store.LoadIndex("local").First(e => e.Name == "a");
        reread.Fields!["tag"].Should().BeEquivalentTo("security");
        reread.Keywords.Should().Contain("how to log in");

        // idempotent: second run writes nothing
        var second = AttributeMigration.MigrateStore(store);
        second.Migrated.Should().Be(0);
        second.Skipped.Should().Be(2);
    }

    [Fact]
    public void MigrateStore_DryRun_ReportsButDoesNotWrite()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        store.Upsert(MakeEntry(tags: ["x"]) with { Name = "a" }, "local");

        var summary = AttributeMigration.MigrateStore(store, dryRun: true);
        summary.Migrated.Should().Be(1);
        store.LoadIndex("local").First(e => e.Name == "a").Fields.Should().BeNull();   // unwritten
    }

    [Fact]
    public void MigrateStore_CoversTopicScopes()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        store.Upsert(MakeEntry(tags: ["t"]) with { Name = "note" }, "local-topic:findings");

        AttributeMigration.MigrateStore(store).Migrated.Should().Be(1);
        store.LoadIndex("local-topic:findings").First().Fields!["tag"].Should().BeEquivalentTo("t");
    }
}
