using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the read path's normalized-cache contract.
/// </summary>
public class LoadIndexCacheHitTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 10, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void LoadIndex_OnACacheHit_ReturnsTheCachedNormalizedEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("warm"), "local");

        // Task 2 (write-path cost): Upsert now updates the cache copy-on-write instead of
        // rebuilding it, and — per that change's "never populate a partial cache" rule — a write
        // against a scope that was NOT already cached leaves the cache absent rather than warming
        // it. This is the first write to "local", so the scope is still uncached at this point.
        // Warm it explicitly with one LoadIndex call, so the read this test actually measures
        // below is a genuine cache hit.
        store.LoadIndex("local").Should().ContainSingle();

        var cached = store.LoadIndex("local");

        cached.Should().ContainSingle("the cache still answers the read");
        ReferenceEquals(store.LoadIndex("local"), cached).Should().BeTrue(
            because: "the cache publishes one normalized entry list until a write replaces it");
    }

    [Fact]
    public void LoadIndex_OnAColdRead_StillTakesTheSharedFileLock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        store.Upsert(Entry("cold"), "local");

        // A second store over the same workspace has an empty _indexCache, so this read is cold
        // and must go through the lock — the fast path must not swallow the lock outright.
        using var fresh = new FileMemoryStore(store.WorkspaceRoot);
        string lockPath = Path.Combine(store.GetStoreDirForScope("local"), ".lock");
        File.Delete(lockPath);

        fresh.LoadIndex("local").Should().ContainSingle();

        File.Exists(lockPath).Should().BeTrue(
            because: "a cold read reads sidecars from disk and still needs the shared lock");
    }

    [Fact]
    public void LoadIndex_AfterAWriteThroughTheSameStore_SeesTheWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("first"), "local");
        store.LoadIndex("local").Should().ContainSingle();

        store.Upsert(Entry("second"), "local");

        store.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["first", "second"],
                because: "the fast path must not outlive the cache invalidation Upsert performs");
    }

    [Fact]
    public void LoadIndex_DoesNotSeeAnotherStoreInstancesWrite_WhichIsWhyTheLockBuysNothingHere()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        store.Upsert(Entry("mine"), "local");
        store.LoadIndex("local").Should().ContainSingle();   // warm the cache

        using var other = new FileMemoryStore(store.WorkspaceRoot);
        other.Upsert(Entry("theirs"), "local");

        store.LoadIndex("local").Select(e => e.Name)
            .Should().BeEquivalentTo(["mine"],
                because: "nothing invalidates _indexCache across store instances or processes, "
                       + "so a cache hit was ALREADY stale with respect to an outside write — "
                       + "with the shared lock or without it. This is pre-existing behaviour, "
                       + "pinned here because it is the whole argument for moving the lock: a "
                       + "lock cannot protect the freshness of an answer that was never fresh.");
    }
}
