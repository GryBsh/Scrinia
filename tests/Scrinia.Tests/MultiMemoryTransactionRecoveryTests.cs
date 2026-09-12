using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionRecoveryTests
{
    [Fact]
    public async Task RecoverAsync_MalformedRecordIsRetainedAndTyped()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        string path = MultiMemoryTransactionRecordCodec.GetPath(directory);
        File.WriteAllText(path, "{\"format\":\"scrinia.multi-memory-transaction\"");

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store)
            .RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Malformed);
        result.Warning.Should().NotContain("format");
        File.ReadAllText(path).Should().StartWith("{\"format\"");
    }

    [Fact]
    public async Task RecoverAsync_MismatchedAdapterIsAmbiguousAndRetainsEvidence()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiMemoryTransactionRecordCodec.Write(directory, new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-4", "local:notes",
            MultiMemoryTransactionState.Committed,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now));
        var adapter = new RecoveryTestAdapter("wrong", "op-a");

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Ambiguous);
        adapter.CommitCalls.Should().Be(0);
        File.Exists(MultiMemoryTransactionRecordCodec.GetPath(directory)).Should().BeTrue();
    }

    [Fact]
    public void FileMemoryStoreRecovery_IsolatedToTheSelectedScope()
    {
        using var store = CreateStore();
        string blockedDirectory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(blockedDirectory);
        File.WriteAllText(MultiMemoryTransactionRecordCodec.GetPath(blockedDirectory), "not-json");

        FluentActions.Invoking(() => store.LoadIndex("local:notes"))
            .Should().Throw<RestoreRecoveryException>();
        store.LoadIndex("local:other").Should().BeEmpty();
    }

    [Fact]
    public void FreshFileMemoryStore_UsesPersistedCompletionProofForCleanupOnlyRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-proof-" + Guid.NewGuid().ToString("N"));
        string recordPath;
        using (var initial = CreateStore(root))
        {
            string directory = initial.GetStoreDirForScope("local:notes");
            Directory.CreateDirectory(directory);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
                "tx-proof", "local:notes", MultiMemoryTransactionState.Committed,
                [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now);
            record = MultiMemoryTransactionRecordCodec.WithCompletionProof(record,
                [new MultiMemoryParticipantCompletion("a", "op-a", "memory", "versions/.append-journal.json")], now);
            MultiMemoryTransactionRecordCodec.Write(directory, record);
            recordPath = MultiMemoryTransactionRecordCodec.GetPath(directory);
        }

        using var restarted = CreateStore(root);
        restarted.LoadIndex("local:notes").Should().BeEmpty();
        File.Exists(recordPath).Should().BeFalse();
    }

    private static FileMemoryStore CreateStore() =>
        new(Path.Combine(Path.GetTempPath(), "scrinia-b3-recovery-" + Guid.NewGuid().ToString("N")));

    private static FileMemoryStore CreateStore(string root) => new(root);

    private sealed class RecoveryTestAdapter : IMultiMemoryParticipantAdapter
    {
        internal RecoveryTestAdapter(string subject, string operationId) =>
            (Subject, OperationId) = (subject, operationId);

        public string Kind => "memory";
        public string Scope => "local:notes";
        public string Subject { get; }
        public string OperationId { get; }
        public string EvidencePath => "notes/evidence";
        internal int CommitCalls { get; private set; }
        public Task PrepareAsync(CancellationToken _) => Task.CompletedTask;
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken _) =>
            Task.FromResult(MultiMemoryCommitStatus.Unknown);
        public Task CommitAsync(CancellationToken _) { CommitCalls++; return Task.CompletedTask; }
        public Task VerifyCommittedAsync(CancellationToken _) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken _) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken _) => Task.CompletedTask;
    }
}
