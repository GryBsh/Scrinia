using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class PersonalizedPageRankTests
{
    private static Dictionary<string, IReadOnlyCollection<string>> Map(
        params (string Key, string[] Vals)[] rows) =>
        rows.ToDictionary(r => r.Key, r => (IReadOnlyCollection<string>)r.Vals);

    [Fact]
    public void Score_DiffusesFromSeed_AcrossSharedEntity_AndFavoursTheSeededDoc()
    {
        // person:a in d1; person:a & person:b in d2; person:b in d3. Seed person:a.
        var e2d = Map(("person:a", ["d1", "d2"]), ("person:b", ["d2", "d3"]));
        var d2e = Map(("d1", ["person:a"]), ("d2", ["person:a", "person:b"]), ("d3", ["person:b"]));

        var mass = PersonalizedPageRank.Score(e2d, d2e, ["person:a"], 0.15, 20);

        mass.Should().ContainKeys("d1", "d2", "d3");
        mass["d3"].Should().BeGreaterThan(0);            // reached via the shared person:b bridge
        mass["d1"].Should().BeGreaterThan(mass["d3"]);    // closer to the seed than the bridged doc
    }

    [Fact]
    public void Score_NoSeeds_ReturnsEmpty()
    {
        var e2d = Map(("person:a", ["d1"]));
        var d2e = Map(("d1", ["person:a"]));
        PersonalizedPageRank.Score(e2d, d2e, [], 0.15, 20).Should().BeEmpty();
    }
}
