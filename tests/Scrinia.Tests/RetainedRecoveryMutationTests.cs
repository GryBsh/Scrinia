using System.Text.Json;
using FluentAssertions;
using Scrinia.Mcp;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class RetainedRecoveryMutationTests
{
    [Theory]
    [InlineData("store", false)]
    [InlineData("append", false)]
    [InlineData("append-create", false)]
    [InlineData("delete", false)]
    [InlineData("upsert", false)]
    [InlineData("update", false)]
    [InlineData("save-index", false)]
    [InlineData("import", false)]
    [InlineData("artifact-write", false)]
    [InlineData("archive", false)]
    [InlineData("store", true)]
    public async Task MutationsRefuseRetainedTerminalProofBeforeChangingAnyOwnedBytes(string operation, bool coordinator)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var address = MemoryAddress.Parse("/notes/note");
        await store.StoreAsync(MemoryWrite.Create(address, "local:notes", ["before"]));
        string directory = store.GetStoreDirForScope("local:notes");
        string body = Path.Combine(directory, "note.nmp2");
        string meta = Path.Combine(directory, "note.meta.json");
        ArtifactEntry entry = (await store.ReadAsync(address))!;
        string evidence;
        try
        {
            if (coordinator)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
                    "retained-proof", "local:notes", MultiMemoryTransactionState.Committed,
                    [new MultiMemoryParticipantManifest("note", "op-note", "memory")], now, now);
                record = MultiMemoryTransactionRecordCodec.WithCompletionProof(record,
                    [new MultiMemoryParticipantCompletion("note", "op-note", "memory", ".restore-journal.json")], now);
                MultiMemoryTransactionRecordCodec.Write(directory, record);
                evidence = MultiMemoryTransactionRecordCodec.GetPath(directory);
                MultiMemoryTransactionCoordinator.FailureInjector.Value = (at, _) =>
                    at == MultiMemoryTransactionTransition.RecordDeleted ? new IOException("cleanup blocked") : null;
            }
            else
            {
                FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
                    at == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup blocked") : null;
                store.ImportTopicEntries("local:notes", [entry],
                    new Dictionary<string, byte[]> { ["note"] = File.ReadAllBytes(body) }, true);
                evidence = Path.Combine(directory, ".restore-journal.json");
            }
            byte[] oldBody = File.ReadAllBytes(body), oldMeta = File.ReadAllBytes(meta), oldEvidence = File.ReadAllBytes(evidence);
            string[] oldFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            Func<Task> mutate = async () =>
            {
                switch (operation)
                {
                    case "store": await store.StoreAsync(MemoryWrite.Create(address, "local:notes", ["after"])); break;
                    case "append": await store.AppendAsync(MemoryWrite.Create(address, "local:notes", ["after"])); break;
                    case "append-create": await store.AppendAsync(MemoryWrite.Create(MemoryAddress.Parse("/notes/new"), "local:notes", ["after"])); break;
                    case "delete": await store.DeleteAsync(address); break;
                    case "upsert": store.Upsert(entry with { ContentPreview = "after" }, "local:notes"); break;
                    case "update": store.TryUpdateEntry(address, e => e with { ContentPreview = "after" }); break;
                    case "save-index": store.SaveIndex([], "local:notes"); break;
                    case "import": store.ImportTopicEntries("local:notes", [entry],
                        new Dictionary<string, byte[]> { ["note"] = "after"u8.ToArray() }, true); break;
                    case "artifact-write": await store.WriteArtifactAsync("note", "local:notes", "after"); break;
                    case "archive": store.ArchiveVersion("note", "local:notes"); break;
                    default: throw new InvalidOperationException(operation);
                }
            };
            var blocked = (await mutate.Should().ThrowAsync<RestoreRecoveryException>()).Which;
            blocked.DiscoveryBlock.Should().NotBeNull();
            blocked.DiscoveryBlock!.EvidencePath.Should().Be(evidence);
            using (JsonDocument recorded = JsonDocument.Parse(oldEvidence))
                blocked.DiscoveryBlock.OperationId.Should().Be(recorded.RootElement
                    .GetProperty(coordinator ? "transactionId" : "operationId").GetString());
            File.ReadAllBytes(body).Should().Equal(oldBody);
            File.ReadAllBytes(meta).Should().Equal(oldMeta);
            File.ReadAllBytes(evidence).Should().Equal(oldEvidence);
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(oldFiles);

            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
            await store.StoreAsync(MemoryWrite.Create(address, "local:notes", ["after cleanup"]));
            File.Exists(evidence).Should().BeFalse();
            (await store.ReadAsync(address))!.ContentPreview.Should().Contain("after cleanup");
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewMultiMemoryExecutionDoesNotAdmitParticipantsOverRetainedProof(bool coordinator)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var address = MemoryAddress.Parse("/notes/note");
        await store.StoreAsync(MemoryWrite.Create(address, "local:notes", ["before"]));
        string directory = store.GetStoreDirForScope("local:notes");
        string evidence;
        try
        {
            if (coordinator)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
                    "retained-execution-proof", "local:notes", MultiMemoryTransactionState.Committed,
                    [new MultiMemoryParticipantManifest("note", "op-note", "memory")], now, now);
                record = MultiMemoryTransactionRecordCodec.WithCompletionProof(record,
                    [new MultiMemoryParticipantCompletion("note", "op-note", "memory", ".restore-journal.json")], now);
                MultiMemoryTransactionRecordCodec.Write(directory, record);
                evidence = MultiMemoryTransactionRecordCodec.GetPath(directory);
                MultiMemoryTransactionCoordinator.FailureInjector.Value = (at, _) =>
                    at == MultiMemoryTransactionTransition.RecordDeleted ? new IOException("cleanup blocked") : null;
            }
            else
            {
                FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
                    at == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup blocked") : null;
                store.ImportTopicEntries("local:notes", [(await store.ReadAsync(address))!],
                    new Dictionary<string, byte[]> { ["note"] = File.ReadAllBytes(Path.Combine(directory, "note.nmp2")) }, true);
                evidence = Path.Combine(directory, ".restore-journal.json");
            }
            byte[] original = File.ReadAllBytes(evidence);
            bool admitted = false;
            var request = new MultiMemoryTransactionRequest("local:notes",
                [new MultiMemoryParticipantRequest(address, "new-operation", "memory")]);
            MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(request,
                (_, _) => { admitted = true; throw new InvalidOperationException("participant must not be admitted"); });
            result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
            result.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
            admitted.Should().BeFalse();
            File.ReadAllBytes(evidence).Should().Equal(original);
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedLegacyProofBlocksIndexProjectionWithoutRewritingEvidence(bool asyncRead)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        var address = MemoryAddress.Parse("/notes/note");
        var legacy = new ArtifactEntry("note", "", 0, 1, DateTimeOffset.UtcNow)
        {
            Keywords = [],
            FileHashes = new Dictionary<string, string> { ["src/legacy.cs"] = "hash" }
        };
        try
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
                at == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup blocked") : null;
            store.ImportTopicEntries("local:notes", [legacy],
                new Dictionary<string, byte[]> { ["note"] = "legacy body"u8.ToArray() }, true);
            string directory = store.GetStoreDirForScope("local:notes");
            string evidence = Path.Combine(directory, ".restore-journal.json");
            string meta = Path.Combine(directory, "note.meta.json");
            byte[] originalEvidence = File.ReadAllBytes(evidence), originalMeta = File.ReadAllBytes(meta);
            Func<Task> read = async () =>
            {
                if (asyncRead) await store.ReadAsync(address);
                else store.LoadIndex("local:notes");
            };
            var blocked = (await read.Should().ThrowAsync<RestoreRecoveryException>()).Which;
            blocked.DiscoveryBlock.Should().NotBeNull();
            blocked.DiscoveryBlock!.EvidencePath.Should().Be(evidence);
            File.ReadAllBytes(evidence).Should().Equal(originalEvidence);
            File.ReadAllBytes(meta).Should().Equal(originalMeta);

            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            store.LoadIndex("local:notes").Should().ContainSingle()
                .Which.Attributes!["file"].Should().Contain("src/legacy.cs");
            File.Exists(evidence).Should().BeFalse();
        }
        finally { FileMemoryStore.RestoreJournalFailureInjector.Value = null; }
    }

    [Theory]
    [InlineData("remember")]
    [InlineData("append")]
    [InlineData("forget")]
    public async Task PublicMcpMutationReportsTheRetainedScopeBlock(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var address = MemoryAddress.Parse("/notes/note");
        await store.StoreAsync(MemoryWrite.Create(address, "local:notes", ["before"]));
        string directory = store.GetStoreDirForScope("local:notes");
        string evidence = Path.Combine(directory, ".restore-journal.json");
        try
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
                at == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup blocked") : null;
            store.ImportTopicEntries("local:notes", [(await store.ReadAsync(address))!],
                new Dictionary<string, byte[]> { ["note"] = File.ReadAllBytes(Path.Combine(directory, "note.nmp2")) }, true);
            byte[] original = File.ReadAllBytes(evidence);
            var tools = new ScriniaMcpTools();
            string yaml = operation switch
            {
                "remember" => await tools.MemoryRemember("/notes/note", ["after"], description: "after"),
                "append" => await tools.MemoryAppend("/notes/note", "after"),
                "forget" => await tools.MemoryForget("/notes/note"),
                _ => throw new InvalidOperationException(operation)
            };
            var response = ResponseParser.Parse(yaml);
            response.Status.Should().Be("error");
            response.ErrorCode.Should().Be(ErrorCodes.RecoveryBlocked);
            File.ReadAllBytes(evidence).Should().Equal(original);
        }
        finally { FileMemoryStore.RestoreJournalFailureInjector.Value = null; }
    }

}
