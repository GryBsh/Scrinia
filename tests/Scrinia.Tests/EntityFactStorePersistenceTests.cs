using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public sealed class EntityFactStorePersistenceTests
{
    private static ArtifactEntry Entry(string name, DateTimeOffset created,
        string[]? keywords = null, Dictionary<string, string[]>? attributes = null) =>
        new(Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: created, Keywords: keywords, Attributes: attributes);

    [Fact]
    public async Task GetEntityFactStore_AggregatesAcrossScopes_AndWritesSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"] }), "local");
        store.Upsert(Entry("b", d, attributes: new() { ["person"] = ["maria"] }), "local:findings");

        var facts = store.GetEntityFactStore();
        facts.MentionCount(EntityRef.Of("person", "maria")).Should().Be(2); // cross-scope merge
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "facts.json")).Should().BeTrue();
    }

    [Fact]
    public async Task GetEntityFactStore_RebuildsWhenSidecarTokenStale()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        _ = store.GetEntityFactStore(); // queues facts.json
        // Corrupt the sidecar with a stale token; the index no longer matches it.
        string sidecar = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        File.WriteAllText(sidecar, EntityFactSidecar.Serialize("STALE_TOKEN", []));

        var facts = store.GetEntityFactStore(); // must rebuild from entries, not trust the file
        facts.MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);
    }

    [Fact]
    public async Task RebuiltFacts_ClearDirtyStateOnlyAfterDrainFlushesSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("a", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["auth"] }), "local");

        _ = store.GetEntityFactStore();

        await Task.Delay(100);
        store.HasDirtyFactCache.Should().BeFalse();
    }

    [Fact]
    public void Mutation_InvalidatesCachedMergedView()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);

        store.Upsert(Entry("b", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(2); // fresh
    }

    [Fact]
    public async Task DirtyFactFlush_DrainsQueuedWritesForEveryScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.UtcNow;
        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["a"] }), "local:a");
        store.Upsert(Entry("b", d, attributes: new() { ["topic"] = ["b"] }), "local:b");

        string factsA = Path.Combine(store.GetStoreDirForScope("local:a"), "facts.json");
        string factsB = Path.Combine(store.GetStoreDirForScope("local:b"), "facts.json");
        Directory.CreateDirectory(factsA);
        Directory.CreateDirectory(factsB);

        _ = store.GetEntityFactStore();
        Directory.Delete(factsA);
        Directory.Delete(factsB);

        await EventuallyAsync(
            () => File.Exists(factsA) && File.Exists(factsB),
            TimeSpan.FromSeconds(3));

        File.Exists(factsA).Should().BeTrue();
        File.Exists(factsB).Should().BeTrue();
    }

    [Fact]
    public async Task DirtyFactFlush_PersistsNewestGenerationForAQueuedPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string factsPath = Path.Combine(store.GetStoreDirForScope("local:a"), "facts.json");
        Directory.CreateDirectory(factsPath);

        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["old"] }), "local:a");
        _ = store.GetEntityFactStore();

        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["newest"] }), "local:a");
        _ = store.GetEntityFactStore();

        Directory.Delete(factsPath);
        await EventuallyAsync(() => SidecarContains(factsPath, "newest"), TimeSpan.FromSeconds(3));

        SidecarContains(factsPath, "newest").Should().BeTrue();
    }

    [Fact]
    public async Task DirtyFactFlush_RestartsQueuedWriteAfterDrainCancellation()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["restart"] }), "local");

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        Directory.CreateDirectory(factsPath);
        _ = store.GetEntityFactStore();

        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(250))).Should().BeFalse();
        store.HasDirtyFactCache.Should().BeTrue(
            because: "a canceled drain must leave the unavailable sidecar queued and dirty");

        Directory.Delete(factsPath);
        await EventuallyAsync(() => File.Exists(factsPath), TimeSpan.FromSeconds(3));

        File.Exists(factsPath).Should().BeTrue(
            because: "cancelling a retrying facts flush must requeue it for a fresh worker token");
        store.HasDirtyFactCache.Should().BeFalse();
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static bool SidecarContains(string path, string text)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Regression for a lock-ordering deadlock: GetEntityFactStore used to hold _factStoreLock
    /// across ListScoped/LoadIndex (which take the per-scope index lock) — order
    /// _factStoreLock -&gt; index-lock. Upsert/Remove take the index write-lock first, then
    /// _factStoreLock -&gt; the opposite order. A query racing a mutation on the same scope could
    /// deadlock. Time-bounded (Stopwatch controls loop duration only, not the assertion) so a
    /// regression fails fast instead of hanging the suite.
    /// </summary>
    [Fact]
    public void ConcurrentQueryAndMutation_DoesNotDeadlock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        store.Upsert(Entry("seed", d, attributes: new() { ["topic"] = ["auth"] }), "local");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var runFor = TimeSpan.FromSeconds(1);

        var tasks = new List<Task>();
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (stopwatch.Elapsed < runFor)
                    store.GetEntityFactStore();
            }));
        }
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                while (stopwatch.Elapsed < runFor)
                {
                    store.Upsert(Entry($"mut{idx}", d, attributes: new() { ["topic"] = ["auth"] }), "local");
                    store.Remove($"mut{idx}", "local");
                }
            }));
        }

        bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(15));
        completed.Should().BeTrue("a concurrent GetEntityFactStore + Upsert/Remove race on the same scope must not deadlock");
    }
}
