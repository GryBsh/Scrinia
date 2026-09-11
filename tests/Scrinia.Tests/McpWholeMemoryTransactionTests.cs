using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class McpWholeMemoryTransactionTests
{
    [Fact]
    public async Task Remember_RollbackSafeFailureReturnsDurableWriteError()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/target", ["old"]);
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/transaction/target", ["new"]));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Remember_RetainedEvidenceReturnsRecoveryDetailsWithoutContent()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/recovery", ["old secret"]);
        FileMemoryStore.WholeMemoryFaultInjector = (point, _) => point switch
        {
            WholeMemoryFaultPoint.LiveArtifactWrite or WholeMemoryFaultPoint.RollbackWrite =>
                new IOException("injected transaction failure"),
            _ => null
        };

        try
        {
            McpResponseAssert response = ResponseParser.Parse(
                await new ScriniaMcpTools().MemoryRemember("/transaction/recovery", ["new secret"]));

            response.Status.Should().Be("error");
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
            response.OperationId.Should().NotBeNullOrWhiteSpace();
            response.Scope.Should().Be("local:transaction");
            response.JournalPath.Should().NotContain(scope.WorkspaceDir);
            response.Content.Should().BeNull();
            response.Error.Should().NotContain("old secret");
        }
        finally
        {
            FileMemoryStore.WholeMemoryFaultInjector = null;
        }
    }

    [Fact]
    public async Task Remember_PostCommitCleanupWarningPreservesCommittedOutcome()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/transaction/warning", ["old"]);
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRemember("/transaction/warning", ["new"]));

        response.Status.Should().Be("success_with_warning");
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Path.Should().BeNull();
    }
}
