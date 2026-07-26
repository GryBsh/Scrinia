using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactStoreScoringAdjacencyTests
{
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
}
