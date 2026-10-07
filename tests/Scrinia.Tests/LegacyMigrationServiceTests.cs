using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class LegacyMigrationServiceTests
{
    [Fact]
    public void MigratePreservesConfiguredLegacyPolicyDriftRefusal()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Assert.True(new LegacyMigrationService().Set(scope.WorkspaceDir, TrackingPolicy.Local).Succeeded);
        string attributes = Path.Combine(directory, ".gitattributes");
        File.WriteAllText(attributes, LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n");
        var paths = new[] { "config.json", ".gitignore", ".gitattributes", "merge.config" };
        var before = paths.ToDictionary(name => name, name => File.Exists(Path.Combine(directory, name))
            ? File.ReadAllBytes(Path.Combine(directory, name)) : null);
        var result = new LegacyMigrationService().Migrate(scope.WorkspaceDir, TrackingPolicy.ProjectGit);
        Assert.Equal(ConfigurationErrorCode.TrackingPolicyInvalid, result.ErrorCode);
        Assert.False(result.Changed);
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, File.Exists(Path.Combine(directory, name)) ? File.ReadAllBytes(Path.Combine(directory, name)) : null);
        Assert.False(File.Exists(Path.Combine(directory, ".tracking-recovery.json")));
    }

    [Fact]
    public void InspectUsesFilesystemEvidenceAndIgnoresUnrelatedCustomMergeConfig()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.Delete(Path.Combine(directory, "memories"), recursive: true);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "merge.config"), "custom", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

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

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

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

        new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null).State.Should().Be(LegacyMigrationState.LegacyClean);
    }

    [Fact]
    public void ConfiguredPolicyWithLegacyAndCustomContentIsMixed()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        File.WriteAllText(Path.Combine(directory, ".gitignore"), ManagedIgnoreBlock.Local, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), LegacyMergeRecognition.ScriniaMetaAttributeLine + "\ncustom\n", new UTF8Encoding(false));
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");

        new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null).State.Should().Be(LegacyMigrationState.LegacyMixed);
    }

    [Fact]
    public void InspectUsesPersistedConfigDespiteAnOldWorkspaceConfigCache()
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceConfig.SetValue(scope.WorkspaceDir, WorkspaceConfig.TrackingKey, "local");
        string configPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "config.json");
        File.WriteAllText(configPath, "{\"Scrinia:Workspace:Tracking\":\"project-git\"}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore"), ManagedIgnoreBlock.ProjectGit, new UTF8Encoding(false));

        new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null).State.Should().Be(LegacyMigrationState.ProjectGit);
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

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

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

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

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

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

        result.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
        File.Exists(config).Should().BeTrue();
    }

    [Fact]
    public void RecoveryJournalRejectsCaseVariantTargetNamesBeforeMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string config = Path.Combine(directory, "config.json");
        File.WriteAllText(config, "original", new UTF8Encoding(false));
        string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        string[] targets = ["CONFIG.JSON", ".gitignore", ".gitattributes", "merge.config"];
        File.WriteAllText(Path.Combine(directory, ".tracking-recovery.json"),
            "prepared\n" + string.Join('\n', targets.Select(name => $"{Encode(Path.Combine(directory, name))}|-")) + "\n",
            new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

        result.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
        File.ReadAllText(config).Should().Be("original");
    }

    [Fact]
    public void CorruptRecoveryJournalMapsConsistentlyAcrossInspectAndMigrate()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        File.WriteAllText(Path.Combine(directory, ".tracking-recovery.json"), "prepared\nnot-base64|-\n", new UTF8Encoding(false));
        var service = new LegacyMigrationService();

        LegacyFilesystemObservation inspection = service.Inspect(scope.WorkspaceDir, null, null);
        LegacyMigrationResult migration = service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        migration.ErrorCode.Should().Be((ConfigurationErrorCode)inspection.ErrorCode);
        migration.ExitClass.Should().Be(inspection.ExitClass);
        inspection.ErrorCode.Should().NotBe(ConfigurationErrorCode.None);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("complete\nnot-base64|-")]
    [InlineData("complete\nprepared")]
    public void CompletedJournalWithTrailingContentIsRejectedByInspectAndMigrate(string journalContent)
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        string managed = Path.Combine(directory, ".gitignore");
        File.WriteAllText(managed, "managed\n", new UTF8Encoding(false));
        string journal = Path.Combine(directory, ".tracking-recovery.json");
        File.WriteAllText(journal, journalContent, new UTF8Encoding(false));
        var service = new LegacyMigrationService();

        LegacyFilesystemObservation inspection = service.Inspect(scope.WorkspaceDir, null, null);
        LegacyMigrationResult migration = service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        inspection.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
        inspection.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        migration.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
        migration.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        File.ReadAllText(managed).Should().Be("managed\n");
        File.ReadAllText(journal).Should().Be(journalContent);
    }

    [Fact]
    public void LegacyCompleteEvidenceIsRetained()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(directory);
        string managed = Path.Combine(directory, ".gitignore");
        File.WriteAllText(managed, ManagedIgnoreBlock.Local, new UTF8Encoding(false));
        string config = Path.Combine(directory, "config.json");
        File.WriteAllText(config, "{\"Scrinia:Workspace:Tracking\":\"local\"}", new UTF8Encoding(false));
        string journal = Path.Combine(directory, ".tracking-recovery.json");
        File.WriteAllText(journal, "complete\n", new UTF8Encoding(false));

        LegacyFilesystemObservation result = new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
        result.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        File.ReadAllText(journal).Should().Be("complete\n");
        File.ReadAllText(managed).Should().Be(ManagedIgnoreBlock.Local);
        File.ReadAllText(config).Should().Be("{\"Scrinia:Workspace:Tracking\":\"local\"}");
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

        Task[] inspections = Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.Inspect(scope.WorkspaceDir, null, null))).ToArray();
        Task migration = Task.Run(() => service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local));
        await Task.WhenAll(inspections.Append(migration));

        service.Inspect(scope.WorkspaceDir, null, null).ErrorCode.Should().Be(ConfigurationErrorCode.None);
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
            result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigRecoveryRequired);
            File.ReadAllText(ignore).Should().Be("user\n");
        }
        finally { DurableFile.FailureInjector = null; }
    }
}
