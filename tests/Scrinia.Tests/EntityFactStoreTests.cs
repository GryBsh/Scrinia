using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactStoreTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
    private static readonly DateTimeOffset D2 = DateTimeOffset.Parse("2023-02-01T00:00:00Z");

    private static MemoryEntities Mem(string id, DateTimeOffset date, params (string k, string v)[] ents) =>
        new("local", id, date, ents.Select(e => EntityRef.Of(e.k, e.v)).ToArray());

    private static EntityFactStore Store(params MemoryEntities[] mems) => new(mems);

    [Fact]
    public void Facts_ReturnsOneFactPerMentioningMemory_WithCoOccurring()
    {
        var store = Store(
            Mem("m1", D1, ("person", "jolene"), ("place", "france")),
            Mem("m2", D2, ("person", "jolene"), ("thing", "seraphim")));

        var facts = store.Facts(EntityRef.Of("person", "jolene"));

        facts.Select(f => f.MemoryId).Should().BeEquivalentTo(["m1", "m2"]);
        facts.Single(f => f.MemoryId == "m1").CoOccurring.Should().ContainSingle()
            .Which.Should().Be(EntityRef.Of("place", "france"));
    }

    [Fact]
    public void MentionCount_CountsMentioningMemories()
    {
        var store = Store(
            Mem("m1", D1, ("person", "jolene")),
            Mem("m2", D2, ("person", "jolene")),
            Mem("m3", D2, ("person", "nate")));
        store.MentionCount(EntityRef.Of("person", "jolene")).Should().Be(2);
        store.MentionCount(EntityRef.Of("person", "unknown")).Should().Be(0);
    }

    [Fact]
    public void MemoriesFor_SingleEntity_ReturnsMentions_ManyEntities_ReturnsIntersection_ZeroArgs_ReturnsEmpty()
    {
        var store = Store(
            Mem("m1", D1, ("topic", "auth"), ("topic", "billing")),
            Mem("m2", D1, ("topic", "auth")),
            Mem("m3", D1, ("topic", "billing")));

        store.MemoriesFor(EntityRef.Of("topic", "auth")).Should().BeEquivalentTo(["m1", "m2"]);
        store.MemoriesFor(EntityRef.Of("topic", "auth"), EntityRef.Of("topic", "billing"))
            .Should().BeEquivalentTo(["m1"]);          // touching ALL
        store.MemoriesFor().Should().BeEmpty();        // zero args = empty, not "everything"
    }

    [Fact]
    public void Entities_DiscoversAllOrByKey_Deterministically()
    {
        var store = Store(Mem("m1", D1, ("person", "nate"), ("person", "jolene"), ("place", "france")));

        store.Entities().Should().Equal(
            EntityRef.Of("person", "jolene"),
            EntityRef.Of("person", "nate"),
            EntityRef.Of("place", "france"));      // key asc, then value asc
        store.Entities("person").Should().Equal(
            EntityRef.Of("person", "jolene"),
            EntityRef.Of("person", "nate"));
    }
}
