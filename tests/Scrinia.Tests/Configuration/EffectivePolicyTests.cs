using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Configuration;

public class EffectivePolicyTests
{
    // ResolveEffective is a pure precedence function — it takes the three candidate values as
    // parameters rather than reading the environment or the workspace file, so the resolution
    // order is the only thing under test and no StoreScope is required.

    [Fact]
    public void SingleUnderscoreWinsOverDoubleAndFile()
    {
        EffectivePolicy.ResolveEffective("local", "project-git", "local").Should().Be(TrackingPolicy.Local);
    }

    [Fact]
    public void SingleUnderscoreWinsEvenWhenDoubleIsProjectGit()
    {
        EffectivePolicy.ResolveEffective("local", "project-git", null).Should().Be(TrackingPolicy.Local);
    }

    [Fact]
    public void DoubleUnderscoreWinsOverFileWhenSingleIsNull()
    {
        EffectivePolicy.ResolveEffective(null, "project-git", "local").Should().Be(TrackingPolicy.ProjectGit);
    }

    [Fact]
    public void FileValueWinsOverUnsetWhenBothEnvValuesAreNull()
    {
        EffectivePolicy.ResolveEffective(null, null, "local").Should().Be(TrackingPolicy.Local);
    }

    [Fact]
    public void AllNullResolvesToUnset()
    {
        EffectivePolicy.ResolveEffective(null, null, null).Should().Be(TrackingPolicy.Unset);
    }

    [Theory]
    [InlineData("project-git")]
    public void ValidProjectGitValueResolves(string value)
    {
        EffectivePolicy.ResolveEffective(value, null, null).Should().Be(TrackingPolicy.ProjectGit);
    }

    [Fact]
    public void MalformedFileValueThrows()
    {
        Action action = () => EffectivePolicy.ResolveEffective(null, null, "bogus");
        action.Should().Throw<ScriniaConfigurationException>().WithMessage("*Tracking must be exactly*");
    }

    [Fact]
    public void MalformedSingleEnvValueThrows()
    {
        Action action = () => EffectivePolicy.ResolveEffective("bogus", null, null);
        action.Should().Throw<ScriniaConfigurationException>().WithMessage("*Tracking must be exactly*");
    }

    [Fact]
    public void MalformedDoubleEnvValueThrows()
    {
        Action action = () => EffectivePolicy.ResolveEffective(null, "bogus", null);
        action.Should().Throw<ScriniaConfigurationException>().WithMessage("*Tracking must be exactly*");
    }

    // The const names are the contract Task 5's callers depend on; pinning them here means a
    // rename surfaces as a compile error rather than a silent precedence regression.
    [Fact]
    public void ExposesThePrecedenceContractAsConstants()
    {
        EffectivePolicy.EnvSingle.Should().Be("SCRINIA_WORKSPACE_TRACKING");
        EffectivePolicy.EnvDouble.Should().Be("SCRINIA__WORKSPACE__TRACKING");
        EffectivePolicy.FileKey.Should().Be("Scrinia:Workspace:Tracking");
    }
}
