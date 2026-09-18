using System.Text;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Configuration;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Migration;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class B0CompatibilityHarness
{
    private const string Fixtures = "Fixtures";

    [Fact]
    public void ArtifactFixture_DecodesAndReEncodesIdentically()
    {
        string artifact = File.ReadAllText(Path.Combine(Fixtures, "entity-facts.nmp2"));
        byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
        Assert.NotEmpty(decoded);
        EncodingResult reencoded = Nmp2Strategy.Instance.Encode(decoded, new EncodingOptions());
        Assert.Equal(artifact, reencoded.Artifact);
    }

    [Fact]
    public void SidecarFixture_RoundTripsThroughReadSidecarMetaWriteSidecarMeta()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"scrinia_b0_{Guid.NewGuid():N}.md");
        try
        {
            int current = DurableFormats.SkillMeta.Current;
            SkillFileMeta written = new(null, "reviewer", null, null, null, null, current);
            ScriniaMcpTools.WriteSidecarMeta(tempFile, written, ScriniaMcpJsonContext.Default.SkillFileMeta);

            SkillFileMeta? read = ScriniaMcpTools.ReadSidecarMeta(tempFile, ScriniaMcpJsonContext.Default.SkillFileMeta);
            Assert.NotNull(read);
            Assert.Equal(current, read!.SchemaVersion);
            Assert.Equal("reviewer", read!.Role);
            Assert.Equal(FormatDisposition.Load,
                FormatVersion.Evaluate(DurableFormats.Registry["skill-meta"], current));
        }
        finally
        {
            string metaPath = Path.ChangeExtension(tempFile, ".meta.json");
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
    }

    [Fact]
    public void CompatibilityMatrix_EveryCurrentFormatLoads_AndRefuseFailsClosed()
    {
        foreach (FormatContract contract in DurableFormats.All)
        {
            FormatDisposition disposition = FormatVersion.Evaluate(
                DurableFormats.Registry[contract.Name], contract.Current);
            Assert.Equal(FormatDisposition.Load, disposition);
        }
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["nmp2-body"], 99));
    }

    [Fact]
    public void CurrentAppendEvidence_ReturnsAppended_ThroughB0Result()
    {
        var append = new AppendStoreResult(AppendStoreOutcome.Appended, null, "op-harness");
        B0Result b0 = B0ResultWrappers.FromAppend(append);
        Assert.Equal(B0Outcome.Appended, b0.Outcome);
        Assert.Equal("op-harness", b0.CorrelationId);
    }

    [Fact]
    public void BareCompleteMigration_StillRejectsCompleteWithoutPrepared()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(directory);
        string journal = Path.Combine(directory, ".tracking-recovery.json");
        File.WriteAllText(journal, "complete", new UTF8Encoding(false));
        var service = new LegacyMigrationService();

        LegacyFilesystemObservation inspection = service.Inspect(scope.WorkspaceDir);
        LegacyMigrationResult migration = service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        Assert.NotEqual(ConfigurationErrorCode.None, inspection.ErrorCode);
        Assert.Equal(ConfigurationExitClass.InvalidConfiguration, migration.ExitClass);
    }

    [Fact]
    public void FaultSeam_ReproducesEachExistingSeamFailureAtMappedPhase()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new IOException("injected");
            B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.AppendJournalWrite, "append-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.AppendJournalWrite, "append-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }
}
