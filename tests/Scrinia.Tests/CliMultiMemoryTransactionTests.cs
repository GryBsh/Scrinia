using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class CliMultiMemoryTransactionTests
{
    [Fact]
    public void MapsCommittedAggregateToPlainMcpEnvelopeWithExitZero()
    {
        McpResponse response = ScriniaCommands.MapMultiMemoryResult(
            Aggregate(MultiMemoryOutcome.Committed), "multi-memory");

        int exitCode = ScriniaCommands.EmitMcpResult(response, json: false, "multi-memory");

        exitCode.Should().Be(0);
        response.ToYaml().Should().Contain("status: success");
        response.ToYaml().Should().Contain("operationId: tx-1");
    }

    [Theory]
    [InlineData("CommittedCleanupPending", 1)]
    [InlineData("RolledBack", 1)]
    [InlineData("RecoveryRequired", 1)]
    [InlineData("Rejected", 1)]
    public void MapsNonSuccessAggregateToNonZeroExit(string outcomeName, int expectedExitCode)
    {
        MultiMemoryOutcome outcome = Enum.Parse<MultiMemoryOutcome>(outcomeName);
        McpResponse response = ScriniaCommands.MapMultiMemoryResult(
            Aggregate(outcome, outcome == MultiMemoryOutcome.RecoveryRequired
                ? MultiMemoryFailure.Ambiguous : MultiMemoryFailure.Admission), "multi-memory");

        int exitCode = ScriniaCommands.EmitMcpResult(response, json: false, "multi-memory");

        exitCode.Should().Be(expectedExitCode);
    }

    [Fact]
    public void ExistingCliWrapperRoundTripsMappedAggregateThroughSourceGeneratedJson()
    {
        McpResponse response = ScriniaCommands.MapMultiMemoryResult(
            Aggregate(MultiMemoryOutcome.RecoveryRequired, MultiMemoryFailure.Verification), "multi-memory");
        string yaml = response.ToYaml();
        var output = new CliMcpOutput("multi-memory", response.Status, yaml);

        string json = JsonSerializer.Serialize(output, CliJsonContext.Default.CliMcpOutput);
        CliMcpOutput? roundTrip = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliMcpOutput);

        roundTrip.Should().NotBeNull();
        roundTrip!.Action.Should().Be("multi-memory");
        roundTrip.Status.Should().Be("error");
        roundTrip.Yaml.Should().Contain("reason: verification");
        roundTrip.Yaml.Should().Contain("journalPath: .multi-memory-transaction.json");
    }

    [Fact]
    public void CliAndMcpMappingsHaveIdenticalTypedSemantics()
    {
        MultiMemoryAggregateResult result = Aggregate(
            MultiMemoryOutcome.RecoveryRequired, MultiMemoryFailure.Inconsistent);

        McpResponse mcp = Scrinia.Mcp.ScriniaMcpTools.MapMultiMemoryResult(result, "multi-memory");
        McpResponse cli = ScriniaCommands.MapMultiMemoryResult(result, "multi-memory");

        cli.Should().BeEquivalentTo(mcp);
    }

    private static MultiMemoryAggregateResult Aggregate(
        MultiMemoryOutcome outcome, MultiMemoryFailure? failure = null) =>
        new(
            "tx-1", "local:notes", outcome,
            [new MultiMemoryParticipantOutcome(
                "note-a", "op-a", "memory", "evidence.json", outcome == MultiMemoryOutcome.Committed, false,
                failure?.ToString())],
            failure, "internal detail must not be rendered", outcome == MultiMemoryOutcome.RecoveryRequired
                ? "nested/.multi-memory-transaction.json" : null);
}
