using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class RecoveryFacadeMappingTests
{
    [Theory]
    [InlineData("malformed", "malformed")]
    [InlineData("unsupportedVersion", "unsupportedVersion")]
    [InlineData("replayIoFailed", "replayIoFailed")]
    public async Task McpFallbackRestoreExceptions_MapTypedReasonWithoutDiscoveryBlock(
        string failureName, string expectedReason)
    {
        using var scope = new TestHelpers.StoreScope();
        const string sensitiveDetail = "raw fallback exception detail must not escape";
        string journalPath = Path.Combine(scope.WorkspaceDir, ".restore-journal.json");
        RestoreRecoveryFailure failure = failureName switch
        {
            "malformed" => RestoreRecoveryFailure.MalformedOrIntegrityFailed,
            "unsupportedVersion" => RestoreRecoveryFailure.UnsupportedVersion,
            "replayIoFailed" => RestoreRecoveryFailure.ReplayIoFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(failureName), failureName)
        };
        var exception = new RestoreRecoveryException(
            "local:notes", journalPath, failure, sensitiveDetail, unsupportedVersion: 7);
        MemoryStoreContext.Current = new FallbackRestoreStore(exception);

        McpResponseAssert response = ResponseParser.Parse(
            await new ScriniaMcpTools().MemoryRecall("/notes/note"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
        response.Reason.Should().Be(expectedReason);
        response.Scope.Should().Be("local:notes");
        response.JournalPath.Should().Be(journalPath);
        response.Error.Should().NotContain(sensitiveDetail);
    }

    [Fact]
    public async Task McpRecoveryBlockedResponse_UsesTypedReasonAndDoesNotLeakRawDetail()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        const string sensitiveDetail = "raw payload must not escape facade";
        FileMemoryStore.RestoreJournalFailureInjector.Value = (_, _) =>
            new IOException(sensitiveDetail);

        try
        {
            string notesDirectory = store.GetStoreDirForScope("local:notes");
            Directory.CreateDirectory(notesDirectory);
            File.WriteAllBytes(Path.Combine(notesDirectory, "note.nmp2"), [1, 2, 3]);
            File.WriteAllText(Path.Combine(notesDirectory, "note.meta.json"), "{\"name\":\"note\"}");
            File.WriteAllText(Path.Combine(notesDirectory, "index.json"), "{\"entries\":[]}");
            File.WriteAllText(Path.Combine(notesDirectory, "metadata.json"), "{\"scope\":\"local:notes\"}");
            string notesJournalPath = Path.Combine(notesDirectory, ".restore-journal.json");
            File.WriteAllText(notesJournalPath, "{]");

            McpResponseAssert response = ResponseParser.Parse(
                await new ScriniaMcpTools().MemoryRecall("/notes/note"));

            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            response.Scope.Should().Be("local:notes");
            response.JournalPath.Should().Be(notesJournalPath);
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

    [Fact]
    public async Task McpGuideAndRestore_ReturnTypedRecoveryBlockedResponse()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string directory = store.GetStoreDirForScope("local:sessions");
        Directory.CreateDirectory(directory);
        string journalPath = Path.Combine(directory, ".restore-journal.json");
        File.WriteAllText(journalPath, "{]");
        var tools = new ScriniaMcpTools();

        McpResponseAssert guide = ResponseParser.Parse(await tools.Guide());
        McpResponseAssert restore = ResponseParser.Parse(await tools.MemoryRestore());

        foreach (McpResponseAssert response in new[] { guide, restore })
        {
            response.Status.Should().Be("error");
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            response.Scope.Should().Be("local:sessions");
            response.JournalPath.Should().Be(journalPath);
            response.Reason.Should().Be("malformed");
            response.ActionNeeded.Should().ContainSingle()
                .Which.Should().Contain("Resolve the retained restore journal");
        }
    }

    private sealed class FallbackRestoreStore(Exception failure) : FakeMemoryStore
    {
        public override ValueTask<ArtifactEntry?> ReadAsync(
            MemoryAddress address, CancellationToken ct = default) =>
            ValueTask.FromException<ArtifactEntry?>(failure);

        public override Task<string> ReadArtifactAsync(
            MemoryAddress address, CancellationToken ct = default) =>
            Task.FromException<string>(failure);
    }
}
