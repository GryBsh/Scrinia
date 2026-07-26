using FluentAssertions;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class FactsResponseFormatterTests
{
    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entities_RendersCountsAndTruncation()
    {
        var yaml = FactsResponseFormatter.ToYaml(new EntitiesListResult(
            [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("place", "france"), 5)], Total: 9, Truncated: true));
        yaml.Should().Contain("status: success").And.Contain("action: facts");
        yaml.Should().Contain("person:jolene").And.Contain("(7)");
        yaml.Should().Contain("showing 2 of 9");
    }

    [Fact]
    public void Profile_RendersSpanNeighborsTimeline()
    {
        var yaml = FactsResponseFormatter.ToYaml(new ProfileResult(
            R("person", "jolene"), 3, D1, D2,
            [new EntityCount(R("place", "france"), 2)],
            [new TimelineEntry("/f/trip", D1), new TimelineEntry("/f/solo", D2)], TimelineTruncated: false));
        yaml.Should().Contain("profile: person:jolene");
        yaml.Should().Contain("mentions: 3");
        yaml.Should().Contain("2026-01-04 → 2026-07-20");
        yaml.Should().Contain("place:france (2)");
        yaml.Should().Contain("2026-01-04").And.Contain("/f/trip");
    }

    [Fact]
    public void Intersection_RendersMembersAndMemories()
    {
        var yaml = FactsResponseFormatter.ToYaml(new IntersectionResult(
            [R("person", "jolene"), R("place", "france")],
            [new TimelineEntry("/f/trip", D1)], Count: 1));
        yaml.Should().Contain("status: success");
        yaml.Should().Contain("person:jolene").And.Contain("place:france");
        yaml.Should().Contain("memories: 1");
        yaml.Should().Contain("/f/trip");
    }

    [Fact]
    public void EmptyIntersection_IsSuccess_WithZero()
    {
        var yaml = FactsResponseFormatter.ToYaml(new IntersectionResult(
            [R("person", "jolene"), R("thing", "seraphim")], [], Count: 0));
        yaml.Should().Contain("status: success");
        yaml.Should().Contain("memories: 0");
    }

    [Fact]
    public void Disambiguation_IsWarning_WithCandidates()
    {
        var yaml = FactsResponseFormatter.ToYaml(new DisambiguationResult(
            "jolene", [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("mention", "jolene"), 2)]));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("person:jolene (7)").And.Contain("mention:jolene (2)");
    }

    [Fact]
    public void NotFound_IsWarning()
    {
        var yaml = FactsResponseFormatter.ToYaml(new NotFoundResult("zzz"));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("zzz");
    }
}
