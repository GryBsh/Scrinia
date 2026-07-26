using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class FactsQueryTests
{
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D3 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);

    // jolene: 3 memories (co-occurs france x2, seraphim x1); france: 2; seraphim: 1; also mention:jolene x1.
    private static EntityFactStore Store() => new(
    [
        new MemoryEntities("/f/trip", D1, [R("person", "jolene"), R("place", "france")]),
        new MemoryEntities("/s/mar",  D2, [R("person", "jolene"), R("place", "france"), R("thing", "seraphim")]),
        new MemoryEntities("/f/solo", D3, [R("person", "jolene")]),
        new MemoryEntities("/n/note", D2, [R("mention", "jolene")]),
    ]);

    [Fact]
    public void Resolve_KeyValue_Exact()
    {
        FactsQuery.Resolve(Store(), "person:jolene").Should().BeOfType<EntityResolution.Resolved>()
            .Which.Entity.Should().Be(R("person", "jolene"));
    }

    [Fact]
    public void Resolve_BareValue_Ambiguous_ListsCandidates()
    {
        var r = FactsQuery.Resolve(Store(), "jolene").Should().BeOfType<EntityResolution.Ambiguous>().Which;
        r.Candidates.Select(c => c.Entity).Should().BeEquivalentTo([R("person", "jolene"), R("mention", "jolene")]);
    }

    [Fact]
    public void Resolve_BareValue_Unique()
    {
        FactsQuery.Resolve(Store(), "seraphim").Should().BeOfType<EntityResolution.Resolved>()
            .Which.Entity.Should().Be(R("thing", "seraphim"));
    }

    [Fact]
    public void Resolve_NotFound()
    {
        FactsQuery.Resolve(Store(), "nobody").Should().BeOfType<EntityResolution.NotFound>();
        FactsQuery.Resolve(Store(), "person:nobody").Should().BeOfType<EntityResolution.NotFound>();
    }

    [Fact]
    public void EntitiesList_SortsByCountDesc_AndFiltersByKey()
    {
        var all = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: null, limit: 50);
        all.Items[0].Should().Be(new EntityCount(R("person", "jolene"), 3)); // highest count first
        all.Total.Should().Be(4);
        all.Truncated.Should().BeFalse();

        var people = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: "person", limit: 50);
        people.Items.Should().ContainSingle().Which.Entity.Should().Be(R("person", "jolene"));
    }

    [Fact]
    public void EntitiesList_Truncates_AtLimit()
    {
        var r = (EntitiesListResult)FactsQuery.EntitiesList(Store(), key: null, limit: 2);
        r.Items.Should().HaveCount(2);
        r.Total.Should().Be(4);
        r.Truncated.Should().BeTrue();
    }
}
