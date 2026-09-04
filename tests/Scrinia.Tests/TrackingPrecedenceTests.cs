using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

[Collection(TrackingPrecedenceCollection.Name)]
public sealed class TrackingPrecedenceTests : IDisposable
{
    private const string Key = "Scrinia:Workspace:Tracking";
    private const string SingleEnvironmentKey = "SCRINIA_WORKSPACE_TRACKING";
    private const string DoubleEnvironmentKey = "SCRINIA__WORKSPACE__TRACKING";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scrinia_tracking_{Guid.NewGuid():N}");

    public TrackingPrecedenceTests()
    {
        Directory.CreateDirectory(_root);
        WorkspaceSetup.OverrideRoot(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
        WorkspaceSetup.OverrideRoot(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SingleUnderscoreEnvironmentWinsOverEveryLowerPrecedenceSource()
    {
        WorkspaceConfig.SetValue(_root, Key, "project-git");
        Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "local");
        Environment.SetEnvironmentVariable(SingleEnvironmentKey, "project-git");

        WorkspaceSetup.GetConfigValue(Key).Should().Be("project-git");
    }

    [Fact]
    public void DoubleUnderscoreEnvironmentWinsOverWorkspaceConfig()
    {
        WorkspaceConfig.SetValue(_root, Key, "project-git");
        Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "local");

        WorkspaceSetup.GetConfigValue(Key).Should().Be("local");
    }

    [Fact]
    public void WorkspaceConfigWinsOverDefault()
    {
        WorkspaceConfig.SetValue(_root, Key, "project-git");

        WorkspaceSetup.GetConfigValue(Key).Should().Be("project-git");
    }

    [Fact]
    public void MissingSourcesReturnTheProvidedDefault()
    {
        WorkspaceSetup.GetConfigValue(Key, "local").Should().Be("local");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TrackingPrecedenceCollection
{
    public const string Name = "TrackingPrecedence";
}
