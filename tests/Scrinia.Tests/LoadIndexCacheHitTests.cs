using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the read path's fast lane: an in-memory cache hit performs no filesystem work at all.
///
/// The observable proxy for "opened a file handle" is the .lock file itself. FileLock's private
/// Acquire helper opens it with FileMode.OpenOrCreate, and AcquireExclusive creates the lock's
/// parent directory before calling it, so any write that reaches the file lock recreates the
/// file. Deleting it and finding it still absent afterwards is direct evidence that no handle
/// was opened — which is stronger than timing the call, and deterministic. (Line numbers are
/// deliberately omitted here — they drift with every unrelated edit to FileLock.cs.)
/// </summary>
public class LoadIndexCacheHitTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 10, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void LoadIndex_OnACacheHit_OpensNoLockFile()
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

        string lockPath = Path.Combine(store.GetStoreDirForScope("local"), ".lock");
        File.Exists(lockPath).Should().BeTrue(
            because: "precondition: the warming LoadIndex call above must have created the lock "
                   + "file, or its absence below proves nothing — File.Delete on a missing file is "
                   + "a silent no-op");
        File.Delete(lockPath);

        store.LoadIndex("local").Should().ContainSingle("the cache still answers the read");

        File.Exists(lockPath).Should().BeFalse(
            because: "a cache hit needs no file handle — the shared lock exists to keep a COLD "
                   + "read off a half-written sidecar, and cannot make an in-process cache hit "
                   + "more correct");
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
