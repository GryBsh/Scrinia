using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Formats;
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
        RecoveryTestFixtures.FileEntityRecoveryPath("entity")
            .Should().EndWith($".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
    }
}
