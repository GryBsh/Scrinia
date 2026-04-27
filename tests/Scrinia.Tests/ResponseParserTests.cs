using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Verifies that <see cref="ResponseParser"/> can round-trip YAML produced
/// by <see cref="ResponseBuilder"/> / <see cref="McpResponseExtensions.ToYaml"/>.
/// </summary>
public sealed class ResponseParserTests
{
    [Fact]
    public void ParseResponse_RoundTrip_AllFields()
    {
        var yaml = ResponseBuilder.Success("test content")
            .WithAction("created")
            .WithPath("goal:G-1")
            .WithInstruction("call task('next')")
            .WithActionNeeded("warn1", "warn2")
            .WithInfo("info1")
            .ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.Status.Should().Be("success");
        parsed.Action.Should().Be("created");
        parsed.Path.Should().Be("goal:G-1");
        parsed.Instruction.Should().Be("call task('next')");
        parsed.Content.Should().Contain("test content");
        parsed.ActionNeeded.Should().HaveCount(2);
        parsed.ActionNeeded.Should().ContainInOrder("warn1", "warn2");
        parsed.Info.Should().HaveCount(1);
        parsed.Info.Should().Contain("info1");
        parsed.Error.Should().BeNull();
    }

    [Fact]
    public void ParseResponse_ErrorResponse_HasErrorField()
    {
        var yaml = ResponseBuilder.Error("something broke").ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.Status.Should().Be("error");
        parsed.Error.Should().Be("something broke");
        parsed.Content.Should().BeNull();
        parsed.Action.Should().BeNull();
    }

    [Fact]
    public void ParseResponse_MinimalSuccess_DefaultsAreClean()
    {
        var yaml = ResponseBuilder.Success().ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.Status.Should().Be("success");
        parsed.Content.Should().BeNull();
        parsed.Action.Should().BeNull();
        parsed.Path.Should().BeNull();
        parsed.Instruction.Should().BeNull();
        parsed.Error.Should().BeNull();
        parsed.ActionNeeded.Should().BeEmpty();
        parsed.Info.Should().BeEmpty();
    }

    [Fact]
    public void ParseResponse_WithFollowUp_ParsesNames()
    {
        var yaml = ResponseBuilder.Success("done")
            .WithFollowUp("task('next')", "memory('search')")
            .ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.FollowUp.Should().HaveCount(2);
        parsed.FollowUp.Should().ContainInOrder("task('next')", "memory('search')");
    }

    [Fact]
    public void ParseResponse_WithoutFollowUp_EmptyList()
    {
        var yaml = ResponseBuilder.Success("no follow-up").ToYaml();

        var parsed = ResponseParser.Parse(yaml);

        parsed.FollowUp.Should().BeEmpty();
    }
}
