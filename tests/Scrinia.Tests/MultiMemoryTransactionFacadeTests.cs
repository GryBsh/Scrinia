using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionFacadeTests
{
    [Theory]
    [InlineData("Committed", "success")]
    [InlineData("CommittedCleanupPending", "success_with_warning")]
    [InlineData("RolledBack", "warning")]
    public void MapsCompletedAggregateThroughExistingResponseEnvelope(
        string outcomeName, string expectedStatus)
    {
        MultiMemoryOutcome outcome = Enum.Parse<MultiMemoryOutcome>(outcomeName);
        MultiMemoryAggregateResult result = Aggregate(outcome, MultiMemoryFailure.Cleanup,
            warning: "typed cleanup warning");

        McpResponse response = ScriniaMcpTools.MapMultiMemoryResult(result, "multi-memory");

        response.Status.Should().Be(expectedStatus);
        response.Action.Should().Be("multi-memory");
        response.OperationId.Should().Be("tx-1");
        response.Info.Should().Contain(item => item.Contains("note-a", StringComparison.Ordinal));
        response.Info.Should().Contain(item => item.Contains("op-a", StringComparison.Ordinal));
        response.ToYaml().Should().NotContain("typed cleanup warning");
    }

    [Theory]
    [InlineData("ParticipantCommit", "participantCommit")]
    [InlineData("Verification", "verification")]
    [InlineData("Ambiguous", "ambiguous")]
    [InlineData("Malformed", "malformed")]
    [InlineData("UnsupportedVersion", "unsupportedVersion")]
    [InlineData("LockTimeout", "lockTimeout")]
    public void MapsTypedFailureWithoutLeakingRawDetails(
        string failureName, string expectedReason)
    {
        MultiMemoryFailure failure = Enum.Parse<MultiMemoryFailure>(failureName);
        const string sensitiveDetail = "raw payload and absolute path must not escape";
        MultiMemoryAggregateResult result = Aggregate(MultiMemoryOutcome.RecoveryRequired, failure,
            warning: sensitiveDetail, evidencePath: "nested/.multi-memory-transaction.json");

        McpResponse response = ScriniaMcpTools.MapMultiMemoryResult(result, "multi-memory");

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(failure is MultiMemoryFailure.Malformed or
            MultiMemoryFailure.UnsupportedVersion or MultiMemoryFailure.Unsafe or
            MultiMemoryFailure.Inconsistent or MultiMemoryFailure.Ambiguous or
            MultiMemoryFailure.ReplayIoFailed ? ErrorCodes.RecoveryBlocked : ErrorCodes.RecoveryRequired);
        response.OperationId.Should().Be("tx-1");
        response.Scope.Should().Be("local:notes");
        response.JournalPath.Should().Be(".multi-memory-transaction.json");
        response.Reason.Should().Be(expectedReason);
        response.Error.Should().NotContain(sensitiveDetail);
        response.ToYaml().Should().NotContain("C:\\secret");
        response.ToYaml().Should().NotContain("payload");
        response.ToYaml().Should().Contain("note-a");
        response.ToYaml().Should().Contain("op-a");
        response.ToYaml().Should().Contain("evidence.json");
    }

    [Fact]
    public void MapsAdmissionFailureToExistingInvalidParameterContract()
    {
        McpResponse response = ScriniaMcpTools.MapMultiMemoryResult(
            Aggregate(MultiMemoryOutcome.Rejected, MultiMemoryFailure.Admission), "multi-memory");

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        response.OperationId.Should().Be("tx-1");
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    private static MultiMemoryAggregateResult Aggregate(
        MultiMemoryOutcome outcome, MultiMemoryFailure? failure = null,
        string? warning = null, string? evidencePath = "evidence.json") =>
        new(
            "tx-1", "local:notes", outcome,
            [new MultiMemoryParticipantOutcome(
                "note-a", "op-a", "memory", "evidence.json", outcome == MultiMemoryOutcome.Committed, false,
                failure?.ToString())],
            failure, warning, outcome == MultiMemoryOutcome.RecoveryRequired ? evidencePath : null);
}
