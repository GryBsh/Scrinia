using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class WorkspaceConfigScalarTests
{
    [Fact]
    public void AbsentEnabledMeansNoEnabledBases()
    {
        WorkspaceConfig.ParseEnabled(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("team;;architecture")]
    [InlineData(";team")]
    [InlineData("team;")]
    [InlineData("Team")]
    [InlineData("team_name")]
    [InlineData("team;team")]
    public void InvalidEnabledValuesAreRejected(string value)
    {
        Action parse = () => WorkspaceConfig.ParseEnabled(value);

        parse.Should().Throw<ScriniaConfigurationException>();
    }

    [Fact]
    public void EnabledValuesPreserveDeclaredOrderAndSpelling()
    {
        WorkspaceConfig.ParseEnabled("team;architecture")
            .Should().Equal("team", "architecture");
    }

    [Theory]
    [InlineData("local", TrackingPolicy.Local)]
    [InlineData("project-git", TrackingPolicy.ProjectGit)]
    public void TrackingAcceptsOnlySupportedPolicies(string value, TrackingPolicy expected)
    {
        WorkspaceConfig.ParseTracking(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("Local")]
    [InlineData("project_git")]
    public void InvalidTrackingValuesAreRejected(string value)
    {
        Action parse = () => WorkspaceConfig.ParseTracking(value);

        parse.Should().Throw<ScriniaConfigurationException>();
    }
}
