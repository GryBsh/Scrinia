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

    // Merge 3 Task 7: Tags/RetrievalAnchors/ReviewAfter/ReviewWhen/Description are removed from
    // ArtifactEntry, so MakeEntry no longer accepts or wires them — the only surviving legacy-carrier
    // inputs are codeRefs (FileHashes) and fields (Attributes). BuildCarrierAttributes_MatchesForward_ForFile
    // below exercises tags/anchors/review via its own locals (BuildCarrierAttributes's request-param
    // signature is unchanged) rather than round-tripping them through the entry.
    private static ArtifactEntry MakeEntry(
        Dictionary<string, string>? codeRefs = null,
        string[]? keywords = null, Dictionary<string, string[]>? fields = null,
        string? contentPreview = "preview") =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch,
            ContentPreview: contentPreview, Keywords: keywords, FileHashes: codeRefs, Attributes: fields);

    [Fact]
    public void Forward_MapsCodeRefPathsToFile()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" });
        var m = AttributeMigration.Forward(e);
        m.Attributes!["file"].Should().BeEquivalentTo("src/A.cs", "src/B.cs");   // paths, not hashes
    }

    [Fact]
    public void Forward_UnionsIntoExistingFields_WithoutDuplicating()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" },
            fields: new() { ["file"] = ["src/A.cs"], ["person"] = ["maria"] });
        var m = AttributeMigration.Forward(e);
        m.Attributes!["file"].Should().BeEquivalentTo("src/A.cs");   // union dedupes
        m.Attributes!["person"].Should().BeEquivalentTo("maria");    // untouched
    }

    [Fact]
    public void MigratesTo_IsIdempotent_SecondPassReportsNoChange()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" });
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
        store.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "a" }, "local");
        store.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "b" }, "local");

        var first = AttributeMigration.MigrateStore(store);
        first.Total.Should().Be(2);
        first.Migrated.Should().Be(1);    // only "a" had legacy data (FileHashes)
        first.Skipped.Should().Be(1);     // "b" already attributes-only

        var reread = store.LoadIndex("local").First(e => e.Name == "a");
        reread.Attributes!["file"].Should().BeEquivalentTo("src/A.cs");

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
        store.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "a" }, "local");

        var summary = AttributeMigration.MigrateStore(store, dryRun: true);
        summary.Migrated.Should().Be(1);
        store.LoadIndex("local").First(e => e.Name == "a").Attributes.Should().BeNull();   // unwritten
    }

    [Fact]
    public void MigrateStore_CoversTopicScopes()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        store.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "note" }, "local-topic:findings");

        AttributeMigration.MigrateStore(store).Migrated.Should().Be(1);
        store.LoadIndex("local-topic:findings").First().Attributes!["file"].Should().BeEquivalentTo("src/A.cs");
    }

    [Fact]
    public void Forward_RoutesRecognizedKeyValueKeywords_IntoFields_KeepingKeyword()
    {
        var e = MakeEntry(keywords: ["person:maria", "topic:auth", "loose-keyword"]);
        var m = AttributeMigration.Forward(e);
        m.Attributes!["person"].Should().BeEquivalentTo("maria");
        m.Attributes!["topic"].Should().BeEquivalentTo("auth");
        m.Keywords.Should().Contain(new[] { "person:maria", "topic:auth", "loose-keyword" });
    }

    [Fact]
    public void Forward_UnionsKeywordEntities_IntoExistingMultiValuedFields()
    {
        var e = MakeEntry(keywords: ["person:bob"], fields: new() { ["person"] = ["alice"] });
        var m = AttributeMigration.Forward(e);
        m.Attributes!["person"].Should().BeEquivalentTo("alice", "bob");
    }

    [Fact]
    public void Forward_IgnoresUnrecognizedAndMalformedColonKeywords()
    {
        var e = MakeEntry(keywords: ["basedOn:skill-x", "http://example.com", "trailingcolon:", ":leading", "12:30"]);
        var m = AttributeMigration.Forward(e);
        (m.Attributes is null || m.Attributes.Count == 0).Should().BeTrue();
        m.Keywords.Should().Contain("basedOn:skill-x");
    }

    [Fact]
    public void Forward_KeyValueRouting_IsIdempotent()
    {
        var e = MakeEntry(keywords: ["ref:other-note"]);
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        once.Attributes!["ref"].Should().BeEquivalentTo("other-note");
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
        m.Attributes!.Keys.ToList().Should().Contain("person").And.NotContain("Person");
        m.Attributes!["person"].Should().BeEquivalentTo("Maria"); // value casing preserved
    }

    [Fact]
    public void Forward_LeavesExistingContentPreview_WhenAlreadySet()
    {
        var e = MakeEntry(contentPreview: "explicit preview");
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().Be("explicit preview");   // Forward no longer touches ContentPreview
    }

    [Fact]
    public void Forward_LeavesContentPreviewNull_WhenNotSet()
    {
        var e = new ArtifactEntry(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, ContentPreview: null);
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().BeNull();   // Forward no longer backfills ContentPreview from anything
    }

    [Fact]
    public void EnumerateRawForMigration_YieldsRawEntries_AndWritesNothing()
    {
        using var tmp = new TempWorkspace();
        // Write a legacy sidecar to disk via one store, then read it cold via a fresh store.
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(contentPreview: null) with { Name = "a" }, "local");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var items = cold.EnumerateRawForMigration().ToList();

        var a = items.Single(i => i.Entry.Name == "a");
        a.Entry.Attributes.Should().BeNull("enumeration must return the raw, un-forward-filled entry");
        a.Scope.Should().Be("local");
        a.StoreDir.Should().Be(cold.GetStoreDirForScope("local")); // existing public accessor; no new test hook
        // Enumerating must not mutate disk: re-read raw, still no fields.
        new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
            .Single(i => i.Entry.Name == "a").Entry.Attributes.Should().BeNull();
        cold.Dispose();
    }

    [Fact]
    public void MigrateStore_ColdCache_AccurateCount_AndWrites()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "legacy" }, "local");
        seed.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "clean" }, "local"); // already current
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold);

        s.Total.Should().Be(2);
        s.Migrated.Should().Be(1, "only the legacy entry needed migrating");
        s.Skipped.Should().Be(1);
        new FileMemoryStore(tmp.Root).LoadIndex("local").First(e => e.Name == "legacy")
            .Attributes!.Should().ContainKey("file");
        cold.Dispose();
    }

    [Fact]
    public void MigrateStore_ColdCache_Idempotent_SecondRunReportsZero()
    {
        using var tmp = new TempWorkspace();
        var seed = tmp.NewStore();
        seed.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "a" }, "local");
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
        seed.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "a" }, "local");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold, dryRun: true);

        s.Migrated.Should().Be(1, "dry-run still reports the true count");
        new FileMemoryStore(tmp.Root).EnumerateRawForMigration()
            .Single(i => i.Entry.Name == "a").Entry.Attributes
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
        seed.Upsert(MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" }) with { Name = "flatnote" }, "local-topic:api");
        seed.Dispose();

        var cold = new FileMemoryStore(tmp.Root);
        var s = AttributeMigration.MigrateStore(cold);

        // Exactly 1 (not >= 1): the shadowed flat entry is counted once — the (dir,name) dedup in
        // EnumerateRawForMigration prevents a double-count if the flat dir is a fallback of >1 scope.
        s.Migrated.Should().Be(1, "the shadowed flat entry needed migrating, counted exactly once");
        // The FLAT sidecar is upgraded in place:
        string flatMeta = Path.Combine(tmp.Root, ".scrinia", "topics", "api", "flatnote.meta.json");
        File.ReadAllText(flatMeta).Should().Contain("\"attributes\"").And.Contain("\"file\"");
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
            MakeEntry(contentPreview: null) with { Name = "flatnote" });

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

    [Fact]
    public void BuildCarrierAttributes_MatchesForward_ForFile()
    {
        // tags/anchors/reviewAfter/reviewWhen have no member home anymore (Task 7 removed them from
        // ArtifactEntry) — BuildCarrierAttributes's signature is unchanged (request-supplied params
        // still flow through it at write time), so this test feeds it locals directly instead of
        // round-tripping the values through the entry.
        string[] tags = ["security", "auth"];
        string[] anchors = ["how to reset", "  ", "reset flow", "how to reset"];
        var codeRefs = new Dictionary<string, string> { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" };
        var reviewAfter = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);
        const string reviewWhen = "when auth changes";
        var entry = MakeEntry(codeRefs: codeRefs);

        var built = AttributeMigration.BuildCarrierAttributes(
            tags, codeRefs.Keys, reviewAfter, reviewWhen, anchors);

        built["tag"].Should().BeEquivalentTo(tags);
        built["file"].Should().BeEquivalentTo(entry.FileHashes!.Keys);
        built["review"].Should().Equal(
            reviewAfter.ToString("o", CultureInfo.InvariantCulture), reviewWhen);
        built["anchor"].Should().Equal("how to reset", "reset flow"); // whitespace dropped, dupe deduped

        // Forward only member-sources "file" (the only surviving legacy-carrier member);
        // BuildCarrierAttributes itself is unchanged and still folds all four when called directly
        // with request-supplied params (the write-time call shape).
        var forwarded = AttributeMigration.Forward(entry);
        forwarded.Attributes!["file"].Should().BeEquivalentTo(built["file"]);
        forwarded.Attributes.Should().NotContainKey("tag");
        forwarded.Attributes.Should().NotContainKey("review");
        forwarded.Attributes.Should().NotContainKey("anchor");
    }
}
