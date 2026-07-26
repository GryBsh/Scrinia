using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactStoreAnalyticsTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    private static readonly DateTimeOffset D2 = DateTimeOffset.Parse("2023-03-01T00:00:00Z");
    private static readonly DateTimeOffset D3 = DateTimeOffset.Parse("2023-05-01T00:00:00Z");

    private static MemoryEntities Mem(string id, DateTimeOffset date, params (string k, string v)[] ents) =>
        new("local", id, date, ents.Select(e => EntityRef.Of(e.k, e.v)).ToArray());

    [Fact]
    public void Timeline_OrdersByDate_AndSpanReturnsFirstLast()
    {
        var store = new EntityFactStore([
            Mem("m2", D2, ("person", "jolene")),
            Mem("m1", D1, ("person", "jolene")),
            Mem("m3", D3, ("person", "jolene")),
        ]);

        store.Timeline(EntityRef.Of("person", "jolene")).Select(f => f.MemoryId)
            .Should().Equal("m1", "m2", "m3");
        store.Span(EntityRef.Of("person", "jolene")).Should().Be((D1, D3));
        store.Span(EntityRef.Of("person", "nobody")).Should().BeNull();
    }

    [Fact]
    public void Neighbors_RanksCoOccurringEntitiesByMemoryCount()
    {
        var store = new EntityFactStore([
            Mem("m1", D1, ("person", "jolene"), ("place", "france")),
            Mem("m2", D2, ("person", "jolene"), ("place", "france")),
            Mem("m3", D3, ("person", "jolene"), ("thing", "seraphim")),
        ]);

        store.Neighbors(EntityRef.Of("person", "jolene")).Should().Equal(
            (EntityRef.Of("place", "france"), 2),
            (EntityRef.Of("thing", "seraphim"), 1));
    }
}
