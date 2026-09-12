using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class RecoveryFacadeMappingTests
{
    [Fact]
    public async Task McpRecoveryBlockedResponse_UsesTypedReasonAndDoesNotLeakRawDetail()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        string evidencePath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllText(evidencePath, "{]");

        const string sensitiveDetail = "raw payload must not escape facade";
        FileMemoryStore.RestoreJournalFailureInjector.Value = (_, _) =>
            new IOException(sensitiveDetail);

        try
        {
            McpResponseAssert response = ResponseParser.Parse(
                await new ScriniaMcpTools().MemoryRecall("/notes/note"));

            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            response.Scope.Should().Be("local:notes");
            response.JournalPath.Should().Be(evidencePath);
            response.Error.Should().NotContain(sensitiveDetail);
            response.Reason.Should().Be("malformed");
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
        }
    }

    [Fact]
    public async Task McpAppendRecoveryBlockedResponse_PreservesTypedReason()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string versions = Path.Combine(store.GetStoreDirForScope("local:notes"), "versions");
        Directory.CreateDirectory(versions);
        File.WriteAllText(Path.Combine(versions, ".append-journal.json"), "{]");

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryAppend("/notes/note", "content"));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
        response.Reason.Should().Be("malformed");
        response.JournalPath.Should().Be(Path.Combine(versions, ".append-journal.json"));
    }
}
