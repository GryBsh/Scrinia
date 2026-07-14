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

        public string Root => _tempDir;

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
        string[]? keywords = null, Dictionary<string, string[]>? fields = null,
        string? contentPreview = "preview") =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "desc",
            Tags: tags, ContentPreview: contentPreview, Keywords: keywords, RetrievalAnchors: anchors,
            ReviewAfter: reviewAfter, ReviewWhen: reviewWhen, FileHashes: codeRefs, Fields: fields);

    [Fact]
    public void Forward_MapsTagsToTagAttribute_AnchorsToAnchorField()
    {
        var e = MakeEntry(tags: ["security", "auth"], anchors: ["how do I log in"]);
        var m = AttributeMigration.Forward(e);
        m.Fields!["tag"].Should().BeEquivalentTo("security", "auth");
        m.Fields!["anchor"].Should().Contain("how do I log in");
        m.Keywords.Should().BeNull(); // no keywords in, none folded in -> fold retired in P6.5
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
        reread.Fields!["anchor"].Should().Contain("how to log in");
        (reread.Keywords is null || !reread.Keywords.Contains("how to log in")).Should().BeTrue();

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

    [Fact]
    public void Forward_RoutesRecognizedKeyValueKeywords_IntoFields_KeepingKeyword()
    {
        var e = MakeEntry(keywords: ["person:maria", "topic:auth", "loose-keyword"]);
        var m = AttributeMigration.Forward(e);
        m.Fields!["person"].Should().BeEquivalentTo("maria");
        m.Fields!["topic"].Should().BeEquivalentTo("auth");
        m.Keywords.Should().Contain(new[] { "person:maria", "topic:auth", "loose-keyword" });
    }

    [Fact]
    public void Forward_UnionsKeywordEntities_IntoExistingMultiValuedFields()
    {
        var e = MakeEntry(keywords: ["person:bob"], fields: new() { ["person"] = ["alice"] });
        var m = AttributeMigration.Forward(e);
        m.Fields!["person"].Should().BeEquivalentTo("alice", "bob");
    }

    [Fact]
    public void Forward_IgnoresUnrecognizedAndMalformedColonKeywords()
    {
        var e = MakeEntry(keywords: ["basedOn:skill-x", "http://example.com", "trailingcolon:", ":leading", "12:30"]);
        var m = AttributeMigration.Forward(e);
        (m.Fields is null || m.Fields.Count == 0).Should().BeTrue();
        m.Keywords.Should().Contain("basedOn:skill-x");
    }

    [Fact]
    public void Forward_KeyValueRouting_IsIdempotent()
    {
        var e = MakeEntry(keywords: ["ref:other-note"]);
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        once.Fields!["ref"].Should().BeEquivalentTo("other-note");
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();
    }

    [Fact]
    public void Forward_CanonicalizesKeyValueKeywordKey_ToLowercase()
    {
        var e = MakeEntry(keywords: ["Person:Maria"]);
        var m = AttributeMigration.Forward(e);
        // Assert on the materialized key TEXT (ordinal/case-sensitive). The Fields dict and its
        // KeyCollection both use OrdinalIgnoreCase, so ContainKey/Keys.Contains("Person") would
        // match "person"; ToList() forces the default ordinal comparer to prove the persisted
        // key text is lowercase.
        m.Fields!.Keys.ToList().Should().Contain("person").And.NotContain("Person");
        m.Fields!["person"].Should().BeEquivalentTo("Maria"); // value casing preserved
    }

    [Fact]
    public void Forward_routes_anchors_into_Fields_anchor()
    {
        var entry = MakeEntry(anchors: ["how to reset", "  ", "reset flow", "how to reset"]);
        var result = AttributeMigration.Forward(entry);

        result.Fields.Should().ContainKey("anchor");
        result.Fields!["anchor"].Should().Equal("how to reset", "reset flow"); // whitespace dropped, dupe deduped, order kept
    }

    [Fact]
    public void Forward_routes_anchors_to_field_not_keywords()
    {
        var entry = MakeEntry(keywords: ["kw1"], anchors: ["anchor phrase"]);
        var result = AttributeMigration.Forward(entry);

        result.Keywords.Should().Contain("kw1");                    // unrelated keyword untouched
        result.Keywords.Should().NotContain("anchor phrase");       // fold retired in P6.5
        result.Fields!["anchor"].Should().Equal("anchor phrase");   // anchor lives in Fields only
    }

    [Fact]
    public void Forward_no_anchors_adds_no_anchor_field()
    {
        var entry = MakeEntry(anchors: null);
        var result = AttributeMigration.Forward(entry);
        (result.Fields is null || !result.Fields.ContainsKey("anchor")).Should().BeTrue();
    }

    [Fact]
    public void Forward_anchor_routing_is_idempotent()
    {
        var entry = MakeEntry(anchors: ["a1", "a2"]);
        var once = AttributeMigration.Forward(entry);
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse(); // second pass is a no-op
    }

    [Fact]
    public void Forward_anchor_coexists_with_other_fields()
    {
        var entry = MakeEntry(tags: ["t1"], anchors: ["a1"]);
        var result = AttributeMigration.Forward(entry);
        result.Fields!["tag"].Should().Equal("t1");
        result.Fields!["anchor"].Should().Equal("a1");
    }

    [Fact]
    public void Forward_BackfillsContentPreviewFromDescription_WhenPreviewNull()
    {
        var e = MakeEntry(contentPreview: null);   // Description "desc", ContentPreview null
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().Be("desc");       // rescued from Description
        m.Description.Should().Be("desc");          // additive: legacy field untouched
    }

    [Fact]
    public void Forward_LeavesExistingContentPreview_WhenAlreadySet()
    {
        var e = MakeEntry(contentPreview: "explicit preview");
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().Be("explicit preview");   // ?? short-circuits, no overwrite
    }

    [Fact]
    public void Forward_LeavesContentPreviewNull_WhenDescriptionBlank()
    {
        var e = new ArtifactEntry(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "   ", ContentPreview: null);
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().BeNull();   // blank Description is not preview-worthy
    }

    [Fact]
    public void MigratesTo_DetectsContentPreviewBackfill_AndIsIdempotent()
    {
        var e = MakeEntry(contentPreview: null);           // only the preview backfill is pending
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        once.ContentPreview.Should().Be("desc");
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // second pass writes nothing
    }

    [Fact]
    public void MigrateStore_BackfillsContentPreviewFromDescription_ForPreviewOnlyEntry()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        // A pre-ContentPreview sidecar: Description present, ContentPreview null, no other legacy data.
        store.Upsert(MakeEntry(contentPreview: null) with { Name = "old" }, "local");
        // A control that already has ContentPreview and no legacy fields -> must be skipped.
        store.Upsert(MakeEntry(contentPreview: "already", fields: new() { ["topic"] = ["auth"] }) with { Name = "ok" }, "local");

        var first = AttributeMigration.MigrateStore(store);
        first.Total.Should().Be(2);
        first.Migrated.Should().Be(1);   // only "old" (preview backfill) migrates
        first.Skipped.Should().Be(1);    // "ok" already has ContentPreview and attributes

        store.LoadIndex("local").First(e => e.Name == "old").ContentPreview.Should().Be("desc");

        // idempotent: re-running writes nothing
        var second = AttributeMigration.MigrateStore(store);
        second.Migrated.Should().Be(0);
        second.Skipped.Should().Be(2);
    }

    [Fact]
    public void EnumerateRawForMigration_YieldsRawEntries_AndWritesNothing()
    {
        using var tmp = new TempWorkspace();
        // Write a legacy sidecar to disk via one store, then read it cold via a fresh store.
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(tags: ["security"], anchors: ["how do I log in"], contentPreview: null) with { Name = "a", Description = "" }, "local");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var items = cold.EnumerateRawForMigration().ToList();

        var a = items.Single(i => i.Entry.Name == "a");
        a.Entry.Fields.Should().BeNull("enumeration must return the raw, un-forward-filled entry");
        a.Scope.Should().Be("local");
        a.StoreDir.Should().Be(cold.GetStoreDirForScope("local")); // existing public accessor; no new test hook
        // Enumerating must not mutate disk: re-read raw, still no fields.
        new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
            .Single(i => i.Entry.Name == "a").Entry.Fields.Should().BeNull();
        cold.Dispose();
    }

    [Fact]
    public void MigrateStore_ColdCache_AccurateCount_AndWrites()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(tags: ["security"], anchors: ["how to log in"], contentPreview: null) with { Name = "legacy", Description = "" }, "local");
        seed.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "clean" }, "local"); // already current
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold);

        s.Total.Should().Be(2);
        s.Migrated.Should().Be(1, "only the legacy entry needed migrating");
        s.Skipped.Should().Be(1);
        new FileMemoryStore(tmp.Root).LoadIndex("local").First(e => e.Name == "legacy")
            .Fields!.Should().ContainKeys("tag", "anchor");
        cold.Dispose();
    }

    [Fact]
    public void MigrateStore_ColdCache_Idempotent_SecondRunReportsZero()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(tags: ["x"], contentPreview: null) with { Name = "a", Description = "" }, "local");
        seed.Dispose();

        AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root)).Migrated.Should().Be(1);
        var second = AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root));
        second.Migrated.Should().Be(0, "sidecars are upgraded on disk after run 1");
        second.Skipped.Should().Be(second.Total);
    }

    [Fact]
    public void MigrateStore_ColdCache_DryRun_WritesNothing_ButCountsTruthfully()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(tags: ["x"], contentPreview: null) with { Name = "a", Description = "" }, "local");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold, dryRun: true);

        s.Migrated.Should().Be(1, "dry-run still reports the true count");
        new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
            .Single(i => i.Entry.Name == "a").Entry.Fields
            .Should().BeNull("dry-run must not write");
        cold.Dispose();
    }

    [Fact]
    public void MigrateStore_ColdCache_LegacyFlatDir_Persisted_Counted_NoPromotion()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        // Namespaced sibling so the bare flat dir is shadowed (reached only via the legacy merge).
        // "local-topic:memory/api" is what MemoryNaming.BuildScopedTopicScope("api") produces (verified
        // in FileMemoryStore.GetStoreDirForScope/ResolveLegacyFallbackDir): ClassifyTopic("api") == "memory",
        // so the namespaced dir is .scrinia/topics/memory/api and its legacy flat fallback is .scrinia/topics/api.
        seed.Upsert(MakeEntry(fields: new() { ["topic"] = ["api"] }) with { Name = "nsnote" }, MemoryNaming.BuildScopedTopicScope("api"));
        // Legacy entry in the bare flat dir .scrinia/topics/api
        seed.Upsert(MakeEntry(tags: ["legacy"], contentPreview: null) with { Name = "flatnote", Description = "" }, "local-topic:api");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold);

        // Exactly 1 (not >= 1): the shadowed flat entry is counted once — the (dir,name) dedup in
        // EnumerateRawForMigration prevents a double-count if the flat dir is a fallback of >1 scope.
        s.Migrated.Should().Be(1, "the shadowed flat entry needed migrating, counted exactly once");
        // The FLAT sidecar is upgraded in place:
        string flatMeta = Path.Combine(tmp.Root, ".scrinia", "topics", "api", "flatnote.meta.json");
        File.ReadAllText(flatMeta).Should().Contain("\"fields\"").And.Contain("\"tag\"");
        // ...and NOT promoted into the namespaced scope dir:
        File.Exists(Path.Combine(tmp.Root, ".scrinia", "topics", "memory", "api", "flatnote.meta.json"))
            .Should().BeFalse("the flat entry must stay in its flat dir, not be promoted");
        cold.Dispose();
    }

    [Fact]
    public void PersistMigratedEntry_WritesToGivenDir_InvalidatesCache_NoPromotion()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        // Target an explicit dir that is NOT the entry's scope dir (simulates a flat-dir write target).
        string flatDir = Path.Combine(tmp.Root, ".scrinia", "topics", "api");
        var upgraded = AttributeMigration.Forward(
            MakeEntry(tags: ["t"], contentPreview: null) with { Name = "flatnote", Description = "" });

        store.PersistMigratedEntry(upgraded, flatDir, "local-topic:memory/api");

        File.Exists(Path.Combine(flatDir, "flatnote.meta.json")).Should().BeTrue("written to the given dir");
        // NOT promoted into the namespaced scope dir: no sidecar for "flatnote" was written there.
        // (We check for the FILE, not Directory.Exists: acquiring the scope's exclusive FileLock via
        // GetLockPath(scope) -> GetStoreDirForScope(scope) creates that directory as a side effect
        // for the ".lock" file, same as any Upsert/LoadIndex call for this scope would — that is
        // pre-existing FileLock behavior, not entry promotion.)
        File.Exists(Path.Combine(tmp.Root, ".scrinia", "memories", "memory", "api", "flatnote.meta.json")).Should().BeFalse();
        File.Exists(Path.Combine(tmp.Root, ".scrinia", "topics", "memory", "api", "flatnote.meta.json")).Should().BeFalse();
        store.Dispose();
    }
}
