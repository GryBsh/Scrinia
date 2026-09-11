using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class CliWholeMemoryTransactionTests
{
    [Fact]
    public async Task CompactRecoveryJsonUsesTheSharedHandlerEnvelopeAndWarningExitPolicy()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/cli/transaction", ["old one", "old two"]);
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Compact(
                "/cli/transaction", workspaceRoot: scope.WorkspaceDir, json: true);

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            mapped.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public void TransactionErrorJsonRetainsOperationAndRecoveryEnvelope()
    {
        McpResponse response = ResponseBuilder.Error(
                "write requires recovery", ErrorCodes.RecoveryRequired, "resolve evidence")
            .WithOperationId("op-7")
            .WithRecovery("local:transaction", "versions/.whole-memory.journal");

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = ScriniaCommands.EmitMcpResult(response, json: true, action: "store");

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            envelope.Status.Should().Be("error");
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
            mapped.OperationId.Should().Be("op-7");
            mapped.Scope.Should().Be("local:transaction");
            mapped.JournalPath.Should().Be("versions/.whole-memory.journal");
        }
        finally
        {
            Console.SetOut(prior);
        }
    }
}
