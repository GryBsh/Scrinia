using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class BundleRetainedScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingBundleRefusesUntilTerminalScopeEvidenceIsRetired(bool coordinator)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        const string memoryScope = "local:notes";
        var address = MemoryAddress.Parse("/notes/note");
        await store.StoreAsync(new MemoryWrite(address, ["before"u8.ToArray()],
            new Dictionary<string, IReadOnlyList<string>>(), []));
        string directory = store.GetStoreDirForScope(memoryScope);
        string body = Path.Combine(directory, "note.nmp2");
        string sidecar = Path.Combine(directory, "note.meta.json");
        string evidence;
        try
        {
            if (coordinator)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
                    "retained-proof", memoryScope, MultiMemoryTransactionState.Committed,
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
                var entry = (await store.ReadAsync(address))!;
                FileMemoryStore.RestoreJournalFailureInjector.Value = (at, _) =>
                    at == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("cleanup blocked") : null;
                store.ImportTopicEntries(memoryScope, [entry],
                    new Dictionary<string, byte[]> { ["note"] = File.ReadAllBytes(body) }, true);
                evidence = Path.Combine(directory, ".restore-journal.json");
            }
            store.LoadIndex(memoryScope).Should().ContainSingle();
            byte[] beforeBody = File.ReadAllBytes(body);
            byte[] beforeSidecar = File.ReadAllBytes(sidecar);
            byte[] beforeEvidence = File.ReadAllBytes(evidence);
            Func<Task> restore = () => store.RestoreBundleAsync(new(
                [new(memoryScope, "notes", [new("note", "", 5, 1, DateTimeOffset.UtcNow)],
                    new Dictionary<string, byte[]> { ["note"] = "after"u8.ToArray() })], [], true));
            await restore.Should().ThrowAsync<RestoreRecoveryException>();
            File.ReadAllBytes(body).Should().Equal(beforeBody);
            File.ReadAllBytes(sidecar).Should().Equal(beforeSidecar);
            File.ReadAllBytes(evidence).Should().Equal(beforeEvidence);
            File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json")).Should().BeFalse();

            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
            (await store.ReadArtifactAsync(address)).Should().Be(System.Text.Encoding.UTF8.GetString(beforeBody));
            File.Exists(evidence).Should().BeFalse();
        }
        finally
        {
            FileMemoryStore.RestoreJournalFailureInjector.Value = null;
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
        }
    }
}
