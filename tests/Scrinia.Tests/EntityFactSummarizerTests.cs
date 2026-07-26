using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactSummarizerTests
{
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D3 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);

    // jolene appears in 3 memories; france in 2 (both co-occur with jolene); seraphim in 1.
    private static EntityFactStore Store() => new(
    [
        new MemoryEntities("local", "m1", D1, [R("person", "jolene"), R("place", "france")]),
        new MemoryEntities("local", "m2", D2, [R("person", "jolene"), R("place", "france"), R("thing", "seraphim")]),
        new MemoryEntities("local", "m3", D3, [R("person", "jolene")]),
    ]);

    [Fact]
    public void CountSpanAndCoOccurrences_AreCorrect()
    {
        var s = EntityFactSummarizer.Summarize(Store(), [R("person", "jolene")]).Single();

        s.MentionCount.Should().Be(3);
        s.First.Should().Be(D1);
        s.Last.Should().Be(D3);
        s.TopCoOccurring.Should().ContainSingle(c => c.Entity == R("place", "france") && c.Count == 2);
        s.TopCoOccurring.Should().Contain(c => c.Entity == R("thing", "seraphim") && c.Count == 1);
    }

    [Fact]
    public void RanksByMentionCountDesc_AndCapsEntities()
    {
        var all = EntityFactSummarizer.Summarize(
            Store(),
            [R("thing", "seraphim"), R("person", "jolene"), R("place", "france")],
            maxEntities: 2);

        all.Should().HaveCount(2);
        all[0].Entity.Should().Be(R("person", "jolene")); // 3 mentions
        all[1].Entity.Should().Be(R("place", "france"));  // 2 mentions (seraphim's 1 is dropped by the cap)
    }

    [Fact]
    public void CapsCoOccurrences()
    {
        var s = EntityFactSummarizer.Summarize(Store(), [R("person", "jolene")], coOccurrenceLimit: 1).Single();
        s.TopCoOccurring.Should().HaveCount(1);
        s.TopCoOccurring[0].Entity.Should().Be(R("place", "france")); // highest co-occurrence
    }

    [Fact]
    public void UnknownEntity_IsSkipped()
    {
        EntityFactSummarizer.Summarize(Store(), [R("person", "nobody")]).Should().BeEmpty();
    }
}
