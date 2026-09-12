using System.Text;
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class MixedVersionRecoveryTests
{
    [Fact]
    public void Discover_MixedLegacyAndEnvelopeClaimsForOneSubjectBlocksDeterministically()
    {
        string root = CreateRoot();
        string skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(skills);
        string legacyPath = RecoveryTestFixtures.FileEntityRecoveryPath(skills, "legacy-operation");
        string envelopePath = RecoveryTestFixtures.FileEntityRecoveryPath(skills, "envelope-operation");
        try
        {
            File.WriteAllBytes(legacyPath, RecoveryTestFixtures.BuildLegacyFileEntityRecovery());
            File.WriteAllBytes(envelopePath, BuildFileEntityEnvelope("envelope-operation"));
            byte[] retained = File.ReadAllBytes(envelopePath);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover(
                RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence.Should().NotBeNull();
            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.Ambiguous);
            result.BlockingEvidence.OperationId.Should().Be("envelope-operation");
            File.ReadAllBytes(envelopePath).Should().Equal(retained);
            File.Exists(legacyPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_NewBodyVersionBlocksAndRetainsTheOriginalBytes()
    {
        string root = CreateRoot();
        try
        {
            string path = RecoveryTestFixtures.AppendJournalPath(root);
            RecoveryEnvelopeDocument supported = new AppendRecoveryAdapter().ToEnvelope(
                AppendJournalCodec.ParseAndValidate(json: RecoveryTestFixtures.BuildLegacyAppendJournal(),
                    expectedScope: RecoveryTestFixtures.LocalNotesScope, journalPath: path));
            string body = Encoding.UTF8.GetString(RecoveryEnvelopeCodec.Serialize(supported));
            File.WriteAllText(path, body.Replace("\"bodyVersion\":1", "\"bodyVersion\":2", StringComparison.Ordinal));
            byte[] retained = File.ReadAllBytes(path);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover(
                RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.UnsupportedVersion);
            result.BlockingEvidence.OperationId.Should().Be(RecoveryTestFixtures.OperationId);
            File.ReadAllBytes(path).Should().Equal(retained);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_UnsafeEvidenceUsesUnsafeClassificationAndDoesNotMutateIt()
    {
        string root = CreateRoot();
        string agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            RecoveryTestFixtures.WriteLegacyFileEntityRecovery(agent, bodyPath: "../escape.md");
            string path = RecoveryTestFixtures.FileEntityRecoveryPath(agent);
            byte[] retained = File.ReadAllBytes(path);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover(
                RecoveryTestFixtures.LocalNotesScope, root);

            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.Unsafe);
            File.ReadAllBytes(path).Should().Equal(retained);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CommittedCleanupFailureIsRetainedAndARecoveryRestartRemovesOnlyEvidence()
    {
        string root = CreateRoot();
        string entityDirectory = Path.Combine(root, "skills");
        Directory.CreateDirectory(entityDirectory);
        string bodyPath = Path.Combine(entityDirectory, "restart.md");
        string metaPath = Path.Combine(entityDirectory, "restart.meta.json");
        File.WriteAllText(bodyPath, "before");
        File.WriteAllText(metaPath, "before metadata");
        FileEntityMutation mutation = new("skill", "restart", bodyPath, metaPath,
            Path.Combine(entityDirectory, "versions"), Encoding.UTF8.GetBytes("after"),
            Encoding.UTF8.GetBytes("after metadata"), false, false);
        try
        {
            var first = new FileEntityTransactionCoordinator(root);
            first.FailOnceAt(FileEntityFaultPoint.EvidenceCleanup);
            FileEntityMutationResult warning = await first.ExecuteAsync(mutation);

            warning.Outcome.Should().Be(WholeMemoryOutcome.SuccessWithWarning);
            warning.OperationId.Should().NotBeNullOrWhiteSpace();
            string evidencePath = warning.Recovery!.EvidencePath;
            byte[] retained = File.ReadAllBytes(evidencePath);
            File.ReadAllText(bodyPath).Should().Be("after");

            FileEntityMutationResult retry = await new FileEntityTransactionCoordinator(root).ExecuteAsync(
                new FileEntityMutation("skill", "restart", bodyPath, metaPath,
                    Path.Combine(entityDirectory, "versions"), Encoding.UTF8.GetBytes("next"),
                    Encoding.UTF8.GetBytes("next metadata"), false, false));

            retry.Recovery.Should().BeNull();
            File.Exists(evidencePath).Should().BeFalse();
            File.ReadAllBytes(bodyPath).Should().NotEqual(retained);
            File.ReadAllText(bodyPath).Should().Be("next");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildFileEntityEnvelope(string operationId)
    {
        byte[] body = RecoveryTestFixtures.FileEntityBody;
        byte[] metadata = RecoveryTestFixtures.FileEntityMetadata;
        var oldBody = new RecoveryMemberSnapshot("entity.md", true,
            Convert.ToBase64String(body), RecoveryTestFixtures.Sha256(body));
        var oldMeta = new RecoveryMemberSnapshot("entity.meta.json", true,
            Convert.ToBase64String(metadata), RecoveryTestFixtures.Sha256(metadata));
        var fileEntity = new FileEntityRecoveryBody("skill", "entity", oldBody, oldMeta,
            null, null, null, null, "prepared");
        RecoveryEnvelopeDocument document = RecoveryEnvelopeCodec.FromFileEntity(fileEntity,
            RecoveryTestFixtures.LocalNotesScope, null, operationId,
            RecoveryLifecycleState.Prepared, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return RecoveryEnvelopeCodec.Serialize(document);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-mixed-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        return root;
    }
}
