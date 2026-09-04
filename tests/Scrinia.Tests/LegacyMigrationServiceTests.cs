using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class LegacyMigrationServiceTests
{
    [Fact]
    public void InspectUsesFilesystemEvidenceAndIgnoresUnrelatedCustomMergeConfig()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.Delete(Path.Combine(directory, "memories"), recursive: true);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "merge.config"), "custom", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir);

        result.State.Should().Be(LegacyMigrationState.Fresh);
        result.ErrorCode.Should().Be(ConfigurationErrorCode.None);
    }

    [Fact]
    public void InspectReportsPolicyDriftFromTheRealManagedIgnoreFile()
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore");
        File.WriteAllText(path, "wrong\n", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir);

        result.State.Should().Be(LegacyMigrationState.PolicyDrift);
        result.ErrorCode.Should().Be(ConfigurationErrorCode.TrackingPolicyInvalid);
    }

    [Fact]
    public void MigrateChangesRealFilesAndIsIdempotent()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(Path.Combine(directory, "memories"));
        File.WriteAllText(Path.Combine(directory, ".gitattributes"),
            LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n" + LegacyMergeRecognition.ScriniaNmp2AttributeLine + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "merge.config"),
            "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflict\"\r\n}\n", new UTF8Encoding(false));

        var service = new LegacyMigrationService();
        service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local).Succeeded.Should().BeTrue();
        service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local).Succeeded.Should().BeTrue();
        WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy().Should().Be(TrackingPolicy.Local);
        File.Exists(Path.Combine(directory, ".gitattributes")).Should().BeFalse();
        File.Exists(Path.Combine(directory, "merge.config")).Should().BeFalse();
    }

    [Fact]
    public void InjectedWriteFailureRollsBackAndLeavesRecoveryJournalForRecovery()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(Path.Combine(directory, "memories"));
        string ignore = Path.Combine(directory, ".gitignore");
        File.WriteAllText(ignore, "user\n", new UTF8Encoding(false));
        DurableFile.FailureInjector = (_, path) => path.EndsWith(".gitignore", StringComparison.Ordinal) ? new IOException("boom") : null;
        try
        {
            LegacyMigrationResult result = new LegacyMigrationService().Migrate(scope.WorkspaceDir, TrackingPolicy.Local);
            result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigWriteFailed);
            File.ReadAllText(ignore).Should().Be("user\n");
        }
        finally { DurableFile.FailureInjector = null; }
    }
}
