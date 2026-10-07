using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;
using Commands = Scrinia.Commands;

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
        Commands.WorkspaceSetup.OverrideRoot(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
        Commands.WorkspaceSetup.OverrideRoot(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SingleUnderscoreEnvironmentWinsOverEveryLowerPrecedenceSource()
    {
        try
        {
            Commands.WorkspaceConfig.SetValue(_root, Key, "project-git");
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "local");
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, "project-git");

            Commands.WorkspaceSetup.GetConfigValue(Key).Should().Be("project-git");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        }
    }

    [Fact]
    public void DoubleUnderscoreEnvironmentWinsOverWorkspaceConfig()
    {
        try
        {
            Commands.WorkspaceConfig.SetValue(_root, Key, "project-git");
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "local");

            Commands.WorkspaceSetup.GetConfigValue(Key).Should().Be("local");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        }
    }

    [Fact]
    public void WorkspaceConfigWinsOverDefault()
    {
        Commands.WorkspaceConfig.SetValue(_root, Key, "project-git");

        Commands.WorkspaceSetup.GetConfigValue(Key).Should().Be("project-git");
    }

    [Fact]
    public void MissingSourcesReturnTheProvidedDefault()
    {
        Commands.WorkspaceSetup.GetConfigValue(Key, "local").Should().Be("local");
    }

    [Fact]
    public void SingleUnderscoreEnvironmentWinsOverDoubleAndFileThroughInspect()
    {
        using var scope = new TestHelpers.StoreScope();
        Commands.WorkspaceConfig.SetValue(scope.WorkspaceDir, Key, "local");
        try
        {
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "project-git");
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, "local");

            string? envSingle = Environment.GetEnvironmentVariable(SingleEnvironmentKey);
            string? envDouble = Environment.GetEnvironmentVariable(DoubleEnvironmentKey);
            LegacyFilesystemObservation observation =
                new LegacyMigrationService().Inspect(scope.WorkspaceDir, envSingle, envDouble);

            observation.Input!.TrackingPolicy.Should().Be(TrackingPolicy.Local);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        }
    }

    [Fact]
    public void DoubleUnderscoreEnvironmentWinsOverMissingFileThroughInspect()
    {
        using var scope = new TestHelpers.StoreScope();
        try
        {
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, "project-git");

            string? envSingle = Environment.GetEnvironmentVariable(SingleEnvironmentKey);
            string? envDouble = Environment.GetEnvironmentVariable(DoubleEnvironmentKey);
            LegacyFilesystemObservation observation =
                new LegacyMigrationService().Inspect(scope.WorkspaceDir, envSingle, envDouble);

            observation.Input!.TrackingPolicy.Should().Be(TrackingPolicy.ProjectGit);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DoubleEnvironmentKey, null);
            Environment.SetEnvironmentVariable(SingleEnvironmentKey, null);
        }
    }

    [Fact]
    public void MalformedEnvironmentValueIsThreadedThroughInspectAsWorkspaceConfigMalformed()
    {
        using var scope = new TestHelpers.StoreScope();

        LegacyFilesystemObservation observation =
            new LegacyMigrationService().Inspect(scope.WorkspaceDir, "bogus", null);

        observation.ErrorCode.Should().Be(ConfigurationErrorCode.WorkspaceConfigMalformed);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition; the Collection suffix identifies the shared fixture contract.")]
public sealed class TrackingPrecedenceCollection
{
    public const string Name = "TrackingPrecedence";
}
