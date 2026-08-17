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

    [Fact]
    public void WriteFirstSession_DoesNotCacheAnUnnormalizedEntry()
    {
        // A write-first session: Upsert before any cold LoadIndex on this scope. The cached
        // entries must be normalized even though no read populated the cache.
        var seed = new ArtifactEntry("note-a", "", 5, 1, DateTimeOffset.UtcNow);
        _store.Upsert(seed, "local");
        string metaPath = FindSidecar("note-a");
        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"topic":["beta"]}}""";
        File.WriteAllText(metaPath, json);

        // Second write to a DIFFERENT name in the same scope — whatever populates the cache
        // as a result must carry note-a normalized.
        _store.Upsert(new ArtifactEntry("note-b", "", 5, 1, DateTimeOffset.UtcNow), "local");

        var a = _store.LoadIndex("local").Single(e => e.Name == "note-a");
        a.Attributes.Should().NotBeNull();
        a.Attributes!["topic"].Should().BeEquivalentTo(["beta"]);
    }

    /// <summary>
    /// The guard that most directly justifies the seam. TryUpdateEntry's COLD path calls
    /// LoadEntriesFromSidecars directly, with no AttributeMigration.MigratesTo forward-fill —
    /// so it is the one production path where an un-normalized entry would reach a caller.
    /// Task 3 keeps that path; this test fails if the seam call is ever removed from under it.
    /// </summary>
    [Fact]
    public void TryUpdateEntry_ColdPath_HandsTheMutatorANormalizedEntry()
    {
        _store.Upsert(new ArtifactEntry("legacy-b", "", 5, 1, DateTimeOffset.UtcNow), "local");
        string metaPath = FindSidecar("legacy-b");
        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"topic":["gamma"]}}""";
        File.WriteAllText(metaPath, json);

        // Fresh instance: TryUpdateEntry is the FIRST operation, so the scope is uncached and
        // the lookup takes the sidecar-scan path.
        using var fresh = new FileMemoryStore(_root);

        Dictionary<string, string[]>? seenByMutator = null;
        bool ok = fresh.TryUpdateEntry("legacy-b", "local", e =>
        {
            seenByMutator = e.Attributes;
            return e;
        });

        ok.Should().BeTrue();
        seenByMutator.Should().NotBeNull(
            because: "the cold lookup path has no MigratesTo forward-fill, so the load seam is "
                   + "the only thing that folds the legacy \"fields\" key before a mutator sees it");
        seenByMutator!["topic"].Should().BeEquivalentTo(["gamma"]);
    }

    private string FindSidecar(string name) =>
        Directory.GetFiles(_root, $"{name}.meta.json", SearchOption.AllDirectories).Single();
}
