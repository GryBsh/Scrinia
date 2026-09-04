using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class TrackingSetTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "scrinia-task7-set", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Set_PerformsThePolicyAndManagedIgnoreTransaction()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".scrinia", "memories"));

        LegacyMigrationResult result = new LegacyMigrationService().Set(_workspace, TrackingPolicy.ProjectGit);

        result.Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(_workspace).GetTrackingPolicy().Should().Be(TrackingPolicy.ProjectGit);
        File.ReadAllText(Path.Combine(_workspace, ".scrinia", ".gitignore")).Should().Be(ManagedIgnoreBlock.ProjectGit);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
