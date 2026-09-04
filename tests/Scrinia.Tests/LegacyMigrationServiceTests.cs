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
    public void ConfiguredPolicyWithRecognizedLegacyStillRequiresLegacyCleanup()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        File.WriteAllText(Path.Combine(directory, ".gitignore"), ManagedIgnoreBlock.Local, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n", new UTF8Encoding(false));
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");

        new LegacyMigrationService().Inspect(scope.WorkspaceDir).State.Should().Be(LegacyMigrationState.LegacyClean);
    }

    [Fact]
    public void ConfiguredPolicyWithLegacyAndCustomContentIsMixed()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        File.WriteAllText(Path.Combine(directory, ".gitignore"), ManagedIgnoreBlock.Local, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), LegacyMergeRecognition.ScriniaMetaAttributeLine + "\ncustom\n", new UTF8Encoding(false));
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");

        new LegacyMigrationService().Inspect(scope.WorkspaceDir).State.Should().Be(LegacyMigrationState.LegacyMixed);
    }

    [Fact]
    public void InspectUsesPersistedConfigDespiteAnOldWorkspaceConfigCache()
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");
        string configPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "config.json");
        File.WriteAllText(configPath, "{\"Scrinia:Workspace:Tracking\":\"project-git\"}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore"), ManagedIgnoreBlock.ProjectGit, new UTF8Encoding(false));

        new LegacyMigrationService().Inspect(scope.WorkspaceDir).State.Should().Be(LegacyMigrationState.ProjectGit);
    }

    [Fact]
    public void RecoveryJournalRejectsConflictDirectoryAndPreservesIt()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string conflict = Path.Combine(directory, "conflict");
        Directory.CreateDirectory(conflict);
        string journal = Path.Combine(directory, ".tracking-recovery.json");
        string encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(conflict));
        File.WriteAllText(journal, $"prepared\n{encodedPath}|-\n", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir);

        result.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
        Directory.Exists(conflict).Should().BeTrue();
    }

    [Fact]
    public void RecoveryJournalValidFirstInvalidSecondDoesNotMutate()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string config = Path.Combine(directory, "config.json");
        File.WriteAllText(config, "original", new UTF8Encoding(false));
        string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        File.WriteAllText(Path.Combine(directory, ".tracking-recovery.json"),
            $"prepared\n{Encode(config)}|-\n{Encode(Path.Combine(directory, "memories", "entry"))}|-\n", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir);

        result.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
        File.ReadAllText(config).Should().Be("original");
    }

    [Fact]
    public void RecoveryJournalRequiresTheCompleteTargetSet()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string config = Path.Combine(directory, "config.json");
        File.WriteAllText(config, "original", new UTF8Encoding(false));
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(config));
        File.WriteAllText(Path.Combine(directory, ".tracking-recovery.json"), $"prepared\n{encoded}|-\n", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir);

        result.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
        File.Exists(config).Should().BeTrue();
    }

    [Fact]
    public void MigrateMapsMalformedWorkspaceConfigWithoutLeakingAnException()
    {
        using var scope = new TestHelpers.StoreScope();
        string config = Path.Combine(scope.WorkspaceDir, ".scrinia", "config.json");
        File.WriteAllText(config, "not-json", new UTF8Encoding(false));

        LegacyMigrationResult result = new LegacyMigrationService().Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.WorkspaceConfigMalformed);
        result.ExitClass.Should().Be(ConfigurationExitClass.InvalidConfiguration);
    }

    [Fact]
    public void MigrateMapsUnreadableManagedFileWithoutLeakingAnException()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitattributes");
        File.WriteAllBytes(path, [0xC3, 0x28]);

        LegacyMigrationResult result = new LegacyMigrationService().Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ManagedFileUnsafe);
        result.ExitClass.Should().Be(ConfigurationExitClass.PolicyFailure);
    }

    [Fact]
    public async Task ConcurrentInspectionAndMigrationUseOneStableConfigurationLock()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(Path.Combine(directory, "memories"));
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n", new UTF8Encoding(false));
        var service = new LegacyMigrationService();

        Task[] inspections = Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.Inspect(scope.WorkspaceDir))).ToArray();
        Task migration = Task.Run(() => service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local));
        await Task.WhenAll(inspections.Append(migration));

        service.Inspect(scope.WorkspaceDir).ErrorCode.Should().Be(ConfigurationErrorCode.None);
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
