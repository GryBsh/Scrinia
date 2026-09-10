using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class WholeMemoryTransactionTests
{
    [Fact]
    public void TransactionSnapshot_DefensivelyCopiesBytesAndRejectsHashMismatch()
    {
        byte[] bytes = [1, 2, 3];
        var snapshot = RawMemberSnapshot.Capture("body", true, bytes);

        bytes[0] = 9;

        snapshot.Bytes.Should().Equal([1, 2, 3]);
        FluentActions.Invoking(() => new RawMemberSnapshot("body", true, [1], snapshot.Sha256))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WholeMemoryResult_RequiresOperationIdAndRecoveryEvidenceForRecovery()
    {
        var result = new WholeMemoryResult(
            WholeMemoryOutcome.RecoveryRequired,
            null,
            "op-1",
            null,
            new RecoveryInfo("scope/.journal", "recoveryRequired", "upgrade or repair", "safe detail"));

        result.OperationId.Should().Be("op-1");
        result.Recovery!.EvidencePath.Should().Be("scope/.journal");

        var (outcome, entry, operationId, warning, recovery) = result;
        outcome.Should().Be(WholeMemoryOutcome.RecoveryRequired);
        entry.Should().BeNull();
        operationId.Should().Be("op-1");
        warning.Should().BeNull();
        recovery.Should().Be(result.Recovery);
    }

    [Fact]
    public void FileEntityMutation_CarriesBothMembersAndExplicitDeleteIntent()
    {
        byte[] body = "new body"u8.ToArray();
        byte[] meta = "new meta"u8.ToArray();
        var mutation = new FileEntityMutation(
            "skill", "qa", "qa.md", "qa.meta.json", "versions", body, meta, true, true);

        mutation.DeleteBody.Should().BeTrue();
        mutation.DeleteMeta.Should().BeTrue();

        body[0] = (byte)'X';
        meta[0] = (byte)'X';
        mutation.NewBody.Should().Equal("new body"u8.ToArray());
        mutation.NewMeta.Should().Equal("new meta"u8.ToArray());

        byte[] returnedBody = mutation.NewBody;
        byte[] returnedMeta = mutation.NewMeta!;
        returnedBody[0] = (byte)'Y';
        returnedMeta[0] = (byte)'Y';
        mutation.NewBody.Should().Equal("new body"u8.ToArray());
        mutation.NewMeta.Should().Equal("new meta"u8.ToArray());

        var (_, _, _, _, _, deconstructedBody, deconstructedMeta, _, _) = mutation;
        deconstructedBody.Should().Equal("new body"u8.ToArray());
        deconstructedMeta.Should().Equal("new meta"u8.ToArray());
    }

    [Fact]
    public void ArchivePairPlan_PreservesPositionalDeconstructionContract()
    {
        var plan = new ArchivePairPlan(true, "archive.nmp2", "archive.meta.json", ["old"]);

        var (required, artifactPath, sidecarPath, pruneCandidates) = plan;

        required.Should().BeTrue();
        artifactPath.Should().Be("archive.nmp2");
        sidecarPath.Should().Be("archive.meta.json");
        pruneCandidates.Should().Equal("old");
    }
}
