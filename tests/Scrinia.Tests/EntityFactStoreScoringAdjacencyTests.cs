using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactStoreScoringAdjacencyTests
{
    private static EntityFactStore Store() => new(new[]
    {
        new MemoryEntities("local", "m1", default,
            [EntityRef.Of("person", "a"), EntityRef.Of("thing", "shared")]),
        new MemoryEntities("local", "m2", default,
            [EntityRef.Of("thing", "shared"), EntityRef.Of("place", "paris")]),
    });

    [Fact]
    public void ScoringAdjacency_KeysEntitiesByTypeValue_AndDocsByScoringKey()
    {
        var store = new EntityFactStore(new[]
        {
            new MemoryEntities("local", "m1", default, [EntityRef.Of("person", "a")]),
            new MemoryEntities("local:memory/api", "m2", default,
                [EntityRef.Of("person", "a"), EntityRef.Of("place", "paris")]),
        });

        var (e2d, d2e) = store.ScoringAdjacency();

        e2d.Should().ContainKey("person:a");
        e2d.Should().ContainKey("place:paris");
        e2d["person:a"].Should().BeEquivalentTo(new[] { "local|m1", "local:memory/api|m2" });
        d2e.Should().ContainKey("local:memory/api|m2");
        d2e["local:memory/api|m2"].Should().BeEquivalentTo(new[] { "person:a", "place:paris" });
    }

    [Fact]
    public void ScoringAdjacency_ReusesMaterializedMaps_AndPreservesScorerOutput()
    {
        var store = Store();
        var first = store.ScoringAdjacency();
        var second = store.ScoringAdjacency();

        ReferenceEquals(first.EntityToDocs, second.EntityToDocs).Should().BeTrue();
        ReferenceEquals(first.DocToEntities, second.DocToEntities).Should().BeTrue();

        var seeds = EntityQuerySeeder.Seed("a", first.EntityToDocs.Keys);
        PersonalizedPageRank.Score(first.EntityToDocs, first.DocToEntities, seeds, 0.15, 20)
            .Should().BeEquivalentTo(
                PersonalizedPageRank.Score(second.EntityToDocs, second.DocToEntities, seeds, 0.15, 20));
    }

    [Fact]
    public async Task ScoringAdjacency_ConcurrentFirstAccess_ReturnsOneMaterializedResult()
    {
        var store = Store();

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(store.ScoringAdjacency)));

        results.Should().OnlyContain(result => ReferenceEquals(result.EntityToDocs, results[0].EntityToDocs)
            && ReferenceEquals(result.DocToEntities, results[0].DocToEntities));
    }

    [Fact]
    public void ScoringAdjacency_DoesNotShareMaterializedMapsAcrossStores()
    {
        var first = Store().ScoringAdjacency();
        var second = Store().ScoringAdjacency();

        ReferenceEquals(first.EntityToDocs, second.EntityToDocs).Should().BeFalse();
        ReferenceEquals(first.DocToEntities, second.DocToEntities).Should().BeFalse();
    }
}
