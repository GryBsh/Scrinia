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
    public void GetEntityFactStore_AggregatesAcrossScopes_AndWritesSidecar()
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
    public void GetEntityFactStore_RebuildsWhenSidecarTokenStale()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        _ = store.GetEntityFactStore(); // writes facts.json

        // Corrupt the sidecar with a stale token; the index no longer matches it.
        string sidecar = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        File.WriteAllText(sidecar, EntityFactSidecar.Serialize("STALE_TOKEN", []));

        var facts = store.GetEntityFactStore(); // must rebuild from entries, not trust the file
        facts.MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);
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
}
