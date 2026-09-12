using System.Text.Json;
using System.Text;
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
        TextWriter original = Console.Out;
        using var output = new StringWriter(new StringBuilder());
        Console.SetOut(output);
        try
        {
            int exitCode = ScriniaCommands.EmitMultiMemoryResult(
                Aggregate(MultiMemoryOutcome.Committed), json: false, "multi-memory");

            exitCode.Should().Be(0);
            output.ToString().Should().Contain("status: success");
            output.ToString().Should().Contain("operationId: tx-1");
            output.ToString().Should().Contain("participant: note-a");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Theory]
    [InlineData("CommittedCleanupPending", 1)]
    [InlineData("RolledBack", 1)]
    [InlineData("RecoveryRequired", 1)]
    [InlineData("Rejected", 1)]
    public void MapsNonSuccessAggregateToNonZeroExit(string outcomeName, int expectedExitCode)
    {
        MultiMemoryOutcome outcome = Enum.Parse<MultiMemoryOutcome>(outcomeName);
        int exitCode = ScriniaCommands.EmitMultiMemoryResult(
            Aggregate(outcome, outcome == MultiMemoryOutcome.RecoveryRequired
                ? MultiMemoryFailure.Ambiguous : MultiMemoryFailure.Admission), false, "multi-memory");

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
    public void RejectedAggregateJsonRetainsCorrelationAndUsesExistingWrapper()
    {
        TextWriter original = Console.Out;
        using var output = new StringWriter(new StringBuilder());
        Console.SetOut(output);
        try
        {
            int exitCode = ScriniaCommands.EmitMultiMemoryResult(
                Aggregate(MultiMemoryOutcome.Rejected, MultiMemoryFailure.Inconsistent), true, "multi-memory");

            exitCode.Should().Be(1);
            CliMcpOutput? envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput);
            envelope.Should().NotBeNull();
            envelope!.Yaml.Should().Contain("operationId: tx-1");
            envelope.Yaml.Should().Contain("participant: note-a");
            envelope.Yaml.Should().Contain("evidence: evidence.json");
            envelope.Yaml.Should().NotContain("C:\\secret");
        }
        finally
        {
            Console.SetOut(original);
        }
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
                "participant-specific")],
            failure, "typed aggregate detail", outcome == MultiMemoryOutcome.RecoveryRequired
                ? "nested/.multi-memory-transaction.json" : null);
}
