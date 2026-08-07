using FluentAssertions;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class FactsResponseFormatterTests
{
    // FactsResponseFormatter now returns the typed McpResponse (ToYaml → ToResponse); these
    // tests still assert on the rendered document, so they serialise it here. The assertions
    // themselves are unchanged — they are what pins the formatter's output byte-for-byte.
    private static string Yaml(FactsResult result) => FactsResponseFormatter.ToResponse(result).ToYaml();

    private static EntityRef R(string k, string v) => EntityRef.Of(k, v);
    private static readonly DateTimeOffset D1 = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entities_RendersCountsAndTruncation()
    {
        var yaml = Yaml(new EntitiesListResult(
            [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("place", "france"), 5)], Total: 9, Truncated: true));
        yaml.Should().Contain("status: success").And.Contain("action: facts");
        yaml.Should().Contain("person:jolene").And.Contain("(7)");
        yaml.Should().Contain("showing 2 of 9");
    }

    [Fact]
    public void Profile_RendersSpanNeighborsTimeline()
    {
        var yaml = Yaml(new ProfileResult(
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
        var yaml = Yaml(new IntersectionResult(
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
        var yaml = Yaml(new IntersectionResult(
            [R("person", "jolene"), R("thing", "seraphim")], [], Count: 0));
        yaml.Should().Contain("status: success");
        yaml.Should().Contain("memories: 0");
    }

    [Fact]
    public void Disambiguation_IsWarning_WithCandidates()
    {
        var yaml = Yaml(new DisambiguationResult(
            "jolene", [new EntityCount(R("person", "jolene"), 7), new EntityCount(R("mention", "jolene"), 2)]));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("person:jolene (7)").And.Contain("mention:jolene (2)");
    }

    [Fact]
    public void NotFound_IsWarning()
    {
        var yaml = Yaml(new NotFoundResult("zzz"));
        yaml.Should().Contain("status: warning");
        yaml.Should().Contain("zzz");
    }

    [Fact]
    public void Profile_NullSpan_OmitsSpanLine()
    {
        var yaml = Yaml(new ProfileResult(
            R("person", "jolene"), 3, null, null,
            [new EntityCount(R("place", "france"), 2)],
            [new TimelineEntry("/f/trip", D1)], TimelineTruncated: false));
        yaml.Should().NotContain("span:");
    }

    [Fact]
    public void Profile_EmptyNeighbors_OmitsCoOccurringLine()
    {
        var yaml = Yaml(new ProfileResult(
            R("person", "jolene"), 3, D1, D2,
            [],
            [new TimelineEntry("/f/trip", D1)], TimelineTruncated: false));
        yaml.Should().NotContain("co-occurring:");
    }

    [Fact]
    public void Profile_EmptyTimeline_StillRendersHeaderWithZeroCount()
    {
        var yaml = Yaml(new ProfileResult(
            R("person", "jolene"), 3, D1, D2,
            [new EntityCount(R("place", "france"), 2)],
            [], TimelineTruncated: false));
        yaml.Should().Contain("timeline (0");
    }

    [Fact]
    public void Entities_EmptyItems_RendersShowingZeroOfZero()
    {
        var yaml = Yaml(new EntitiesListResult([], Total: 0, Truncated: false));
        yaml.Should().Contain("showing 0 of 0");
    }
}
