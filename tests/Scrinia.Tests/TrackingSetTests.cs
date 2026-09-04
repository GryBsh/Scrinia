using FluentAssertions;
using Scrinia.Core;
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

    [Fact]
    public void Set_RetainsBothWriteAndRollbackFailuresAndLeavesRecoverableJournal()
    {
        string directory = Path.Combine(_workspace, ".scrinia");
        Directory.CreateDirectory(Path.Combine(directory, "memories"));
        WorkspaceConfig.SetValue(_workspace, WorkspaceConfig.TrackingKey, "local");
        string ignorePath = Path.Combine(directory, ".gitignore");
        File.WriteAllText(ignorePath, ManagedIgnoreBlock.Local);
        DurableFile.FailureInjector = (_, path) => path.EndsWith(".gitignore", StringComparison.Ordinal)
            ? new IOException("managed-ignore write failed") : null;

        LegacyMigrationResult result;
        try
        {
            result = new LegacyMigrationService().Set(_workspace, TrackingPolicy.ProjectGit);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
        result.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        result.Changed.Should().BeFalse();
        result.Exception.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(2);
        File.Exists(Path.Combine(directory, ".tracking-recovery.json")).Should().BeTrue();

        LegacyFilesystemObservation recovered = new LegacyMigrationService().Inspect(_workspace);

        recovered.ErrorCode.Should().Be(ConfigurationErrorCode.None);
        WorkspaceConfig.Load(_workspace).GetTrackingPolicy().Should().Be(TrackingPolicy.Local);
        File.ReadAllText(ignorePath).Should().Be(ManagedIgnoreBlock.Local);
        File.Exists(Path.Combine(directory, ".tracking-recovery.json")).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
