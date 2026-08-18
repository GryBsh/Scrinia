using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Pins the write path's cache behaviour. Writes must not re-read every sidecar in the scope,
/// and — load-bearing — must never mutate an existing CachedIndex.Entries in place, because
/// LoadIndex hands that list out BY REFERENCE. See
/// docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
/// </summary>
public sealed class IndexCacheWritePathTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public IndexCacheWritePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_cache_write_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ArtifactEntry E(string name) =>
        new(name, "", 4, 1, DateTimeOffset.UtcNow);

    /// <summary>
    /// Proves <paramref name="read"/> was a cache HIT rather than a cold disk load — the same
    /// observable proxy LoadIndexCacheHitTests uses: FileLock's Acquire opens the ".lock" file
    /// with FileMode.OpenOrCreate, so any cold read (which takes the shared lock) recreates a
    /// deleted lock file, while a genuine cache hit never touches it. Deletes the lock file
    /// immediately before running <paramref name="read"/> so only that call's own lock behaviour
    /// is observed, then asserts it was not recreated. Exists so IndexCacheWritePathTests can
    /// prove the writes ABOVE `read` genuinely warmed/kept-warm the cache — a content-only
    /// assertion on `read`'s result cannot distinguish "warm cache, copy-on-write ran" from
    /// "cache went absent, this read cold-reloaded and happened to get the right answer anyway".
    /// </summary>
    private T AssertReadIsACacheHit<T>(Func<T> read, string because)
    {
        string lockPath = Path.Combine(_store.GetStoreDirForScope("local"), ".lock");
        File.Delete(lockPath);
        T result = read();
        File.Exists(lockPath).Should().BeFalse(because: because);
        return result;
    }

    [Fact]
    public void AReaderHoldingALoadIndexResult_IsNotDisturbedByALaterWrite()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        // The alias a reader holds. LoadIndex returns a live view of CachedIndex.Entries.
        var held = _store.LoadIndex("local");
        int countBefore = held.Count;
        var namesBefore = held.Select(e => e.Name).ToArray();

        _store.Upsert(E("c"), "local");
        _store.Remove("a", "local");

        held.Count.Should().Be(countBefore,
            because: "a write must install a NEW CachedIndex, never mutate the list a reader "
                   + "already holds — an in-place Add/Remove would be visible through this alias");
        held.Select(e => e.Name).Should().BeEquivalentTo(namesBefore);
    }

    [Fact]
    public void Cold_And_Warm_Caches_Agree()
    {
        _store.Upsert(E("a"), "local");

        // LoadIndexUnsafe only caches a NON-EMPTY cold-load result, so the very first Upsert to a
        // fresh store is against an uncached scope and — per the "leave absent" rule — leaves it
        // absent. Warm it explicitly here so every write below actually takes the copy-on-write
        // branch (cache present) instead of also landing in "leave absent", which would make this
        // test compare a cold load against a cold load and could never catch the
        // slot-replacement/append/omit logic diverging from disk.
        _store.LoadIndex("local").Should().ContainSingle();

        _store.Upsert(E("b"), "local");
        _store.Upsert(E("c"), "local");
        _store.Remove("b", "local");

        var warm = AssertReadIsACacheHit(
            () => _store.LoadIndex("local").Select(e => e.Name).ToArray(),
            because: "if every write above stayed on the copy-on-write branch, the cache never "
                   + "went absent and this read never needed the shared lock — recreation here "
                   + "would mean at least one write silently fell back to the uncached-scope "
                   + "path despite the scope being cached, which is exactly the bug this test "
                   + "exists to catch");

        using var cold = new FileMemoryStore(_root);
        var coldNames = cold.LoadIndex("local").Select(e => e.Name).ToArray();

        warm.Should().BeEquivalentTo(coldNames,
            because: "a warm copy-on-write cache and a cold load of the same scope must "
                   + "describe the same scope");
    }

    [Fact]
    public void WriteAgainstAnUncachedScope_LeavesTheWholeScopeReadable()
    {
        // Populate on disk, then start a fresh store so the scope is NOT cached.
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        using var fresh = new FileMemoryStore(_root);

        // First operation on this instance is a WRITE, against a cold cache.
        fresh.Upsert(E("c"), "local");

        fresh.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["a", "b", "c"],
                because: "a write against an uncached scope must not install a partial cache "
                       + "holding only the written entry");
    }

    [Fact]
    public void Upsert_ReplacesAnExistingEntry_RatherThanDuplicatingIt()
    {
        _store.Upsert(E("a"), "local");

        // Warm the cache before the replacing Upsert — see Cold_And_Warm_Caches_Agree for why the
        // first Upsert to a fresh store never does this itself, and why skipping this step would
        // let the second Upsert also land in "leave absent" and mask the slot-replacement loop
        // under test (a cold reload afterward would still report the right OriginalBytes).
        _store.LoadIndex("local").Should().ContainSingle();

        // ArtifactEntry's positional members are (Name, Uri, OriginalBytes, ChunkCount,
        // CreatedAt). There is NO Description property — assert on a member that exists.
        var updated = new ArtifactEntry("a", "", 99, 1, DateTimeOffset.UtcNow);
        _store.Upsert(updated, "local");

        var entries = AssertReadIsACacheHit(
            () => _store.LoadIndex("local").Where(e => e.Name == "a").ToList(),
            because: "the Upsert above must have taken the copy-on-write replace-in-slot branch "
                   + "(cache was warmed just above) rather than falling back to leave-absent — "
                   + "recreation of the lock file here would mean this read cold-reloaded instead "
                   + "of exercising the replacement logic under test");
        entries.Should().HaveCount(1);
        entries[0].OriginalBytes.Should().Be(99);
    }

    [Fact]
    public void Remove_DropsTheEntryFromTheCache()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        // Warm the cache before Remove — see Cold_And_Warm_Caches_Agree for why the first write
        // to a fresh store never does this itself, and why skipping this step would let Remove
        // land in "leave absent" and mask the omit-logic under test (a cold reload afterward
        // would still report the right surviving name).
        _store.LoadIndex("local").Should().HaveCount(2);

        _store.Remove("a", "local");

        var names = AssertReadIsACacheHit(
            () => _store.LoadIndex("local").Select(e => e.Name).ToArray(),
            because: "Remove above must have taken the copy-on-write omit branch (cache was "
                   + "warmed just above) rather than falling back to leave-absent — recreation "
                   + "of the lock file here would mean this read cold-reloaded instead of "
                   + "exercising the omit logic under test");
        names.Should().BeEquivalentTo(["b"]);
    }

    [Fact]
    public void TryUpdateEntry_UpdatesTheCache_AndKeepsTheRestOfTheScope()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");

        // Warm the cache before the update under test — see Cold_And_Warm_Caches_Agree for why
        // the two Upserts above never do this themselves (the first Upsert to a fresh store
        // always lands in "leave absent", and nothing here reads in between). TryUpdateEntry's
        // READ is always disk-authoritative; what needs the cache present is its copy-on-write
        // update, which is the logic this test pins.
        _store.LoadIndex("local").Should().HaveCount(2);

        // No Description property on ArtifactEntry — Uri is the second positional member.
        bool ok = _store.TryUpdateEntry("a", "local", e => e with { Uri = "patched" });

        ok.Should().BeTrue();
        var entries = AssertReadIsACacheHit(
            () => _store.LoadIndex("local"),
            because: "TryUpdateEntry above must have taken the copy-on-write branch (cache was "
                   + "warmed just above) rather than falling back to leave-absent — recreation "
                   + "of the lock file here would mean this read cold-reloaded instead of "
                   + "exercising the warm update logic under test");
        entries.Select(x => x.Name).Should().BeEquivalentTo(["a", "b"]);
        entries.Single(x => x.Name == "a").Uri.Should().Be("patched");
    }

    [Fact]
    public void TryUpdateEntry_AgainstAMissingEntry_ReturnsFalse_AndLeavesTheCacheIntact()
    {
        _store.Upsert(E("a"), "local");

        // Warm the cache before the lookup under test — see Cold_And_Warm_Caches_Agree for why
        // the Upsert above never does this itself.
        _store.LoadIndex("local").Should().ContainSingle();

        bool ok = _store.TryUpdateEntry("nope", "local", e => e with { Uri = "x" });

        ok.Should().BeFalse();
        var names = AssertReadIsACacheHit(
            () => _store.LoadIndex("local").Select(x => x.Name).ToArray(),
            because: "a missing-entry lookup resolves against the sidecars and must leave the "
                   + "warm cache untouched — recreation of the lock file here would mean this "
                   + "read cold-reloaded instead of proving the cache stayed warm and intact");
        names.Should().BeEquivalentTo(["a"]);
    }

    /// <summary>
    /// <see cref="FileMemoryStore.TryUpdateEntry"/> claims atomic load-mutate-persist under a
    /// single acquisition of the per-scope write lock — and that lock (<c>FileLock</c>,
    /// <c>FileShare.None</c>) is a CROSS-PROCESS one, so the claim is about other processes, not
    /// only other threads. The method does not merely read the entry: <c>mutate</c> and
    /// <see cref="MemoryDerivation.ForMetadataUpdate"/> build the complete entry it writes back
    /// FROM whatever the lookup returned, so a lookup answered from <c>_indexCache</c> — which
    /// nothing invalidates across processes — persists a stale entry over the concurrent writer's,
    /// reverting every field the mutation itself did not touch. Not hypothetical here: this
    /// repository's own <c>.scrinia/memories/**/*.meta.json</c> are git-tracked, so a <c>git
    /// pull</c> under a live <c>scri serve</c> produces exactly this state.
    ///
    /// <para>A second <see cref="FileMemoryStore"/> over the same workspace root is the faithful
    /// stand-in for the second process: it has its own empty <c>_indexCache</c> and shares only
    /// the filesystem, so what it leaves behind on disk is exactly what another process leaves
    /// behind.</para>
    /// </summary>
    [Fact]
    public void TryUpdateEntry_ReadsTheEntryFromDisk_NotFromACacheAnotherWriterHasStaled()
    {
        _store.Upsert(new ArtifactEntry("note", "uri-v1", 4, 1, DateTimeOffset.UtcNow), "local");

        // Warm THIS store's cache with v1 — the state a long-lived `scri serve` is in.
        _store.LoadIndex("local").Should().ContainSingle();

        // The "other process" writes v2. Nothing tells _store about it.
        using (var other = new FileMemoryStore(_root))
            other.Upsert(new ArtifactEntry("note", "uri-v2", 99, 1, DateTimeOffset.UtcNow), "local");

        // A metadata-only update of the kind MaintenanceEventSink and EntityTypingEventHandler
        // perform: it touches Attributes and nothing else.
        bool changed = _store.TryUpdateEntry("note", "local", e => e with
        {
            Attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["orphan"] = ["true"],
            },
        });
        changed.Should().BeTrue();

        using var reader = new FileMemoryStore(_root);
        var persisted = reader.LoadIndex("local").Single(e => e.Name == "note");

        persisted.Attributes.Should().ContainKey("orphan",
            because: "the mutation itself must still have been applied and persisted");
        persisted.Uri.Should().Be("uri-v2",
            because: "TryUpdateEntry writes back a COMPLETE entry derived from what it read, so "
                   + "reading a stale cached entry would republish the pre-pull Uri over the "
                   + "other writer's — a silent revert of a field this mutation never touched");
        persisted.OriginalBytes.Should().Be(99,
            because: "same reason as Uri: every field the mutation leaves alone is copied from "
                   + "the entry the lookup returned, so the lookup has to be disk-authoritative");

        _store.LoadIndex("local").Single(e => e.Name == "note").Uri.Should().Be("uri-v2",
            because: "the write also re-seats this store's own cached slot from the entry it just "
                   + "persisted, so the stale copy does not survive the write that overwrote it");
    }

    [Fact]
    public void TryUpdateEntry_AgainstAnUncachedScope_StillFindsTheEntry()
    {
        _store.Upsert(E("a"), "local");
        using var fresh = new FileMemoryStore(_root);

        // First operation on this instance, cold cache — must fall back to the sidecar scan.
        bool ok = fresh.TryUpdateEntry("a", "local", e => e with { Uri = "cold" });

        ok.Should().BeTrue();
        fresh.LoadIndex("local").Single(x => x.Name == "a").Uri.Should().Be("cold");
    }

    /// <summary>
    /// A cached entry is NOT provably normalized: LoadIndexUnsafe only overwrites a slot with its
    /// normalized form when AttributeMigration.MigratesTo reports a change, and MigratesTo reports
    /// NO change for an entry that already has non-null Attributes but ALSO still carries
    /// separately-populated, divergent LegacyFields — NormalizeAttributesKey's coalesce
    /// (`Attributes = Attributes ?? _legacyFields`) is a no-op there, so the before/after diff sees
    /// nothing and the raw, LegacyFields-bearing entry is what gets cached. That is the "inert"
    /// edge case documented at ArtifactEntry.cs:63-68.
    /// <para>This pins that a TryUpdateEntry against such a scope leaves the cache NORMALIZED
    /// anyway. Both halves of the guarantee are exercised: the entry fed to <c>mutate</c> comes
    /// off disk through LoadSidecar (a load seam) rather than out of the raw cached slot, and the
    /// entry written back into the cache is normalized before it lands there. A cache holding a
    /// LegacyFields-bearing entry while disk holds the normalized form is exactly the divergence
    /// the load seam exists to prevent.</para>
    /// </summary>
    [Fact]
    public void TryUpdateEntry_LeavesTheCacheNormalized_EvenWhenTheWarmSlotWasNot()
    {
        // Attributes already populated (so the coalesce below is a no-op) — this is what makes
        // MigratesTo see no change and leave LegacyFields in place on the cached slot.
        var seed = new ArtifactEntry("legacy-warm", "", 5, 1, DateTimeOffset.UtcNow)
        {
            Attributes = new Dictionary<string, string[]> { ["topic"] = ["alpha"] },
        };
        _store.Upsert(seed, "local");

        // Hand-corrupt the sidecar to also carry a legacy "fields" value, mirroring
        // NormalizationSeamTests' pattern of rewriting the store's own output rather than
        // hand-authoring JSON (ArtifactEntry's persisted key names are owned by
        // FileStoreJsonContext, so a hand-written literal would silently drift).
        string metaPath = Directory.GetFiles(_root, "legacy-warm.meta.json", SearchOption.AllDirectories).Single();
        string json = File.ReadAllText(metaPath);
        json = json.TrimEnd().TrimEnd('}') + ""","fields":{"legacy":["beta"]}}""";
        File.WriteAllText(metaPath, json);

        // Cold-load through the real production path (LoadIndexUnsafe) to warm the cache — a
        // hand-seeded cache would not prove LoadIndexUnsafe's MigratesTo-guarded write-back can
        // actually leave LegacyFields behind; this reproduces it for real.
        var warmed = _store.LoadIndex("local").Single(e => e.Name == "legacy-warm");
        warmed.LegacyFields.Should().NotBeNull(
            because: "sanity check on the scenario setup itself: if this is null, the seeded "
                   + "sidecar did not reproduce MigratesTo reporting no change, and the assertions "
                   + "below would be proving nothing about the fix under test");

        bool ok = _store.TryUpdateEntry("legacy-warm", "local", e => e with { Uri = "patched" });
        ok.Should().BeTrue();

        var updated = AssertReadIsACacheHit(
            () => _store.LoadIndex("local").Single(e => e.Name == "legacy-warm"),
            because: "TryUpdateEntry above must have taken the warm copy-on-write branch (the "
                   + "cache was warmed by the LoadIndex call above) — recreation of the lock file "
                   + "here would mean this read cold-reloaded instead of exercising the warm "
                   + "cache-write logic under test");
        updated.LegacyFields.Should().BeNull(
            because: "TryUpdateEntry reads the entry back through a load seam and normalizes what "
                   + "it caches, same as Upsert does — leaving it raw would let a "
                   + "LegacyFields-bearing entry sit in the cache diverging from disk (which "
                   + "WriteSidecarCore always normalizes before serializing), reproducing exactly "
                   + "the divergence the load seam exists to prevent");
        updated.Uri.Should().Be("patched");
    }
}
