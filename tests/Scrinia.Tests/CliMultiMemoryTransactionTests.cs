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
    public void ExistingCliWrapperOmitsB4ProjectionDetailsFromSourceGeneratedJson()
    {
        McpResponse response = ScriniaCommands.MapMultiMemoryResult(
            AggregateWithB4Projection(), "multi-memory");
        string json = JsonSerializer.Serialize(
            new CliMcpOutput("multi-memory", response.Status, response.ToYaml()),
            CliJsonContext.Default.CliMcpOutput);

        json.Should().Contain("operationId: tx-1");
        json.Should().Contain("participant: note-a");
        json.Should().NotContain("transactionalParticipants");
        json.Should().NotContain("nonAdmittedParticipants");
        json.Should().NotContain("plugin-a");
        json.Should().NotContain("plugin-rejected");
    }

    [Fact]
    public void CliEntrypointOmitsB4ProjectionDetailsFromEmbeddedYaml()
    {
        (int exitCode, string output) = CaptureCliOutput(
            AggregateWithB4Projection(), json: true);

        exitCode.Should().Be(0);
        output.Should().NotContain("transactionalParticipants");
        output.Should().NotContain("nonAdmittedParticipants");
        output.Should().Contain("operationId: tx-1");
        output.Should().Contain("participant: note-a");
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

    [Fact]
    public void NoPublicAggregateEntrypointExistsUntilSeparatelyApproved()
    {
        var publicMcpNames = typeof(ScriniaMcpTools).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name.Contains("Multi", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Transaction", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var publicCliNames = typeof(ScriniaCommands).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name.Contains("Multi", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Transaction", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var coordinatorSeam = typeof(FileMemoryStore).GetMethod(
            "ExecuteMultiMemoryAsync", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        publicMcpNames.Should().BeEmpty();
        publicCliNames.Should().BeEmpty();
        coordinatorSeam.Should().NotBeNull();
        coordinatorSeam!.IsPublic.Should().BeFalse();
        ScriniaMcpTools.EmitMultiMemoryResult(
            Aggregate(MultiMemoryOutcome.Committed), "multi-memory")
            .Should().Contain("operationId: tx-1");
    }

    [Theory]
    [InlineData("warning\rtext")]
    [InlineData("warning\ntext")]
    [InlineData("warning\ttext")]
    [InlineData("warning\u000Btext")]
    [InlineData("warning\u0085text")]
    [InlineData("warning\u00A0text")]
    [InlineData("warning\u2007text")]
    public void CLIControlWhitespaceIsRedactedFromPlainAndJsonEnvelopes(string unsafeDetail)
    {
        MultiMemoryAggregateResult result = Aggregate(
            MultiMemoryOutcome.CommittedCleanupPending, MultiMemoryFailure.Cleanup, unsafeDetail,
            participantReason: unsafeDetail);

        (int plainExitCode, string plainOutput) = CaptureCliOutput(result, json: false);
        (int jsonExitCode, string jsonOutput) = CaptureCliOutput(result, json: true);

        plainExitCode.Should().Be(1);
        jsonExitCode.Should().Be(1);
        plainOutput.Should().NotContain(unsafeDetail);
        jsonOutput.Should().NotContain(unsafeDetail);
        plainOutput.Should().Contain("operationId: tx-1");
        jsonOutput.Should().Contain("operationId: tx-1");
        plainOutput.Should().Contain("reason: cleanup");
        jsonOutput.Should().Contain("reason: cleanup");
        plainOutput.Should().Contain("participant: note-a");
        jsonOutput.Should().Contain("participant: note-a");
        plainOutput.Should().Contain("Multi-memory transaction committed; cleanup remains pending.");
        jsonOutput.Should().Contain("Multi-memory transaction committed; cleanup remains pending.");
    }

    private static (int ExitCode, string Output) CaptureCliOutput(
        MultiMemoryAggregateResult result, bool json)
    {
        TextWriter original = Console.Out;
        using var output = new StringWriter(new StringBuilder());
        Console.SetOut(output);
        try
        {
            int exitCode = ScriniaCommands.EmitMultiMemoryResult(result, json, "multi-memory");
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static MultiMemoryAggregateResult Aggregate(
        MultiMemoryOutcome outcome, MultiMemoryFailure? failure = null, string? warning = null,
        string? participantReason = null) =>
        new(
            "tx-1", "local:notes", outcome,
            [new MultiMemoryParticipantOutcome(
                "note-a", "op-a", "memory", "evidence.json", outcome == MultiMemoryOutcome.Committed, false,
                participantReason ?? "participant-specific")],
                failure, warning ?? "typed aggregate detail", outcome == MultiMemoryOutcome.RecoveryRequired
                ? "nested/.multi-memory-transaction.json" : null);

    private static MultiMemoryAggregateResult AggregateWithB4Projection()
    {
        MultiMemoryTransactionRequest request = new("local:notes",
            [new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/note-a"), "op-a", "memory")],
            [
                new MultiMemoryTransactionalParticipantRequest(
                    MemoryAddress.Parse("/notes/plugin"), "plugin-a", 1, "plugin-key",
                    "op-plugin", "plugin", new Dictionary<string, string>(), new Dictionary<string, string>()),
                new MultiMemoryTransactionalParticipantRequest(
                    MemoryAddress.Parse("/notes/rejected"), "plugin-rejected", 1, "plugin-key-rejected",
                    "op-rejected", "plugin", new Dictionary<string, string>(), new Dictionary<string, string>())
            ]);

        return MultiMemoryAggregateResult.Create(
            "tx-1", "local:notes", MultiMemoryOutcome.Committed, request.RequestedParticipants(),
            new MultiMemoryAggregateProjectionInput(
                [new MultiMemoryParticipantOutcome(
                    "note-a", "op-a", "memory", "evidence.json", true, false, "participant-specific", 0)],
                [new MultiMemoryTransactionalParticipantOutcome(
                    "plugin-a", "plugin-key", "op-plugin", "plugin", true,
                    TransactionalParticipantLifecycleState.Committed, true, false, "plugin/evidence", null, 1)],
                [new MultiMemoryTransactionalParticipantOutcome(
                    "plugin-rejected", "plugin-key-rejected", "op-rejected", "plugin", false,
                    TransactionalParticipantLifecycleState.Ambiguous, false, false,
                    "plugin/rejected", "admission rejected", 2)]),
            null, "typed aggregate detail", null);
    }
}
