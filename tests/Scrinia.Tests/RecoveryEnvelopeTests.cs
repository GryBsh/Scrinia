using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class RecoveryEnvelopeTests
{
    [Fact]
    public void RecoveryOperation_HasExactlyTheSupportedOperations() =>
        Enum.GetValues<RecoveryOperation>().Should().Equal(
            RecoveryOperation.Restore,
            RecoveryOperation.Append,
            RecoveryOperation.FileEntity);

    [Fact]
    public void RecoveryLifecycleState_HasExactlyTheSupportedStates() =>
        Enum.GetValues<RecoveryLifecycleState>().Should().Equal(
            RecoveryLifecycleState.Prepared,
            RecoveryLifecycleState.Applying,
            RecoveryLifecycleState.Committed,
            RecoveryLifecycleState.CleanupPending,
            RecoveryLifecycleState.Cleaned,
            RecoveryLifecycleState.RecoveryRequired,
            RecoveryLifecycleState.RolledBack);

    [Fact]
    public void RecoveryBlock_RequiresItsScopeEvidencePathAndReason()
    {
        FluentActions.Invoking(() => new RecoveryBlock(
                " ", "evidence", RecoveryOperation.Append, "unknown",
                RecoveryBlockReason.Malformed, "detail"))
            .Should().Throw<ArgumentException>();

        FluentActions.Invoking(() => new RecoveryBlock(
                "local:notes", " ", RecoveryOperation.Append, "unknown",
                RecoveryBlockReason.Malformed, "detail"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecoveryOutcome_RequiresAValidatedOperationId()
    {
        FluentActions.Invoking(() => new RecoveryOutcome(
                false, false, false, "local:notes", "evidence", " ", null, null))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DurableFormats_AddsOnlyTheCommonRecoveryEnvelopeContract()
    {
        DurableFormats.All.Should().ContainSingle(contract => contract.Name == "recovery-envelope");
        DurableFormats.RecoveryEnvelope.Should().Be(
            new FormatContract("recovery-envelope", 1, null,
                FormatDisposition.Refuse, FormatDisposition.Refuse));
        DurableFormats.All.Select(contract => contract.Name)
            .Should().ContainInOrder("nmp2-body", "restore-journal", "append-journal", "recovery-envelope");
    }

    [Fact]
    public void LegacyFixtures_PreserveEachEvidenceLocationAndBytes()
    {
        RecoveryTestFixtures.BuildLegacyRestoreJournal().Should().NotBeEmpty();
        RecoveryTestFixtures.BuildLegacyAppendJournal().Should().NotBeEmpty();
        RecoveryTestFixtures.BuildLegacyFileEntityRecovery().Should().NotBeEmpty();

        RecoveryTestFixtures.RestoreJournalPath("scope")
            .Should().Be(Path.Combine("scope", ".restore-journal.json"));
        RecoveryTestFixtures.AppendJournalPath("scope")
            .Should().Be(Path.Combine("scope", "versions", ".append-journal.json"));
        RecoveryTestFixtures.FileEntityRecoveryPath("entity")
            .Should().Be(Path.Combine("entity", ".file-entity-01JOURNAL000000000000000001.recovery"));
        RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityBody)
            .Should().Be("6c0143617a9d0759f78edaec176588f3e7a92ff37d0c2ae3fd39213c77007571");
        RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityMetadata)
            .Should().Be("ae4f394f95259cf29c1ca17420adeda255ec94cab289f31f3daca9d1020d21e1");
    }

    [Fact]
    public void LegacyAppendFixture_IsAcceptedByTheProductionReaderWithExactEvidence()
    {
        byte[] bytes = RecoveryTestFixtures.BuildLegacyAppendJournal();

        AppendJournal journal = AppendJournalCodec.ParseAndValidate(
            bytes, RecoveryTestFixtures.LocalNotesScope,
            RecoveryTestFixtures.AppendJournalPath("scope"));

        journal.OperationId.Should().Be(RecoveryTestFixtures.OperationId);
        journal.Address.Should().Be(RecoveryTestFixtures.CanonicalAddress);
        journal.Scope.Should().Be(RecoveryTestFixtures.LocalNotesScope);
        journal.OldArtifact.Payload.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.OldArtifact));
        journal.OldArtifact.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.OldArtifact));
        journal.NewArtifact.Payload.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewArtifact));
        journal.NewArtifact.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewArtifact));
        journal.OldSidecar.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.OldSidecar));
        journal.NewSidecar.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewSidecar));
    }

    [Fact]
    public void LegacyRestoreFixtures_AreAcceptedByTheProductionReaderWithExactEvidence()
    {
        foreach (string state in new[] { "prepared", "completed" })
        {
            byte[] bytes = RecoveryTestFixtures.BuildLegacyRestoreJournal(state: state);
            RestoreJournal journal = RestoreJournalCodec.ParseAndValidate(
                bytes, RecoveryTestFixtures.LocalNotesScope,
                RecoveryTestFixtures.RestoreJournalPath("scope"));

            journal.TransactionId.Should().Be(RecoveryTestFixtures.RestoreTransactionId);
            journal.Scope.Should().Be(RecoveryTestFixtures.LocalNotesScope);
            journal.Entries.Should().ContainSingle();
            RestoreJournalEntry entry = journal.Entries[0];
            entry.BodyBase64.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewArtifact));
            entry.BodySha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewArtifact));
            entry.SidecarBase64.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewSidecar));
            entry.SidecarSha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewSidecar));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFileEntityFixtures_AreReadByTheProductionCoordinator(bool committed)
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-fixture-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "entity");
        string bodyPath = Path.Combine(entityDirectory, "entity.md");
        string metaPath = Path.Combine(entityDirectory, "entity.meta.json");
        string versionsDirectory = Path.Combine(entityDirectory, "versions");
        string evidencePath = RecoveryTestFixtures.FileEntityRecoveryPath(entityDirectory);
        Directory.CreateDirectory(entityDirectory);
        File.WriteAllBytes(bodyPath, RecoveryTestFixtures.FileEntityBody);
        File.WriteAllBytes(metaPath, RecoveryTestFixtures.FileEntityMetadata);
        File.WriteAllBytes(evidencePath, RecoveryTestFixtures.BuildLegacyFileEntityRecovery(
            bodyPath, metaPath, committed));

        try
        {
            FileEntityMutationResult result = await new FileEntityTransactionCoordinator(root).ExecuteAsync(
                new FileEntityMutation("skill", "entity", bodyPath, metaPath, versionsDirectory,
                    RecoveryTestFixtures.NewArtifact, RecoveryTestFixtures.NewSidecar,
                    deleteBody: false, deleteMeta: false));

            result.Recovery.Should().BeNull();
            File.Exists(evidencePath).Should().BeFalse();
            File.ReadAllBytes(bodyPath).Should().Equal(RecoveryTestFixtures.NewArtifact);
            File.ReadAllBytes(metaPath).Should().Equal(RecoveryTestFixtures.NewSidecar);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
