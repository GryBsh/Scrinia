using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Pins the invariant stated at ArtifactEntry.cs:67 — every load seam normalizes the legacy
/// "fields" key before an entry escapes. Before this moved into LoadEntriesFromSidecars, four
/// write sites each pasted `.Select(e => e.NormalizeAttributesKey())` around it; deleting any one
/// of them silently hid that entry's facets from search, filter and display for the process
/// lifetime. This test fails if the seam change is reverted.
/// </summary>
public sealed class NormalizationSeamTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public NormalizationSeamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_norm_seam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LoadIndex_NormalizesLegacyFieldsKey_OnAColdLoad()
    {
        // Seed a sidecar carrying the LEGACY "fields" key and no "attributes" key at all —
        // the on-disk shape written before the attributes rename.
        var seed = new ArtifactEntry("legacy-note", "", 10, 1, DateTimeOffset.UtcNow);
        _store.Upsert(seed, "local");
        string metaPath = FindSidecar("legacy-note");

        // Build the legacy shape by rewriting the sidecar the store just wrote, rather than
        // hand-authoring JSON: ArtifactEntry's positional members are
        // (Name, Uri, OriginalBytes, ChunkCount, CreatedAt) and its persisted key names are
        // set by FileStoreJsonContext, so a hand-written literal silently drifts.
        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"topic":["alpha"]}}""";
        File.WriteAllText(metaPath, json);

        // Force a cold load: drop the cache the Upsert above installed.
        using var reader = new FileMemoryStore(_root);

        var entry = reader.LoadIndex("local").Single(e => e.Name == "legacy-note");

        entry.Attributes.Should().NotBeNull(
            because: "the load seam must fold the legacy \"fields\" key into Attributes; "
                   + "an entry escaping with Attributes == null hides its facets from search");
        entry.Attributes!["topic"].Should().BeEquivalentTo(["alpha"]);
    }

    /// <summary>
    /// Task 2 (write-path cost) repoint: the original version of this test wrote to a scope that
    /// was uncached both times (a fresh store's first Upsert never warms the cache — see
    /// IndexCacheWritePathTests), so its final LoadIndex was a genuine cold load and the
    /// assertion passed via LoadIndexUnsafe's unrelated read-time forward-fill, not via anything
    /// Upsert's own cache update did. This version warms the scope FIRST so the Upsert under test
    /// runs its copy-on-write branch, and proves the following read is a cache hit (not a cold
    /// reload) before trusting what it returns — otherwise the same masking could recur silently.
    /// </summary>
    [Fact]
    public void Upsert_CachesTheNormalizedEntry_NotTheCallersRawLegacyFieldsEntry()
    {
        _store.Upsert(new ArtifactEntry("note-b", "", 5, 1, DateTimeOffset.UtcNow), "local");
        _store.LoadIndex("local").Should().ContainSingle();   // warm the scope

        // A legacy-shaped entry constructed directly — LegacyFields populated, Attributes null —
        // rather than corrupting a sidecar on disk, because what is under test is whether
        // Upsert's copy-on-write cache update normalizes the CALLER'S entry before caching it,
        // not whether a cold read can forward-fill a legacy sidecar (LoadIndex_NormalizesLegacy-
        // FieldsKey_OnAColdLoad above already covers that).
        var legacyEntry = new ArtifactEntry("note-a", "", 5, 1, DateTimeOffset.UtcNow)
        {
            LegacyFields = new Dictionary<string, string[]> { ["topic"] = ["beta"] }
        };
        _store.Upsert(legacyEntry, "local");

        string lockPath = Path.Combine(_store.GetStoreDirForScope("local"), ".lock");
        File.Exists(lockPath).Should().BeTrue(
            because: "precondition: the Upserts above run under the exclusive scope lock and must "
                   + "have created this file — File.Delete on a missing path is a silent no-op, so "
                   + "without this the cache-hit assertion below could pass vacuously");
        File.Delete(lockPath);

        var a = _store.LoadIndex("local").Single(e => e.Name == "note-a");

        File.Exists(lockPath).Should().BeFalse(
            because: "this read must be a cache hit — both Upserts above ran against an "
                   + "already-warmed scope, so a lock file recreated here would mean the read "
                   + "cold-reloaded instead, and the assertion below would be proving the "
                   + "unrelated forward-fill mechanism again rather than the cache update");
        a.Attributes.Should().NotBeNull(
            because: "Upsert's copy-on-write cache update must cache "
                   + "entry.NormalizeAttributesKey(), not the caller's raw entry — caching the "
                   + "raw form would let a LegacyFields-bearing entry sit in the cache with "
                   + "Attributes == null while disk holds the normalized form (WriteSidecarCore "
                   + "normalizes before serializing), hiding this entry's facets from "
                   + "search/filter/display for the rest of the process lifetime");
        a.Attributes!["topic"].Should().BeEquivalentTo(["beta"]);
    }

    /// <summary>
    /// The guard that most directly justifies the seam. TryUpdateEntry's COLD path calls
    /// LoadEntriesFromSidecars directly, with no AttributeMigration.MigratesTo forward-fill —
    /// so it is the one production path where an un-normalized entry would reach a caller.
    /// Task 3 keeps that path; this test fails if the seam call is ever removed from under it.
    /// </summary>
    /// <summary>
    /// The third write path's cache write. <c>SaveIndex</c> hands the caller's list straight to
    /// <c>_indexCache</c>, while <c>WriteSidecarCore</c> normalizes a local copy on its way to
    /// disk — so without a normalize here, disk and cache disagree exactly as they would if
    /// <c>Upsert</c>'s cache write skipped it. Reachable in production, not only through this
    /// hand-built entry: <c>ImportTopicEntries</c> is the sole production caller and builds its
    /// list from <c>LoadIndex(topicScope).ToList()</c>, which can itself hand back a
    /// LegacyFields-bearing entry (LoadIndexUnsafe caches raw whenever MigratesTo reports no
    /// change).
    /// </summary>
    [Fact]
    public void SaveIndex_CachesTheNormalizedEntries_NotTheCallersRawLegacyFieldsList()
    {
        var legacyEntry = new ArtifactEntry("saved-legacy", "", 5, 1, DateTimeOffset.UtcNow)
        {
            LegacyFields = new Dictionary<string, string[]> { ["topic"] = ["delta"] }
        };

        _store.SaveIndex([legacyEntry], "local");

        // SaveIndex installs the cache unconditionally, so this read is a cache hit and is
        // therefore reading what SaveIndex cached rather than what a cold reload would rebuild.
        string lockPath = Path.Combine(_store.GetStoreDirForScope("local"), ".lock");
        File.Exists(lockPath).Should().BeTrue(
            because: "precondition: the SaveIndex above runs under the exclusive scope lock and "
                   + "must have created this file — File.Delete on a missing path is a silent "
                   + "no-op, so without this the assertion below could pass vacuously");
        File.Delete(lockPath);

        var saved = _store.LoadIndex("local").Single(e => e.Name == "saved-legacy");

        File.Exists(lockPath).Should().BeFalse(
            because: "SaveIndex caches the list it was given, so this read must be a cache hit — "
                   + "a recreated lock file would mean it cold-reloaded and the assertions below "
                   + "would be re-proving the load seam instead of SaveIndex's cache write");
        saved.LegacyFields.Should().BeNull();
        saved.Attributes.Should().NotBeNull(
            because: "SaveIndex must cache the normalized entries — caching them raw leaves a "
                   + "LegacyFields-bearing entry in the cache with Attributes == null while disk "
                   + "holds the normalized form, hiding this entry's facets from "
                   + "search/filter/display for the rest of the process lifetime");
        saved.Attributes!["topic"].Should().BeEquivalentTo(["delta"]);
    }

    [Fact]
    public void TryUpdateEntry_HandsTheMutatorANormalizedEntry()
    {
        _store.Upsert(new ArtifactEntry("legacy-b", "", 5, 1, DateTimeOffset.UtcNow), "local");
        string metaPath = FindSidecar("legacy-b");
        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"topic":["gamma"]}}""";
        File.WriteAllText(metaPath, json);

        // Fresh instance, so nothing is cached: TryUpdateEntry's lookup reads the sidecar off
        // disk through LoadSidecar, which is where the seam has to fire.
        using var fresh = new FileMemoryStore(_root);

        Dictionary<string, string[]>? seenByMutator = null;
        bool ok = fresh.TryUpdateEntry("legacy-b", "local", e =>
        {
            seenByMutator = e.Attributes;
            return e;
        });

        ok.Should().BeTrue();
        seenByMutator.Should().NotBeNull(
            because: "TryUpdateEntry's lookup has no MigratesTo forward-fill of its own, so the "
                   + "load seam is the only thing that folds the legacy \"fields\" key before a "
                   + "mutator sees it");
        seenByMutator!["topic"].Should().BeEquivalentTo(["gamma"]);
    }

    /// <summary>
    /// Guards the DURABLE half of read-time forward-fill, which the in-memory-only assertions
    /// above cannot see. LoadIndexUnsafe (in FileMemoryStore.cs) feeds entries through
    /// AttributeMigration.MigratesTo and, when it reports a change, calls WriteSidecar to rewrite
    /// the sidecar on disk from the legacy "fields" key to "attributes" — this is what organically
    /// migrates a workspace that never runs `scri migrate`. MigratesTo detects "needs migrating" by
    /// calling NormalizeAttributesKey() itself and diffing before/after; that detection only works
    /// if the entry MigratesTo receives is still raw (LegacyFields still populated). If
    /// LoadIndexUnsafe's read is pre-normalized before reaching MigratesTo, the diff is a no-op,
    /// MigratesTo reports false, and WriteSidecar never fires — the in-memory entry is still
    /// correct (which is why Attributes-only assertions miss this), but the on-disk sidecar is
    /// stuck carrying "fields" forever.
    /// </summary>
    [Fact]
    public void LoadIndex_DurablyRewritesTheSidecar_FromLegacyFieldsToAttributes()
    {
        var seed = new ArtifactEntry("legacy-durable", "", 10, 1, DateTimeOffset.UtcNow);
        _store.Upsert(seed, "local");
        string metaPath = FindSidecar("legacy-durable");

        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"topic":["delta"]}}""";
        File.WriteAllText(metaPath, json);

        // Force a cold load so LoadIndexUnsafe's read-time forward-fill actually runs.
        using var reader = new FileMemoryStore(_root);
        _ = reader.LoadIndex("local");

        string rewritten = File.ReadAllText(metaPath);
        rewritten.Should().Contain("\"attributes\"",
            because: "read-time forward-fill must durably rewrite a legacy sidecar to the current key");
        rewritten.Should().NotContain("\"fields\"",
            because: "a legacy sidecar must not survive a cold LoadIndex still carrying the old key");
    }

    private string FindSidecar(string name) =>
        Directory.GetFiles(_root, $"{name}.meta.json", SearchOption.AllDirectories).Single();
}
