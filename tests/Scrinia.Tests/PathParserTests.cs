using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class PathParserTests
{
    private static readonly HashSet<string> EntityTypes = new(
        ["goal", "phase", "task", "concern", "requirement", "project", "workflow", "skill"],
        StringComparer.OrdinalIgnoreCase);

    // ── Entity / ID inference ─────────────────────────────────────────────────

    [Fact]
    public void Parse_FullEntityChain_ExtractsThreeEntityPairs()
    {
        var result = PathParser.Parse("/goal/G-5/phase/01/task/fix", EntityTypes);

        result.EntityPairs.Should().HaveCount(3);
        result.EntityPairs[0].Should().Be(new EntityIdPair("goal", "G-5"));
        result.EntityPairs[1].Should().Be(new EntityIdPair("phase", "01"));
        result.EntityPairs[2].Should().Be(new EntityIdPair("task", "fix"));
        result.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EntityFollowedByNonEntity_MixesEntityPairsAndTags()
    {
        var result = PathParser.Parse("/goal/G-5/research/frontend", EntityTypes);

        result.EntityPairs.Should().HaveCount(1);
        result.EntityPairs[0].Should().Be(new EntityIdPair("goal", "G-5"));
        result.Tags.Should().BeEquivalentTo(["research", "frontend"]);
    }

    [Fact]
    public void Parse_TwoEntityPairs_GoalAndConcern()
    {
        var result = PathParser.Parse("/goal/G-5/concern/SEC-054", EntityTypes);

        result.EntityPairs.Should().HaveCount(2);
        result.EntityPairs[0].Should().Be(new EntityIdPair("goal", "G-5"));
        result.EntityPairs[1].Should().Be(new EntityIdPair("concern", "SEC-054"));
        result.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoEntityTypes_AllSegmentsBecameTags()
    {
        var result = PathParser.Parse("/api/auth-flow", EntityTypes);

        result.EntityPairs.Should().BeEmpty();
        result.Tags.Should().BeEquivalentTo(["api", "auth-flow"]);
    }

    [Fact]
    public void Parse_AgentProfile_AgentIsNotEntityType()
    {
        var result = PathParser.Parse("/agent/profile", EntityTypes);

        result.EntityPairs.Should().BeEmpty();
        result.Tags.Should().BeEquivalentTo(["agent", "profile"]);
    }

    [Fact]
    public void Parse_SkillIsEntityType_ReturnsOneEntityPair()
    {
        var result = PathParser.Parse("/skill/qa", EntityTypes);

        result.EntityPairs.Should().HaveCount(1);
        result.EntityPairs[0].Should().Be(new EntityIdPair("skill", "qa"));
        result.Tags.Should().BeEmpty();
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WhitespaceAndDoubleSlashes_Normalized()
    {
        var result = PathParser.Parse(" /goal//G-5/ ", EntityTypes);

        result.RawPath.Should().Be("/goal/G-5");
        result.EntityPairs.Should().HaveCount(1);
        result.EntityPairs[0].Should().Be(new EntityIdPair("goal", "G-5"));
    }

    [Fact]
    public void Parse_MissingLeadingSlash_AddsLeadingSlash()
    {
        var result = PathParser.Parse("goal/G-5", EntityTypes);

        result.RawPath.Should().Be("/goal/G-5");
        result.EntityPairs.Should().HaveCount(1);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => PathParser.Parse("", EntityTypes);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_DotDotSequence_Throws()
    {
        var act = () => PathParser.Parse("/bad/../path", EntityTypes);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Colon_Throws()
    {
        var act = () => PathParser.Parse("/has:colon", EntityTypes);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Backslash_Throws()
    {
        var act = () => PathParser.Parse("/has\\backslash", EntityTypes);
        act.Should().Throw<ArgumentException>();
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleSegment_OneTagNoEntityPairs()
    {
        var result = PathParser.Parse("/single", EntityTypes);

        result.Segments.Should().HaveCount(1);
        result.EntityPairs.Should().BeEmpty();
        result.Tags.Should().BeEquivalentTo(["single"]);
    }

    [Fact]
    public void Parse_GoalWithId_LeafSegmentIsId()
    {
        var result = PathParser.Parse("/goal/G-5", EntityTypes);

        result.EntityPairs.Should().HaveCount(1);
        result.LeafSegment.Should().Be("G-5");
    }

    [Fact]
    public void Parse_IsEntityPath_TrueWhenEntityPairsExist()
    {
        var entityPath = PathParser.Parse("/goal/G-5", EntityTypes);
        entityPath.IsEntityPath.Should().BeTrue();

        var nonEntityPath = PathParser.Parse("/api/auth-flow", EntityTypes);
        nonEntityPath.IsEntityPath.Should().BeFalse();
    }

    // ── Additional coverage ───────────────────────────────────────────────────

    [Fact]
    public void Parse_WhitespaceOnly_Throws()
    {
        var act = () => PathParser.Parse("   ", EntityTypes);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_EntityTypeAtEnd_WithoutId_BecameTag()
    {
        // "goal" at the end has no following segment to pair with, so it's a tag.
        var result = PathParser.Parse("/api/goal", EntityTypes);

        result.EntityPairs.Should().BeEmpty();
        result.Tags.Should().Contain("goal");
    }

    [Fact]
    public void Parse_CaseInsensitiveEntityType_Matches()
    {
        var result = PathParser.Parse("/Goal/G-5", EntityTypes);

        result.EntityPairs.Should().HaveCount(1);
        result.EntityPairs[0].EntityType.Should().Be("Goal");
        result.EntityPairs[0].Id.Should().Be("G-5");
    }

    [Fact]
    public void Parse_SegmentsCount_MatchesExpected()
    {
        var result = PathParser.Parse("/goal/G-5/phase/01/task/fix", EntityTypes);

        // 6 parts: goal, G-5, phase, 01, task, fix
        result.Segments.Should().HaveCount(6);
    }
}
