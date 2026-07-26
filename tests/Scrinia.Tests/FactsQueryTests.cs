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
        // Assert ordering: person:jolene (count 3) before mention:jolene (count 1)
        r.Candidates.Select(c => c.Entity).Should().Equal(R("person", "jolene"), R("mention", "jolene"));
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
        // Assert full ordering: count desc (3, 2, 1, 1), then key ordinal for ties (mention < thing)
        all.Items.Select(i => i.Entity).Should().Equal(
            R("person", "jolene"),
            R("place", "france"),
            R("mention", "jolene"),
            R("thing", "seraphim"));
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

    [Fact]
    public void Profile_ReturnsCountSpanNeighborsAndTimeline()
    {
        var p = (ProfileResult)FactsQuery.Profile(Store(), "person:jolene", timelineLimit: 50);
        p.Entity.Should().Be(R("person", "jolene"));
        p.MentionCount.Should().Be(3);
        p.First.Should().Be(D1);
        p.Last.Should().Be(D3);
        p.Neighbors.Should().Contain(new EntityCount(R("place", "france"), 2));
        // timeline chronological by date then id
        p.Timeline.Select(t => t.MemoryId).Should().Equal("/f/trip", "/s/mar", "/f/solo");
        p.TimelineTruncated.Should().BeFalse();
    }

    [Fact]
    public void Profile_TimelineTruncates_AtLimit()
    {
        var p = (ProfileResult)FactsQuery.Profile(Store(), "person:jolene", timelineLimit: 2);
        p.Timeline.Should().HaveCount(2);
        p.TimelineTruncated.Should().BeTrue();
    }

    [Fact]
    public void Profile_BareAmbiguous_ReturnsDisambiguation()
    {
        FactsQuery.Profile(Store(), "jolene", 50).Should().BeOfType<DisambiguationResult>()
            .Which.Candidates.Select(c => c.Entity).Should().Contain(R("mention", "jolene"));
    }

    [Fact]
    public void Profile_NotFound()
    {
        FactsQuery.Profile(Store(), "person:nobody", 50).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Intersect_ReturnsMemoriesMentioningAll_WithDates()
    {
        var r = (IntersectionResult)FactsQuery.Intersect(Store(), ["person:jolene", "place:france"]);
        r.Count.Should().Be(2);
        r.Memories.Select(m => m.MemoryId).Should().Equal("/f/trip", "/s/mar"); // date asc then id
        r.Memories[0].Date.Should().Be(D1);
    }

    [Fact]
    public void Intersect_EmptyResult_WhenNoSharedMemory()
    {
        var r = (IntersectionResult)FactsQuery.Intersect(Store(), ["thing:seraphim", "person:jolene"]);
        r.Count.Should().Be(1); // only /s/mar has both
        var none = (IntersectionResult)FactsQuery.Intersect(Store(), ["thing:seraphim", "place:france"]);
        none.Count.Should().Be(1); // /s/mar again
    }

    [Fact]
    public void Intersect_AmbiguousMember_ReturnsDisambiguation()
    {
        FactsQuery.Intersect(Store(), ["jolene", "place:france"]).Should().BeOfType<DisambiguationResult>();
    }

    [Fact]
    public void Intersect_NotFoundMember_ReturnsNotFound()
    {
        FactsQuery.Intersect(Store(), ["person:nobody", "place:france"]).Should().BeOfType<NotFoundResult>();
    }
}
