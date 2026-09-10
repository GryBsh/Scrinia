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
    }

    [Fact]
    public void FileEntityMutation_CarriesBothMembersAndExplicitDeleteIntent()
    {
        var mutation = new FileEntityMutation(
            "skill", "qa", "qa.md", "qa.meta.json", "versions", "new"u8.ToArray(), null, true, true);

        mutation.DeleteBody.Should().BeTrue();
        mutation.DeleteMeta.Should().BeTrue();
    }
}
