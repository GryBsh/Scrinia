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
        _store.Upsert(E("b"), "local");
        _store.Upsert(E("c"), "local");
        _store.Remove("b", "local");

        var warm = _store.LoadIndex("local").Select(e => e.Name).ToArray();

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
        // ArtifactEntry's positional members are (Name, Uri, OriginalBytes, ChunkCount,
        // CreatedAt). There is NO Description property — assert on a member that exists.
        var updated = new ArtifactEntry("a", "", 99, 1, DateTimeOffset.UtcNow);
        _store.Upsert(updated, "local");

        var entries = _store.LoadIndex("local").Where(e => e.Name == "a").ToList();
        entries.Should().HaveCount(1);
        entries[0].OriginalBytes.Should().Be(99);
    }

    [Fact]
    public void Remove_DropsTheEntryFromTheCache()
    {
        _store.Upsert(E("a"), "local");
        _store.Upsert(E("b"), "local");
        _store.Remove("a", "local");

        _store.LoadIndex("local").Select(e => e.Name).Should().BeEquivalentTo(["b"]);
    }
}
