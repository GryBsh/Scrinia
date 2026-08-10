using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the read path's fast lane: an in-memory cache hit performs no filesystem work at all.
///
/// The observable proxy for "opened a file handle" is the .lock file itself. FileLock.Acquire
/// opens it with FileMode.OpenOrCreate (FileLock.cs:62) after a Directory.CreateDirectory
/// (:51), so any call that reaches the file lock recreates it. Deleting it and finding it
/// still absent afterwards is direct evidence that no handle was opened — which is stronger
/// than timing the call, and deterministic.
/// </summary>
public class LoadIndexCacheHitTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 10, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void LoadIndex_OnACacheHit_OpensNoLockFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        // Upsert both writes the sidecar and leaves the scope's cache populated
        // (FileMemoryStore.cs:982), so the very next LoadIndex is a cache hit.
        store.Upsert(Entry("warm"), "local");
        string lockPath = Path.Combine(store.GetStoreDirForScope("local"), ".lock");
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
        var store = TestHelpers.LocalStore;
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
        var store = TestHelpers.LocalStore;

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
        var store = TestHelpers.LocalStore;
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
