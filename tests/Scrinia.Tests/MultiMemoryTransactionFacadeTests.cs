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
        response.ToYaml().Should().Contain("typed cleanup warning");
        response.ToYaml().Should().Contain("participant-specific");
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
            warning: "typed aggregate detail", evidencePath: "nested/.multi-memory-transaction.json");

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
        response.ToYaml().Should().Contain("detail: typed aggregate detail");
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

    [Fact]
    public void MCPEntrypointEmitsTheMappedAggregateEnvelope()
    {
        string yaml = ScriniaMcpTools.EmitMultiMemoryResult(
            Aggregate(MultiMemoryOutcome.CommittedCleanupPending, MultiMemoryFailure.Cleanup,
                warning: "typed cleanup warning"), "multi-memory");

        yaml.Should().Contain("status: success_with_warning");
        yaml.Should().Contain("operationId: tx-1");
        yaml.Should().Contain("typed cleanup warning");
        yaml.Should().Contain("participant-specific");
    }

    [Theory]
    [InlineData("/var/lib/scrinia/secret.json")]
    [InlineData("C:/foreign/root/recovery.json")]
    [InlineData("C:\\foreign\\root\\recovery.json")]
    [InlineData("../outside/evidence.json")]
    [InlineData("file:" + "//foreign/root")]
    public void PathLikeAggregateDetailsAreRedacted(string pathLikeDetail)
    {
        MultiMemoryAggregateResult result = Aggregate(
            MultiMemoryOutcome.RecoveryRequired, MultiMemoryFailure.Ambiguous,
            warning: pathLikeDetail);

        McpResponse response = ScriniaMcpTools.MapMultiMemoryResult(result, "multi-memory");

        response.ToYaml().Should().NotContain(pathLikeDetail);
        response.ToYaml().Should().NotContain("foreign");
        response.ToYaml().Should().NotContain("outside");
        response.ToYaml().Should().Contain("reason: ambiguous");
    }

    [Theory]
    [InlineData("warning\rtext")]
    [InlineData("warning\ntext")]
    [InlineData("warning\ttext")]
    [InlineData("warning\u000Btext")]
    [InlineData("warning\u0085text")]
    [InlineData("warning\u00A0text")]
    [InlineData("warning\u2007text")]
    public void MCPControlWhitespaceIsRedactedFromWarningsAndParticipantReasons(string unsafeDetail)
    {
        MultiMemoryAggregateResult result = Aggregate(
            MultiMemoryOutcome.CommittedCleanupPending, MultiMemoryFailure.Cleanup,
            warning: unsafeDetail, participantReason: unsafeDetail);

        string yaml = ScriniaMcpTools.EmitMultiMemoryResult(result, "multi-memory");

        yaml.Should().NotContain(unsafeDetail);
        yaml.Should().Contain("reason: cleanup");
        yaml.Should().Contain("Multi-memory transaction committed; cleanup remains pending.");
    }

    private static MultiMemoryAggregateResult Aggregate(
        MultiMemoryOutcome outcome, MultiMemoryFailure? failure = null,
        string? warning = null, string? evidencePath = "evidence.json", string? participantReason = null) =>
        new(
            "tx-1", "local:notes", outcome,
            [new MultiMemoryParticipantOutcome(
                "note-a", "op-a", "memory", "evidence.json", outcome == MultiMemoryOutcome.Committed, false,
                participantReason ?? "participant-specific")],
            failure, warning, outcome == MultiMemoryOutcome.RecoveryRequired ? evidencePath : null);
}
