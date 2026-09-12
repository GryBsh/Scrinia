using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionLifecycleTests
{
    [Fact]
    public async Task ExecuteAsync_PublishesDecisionThenCommitsVerifiesCleansAndDeletesRecord()
    {
        using var store = CreateStore();
        var calls = new List<string>();
        var adapters = new[] { new TestAdapter("a", "op-a", calls), new TestAdapter("b", "op-b", calls) };

        MultiMemoryAggregateResult result = await CreateCoordinator(store).ExecuteAsync(
            Request(), (participant, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(
                adapters.Single(adapter => adapter.Subject == LastSegment(participant.Address.Value))));

        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
        result.Participants.Select(participant => participant.Subject).Should().Equal("a", "b");
        calls.Should().Equal("prepare:a", "prepare:b", "commit:a", "verify:a", "commit:b", "verify:b", "cleanup:a", "cleanup:b");
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"), MultiMemoryTransactionRecordCodec.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PreDecisionFailureRollsBackAndDoesNotLeaveDecision()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a", []) { FailPrepare = true };

        MultiMemoryAggregateResult result = await CreateCoordinator(store).ExecuteAsync(
            new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

        result.Outcome.Should().Be(MultiMemoryOutcome.RolledBack);
        result.Failure.Should().BeNull();
        adapter.Calls.Should().Contain("rollback:a");
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"), MultiMemoryTransactionRecordCodec.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PostDecisionFailureRetainsRecordAndNeverRollsBack()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a", []) { FailCommit = true };
        var coordinator = CreateCoordinator(store);

        MultiMemoryAggregateResult result = await coordinator.ExecuteAsync(
            new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        adapter.Calls.Should().NotContain("rollback:a");
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"), MultiMemoryTransactionRecordCodec.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_VerificationFailureIsTypedSeparatelyFromCommitFailure()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a", []) { FailVerify = true };

        MultiMemoryAggregateResult result = await CreateCoordinator(store).ExecuteAsync(
            new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Verification);
        result.Participants.Single().Subject.Should().Be("a");
    }

    [Fact]
    public async Task RecoverAsync_SkipsCommitWhenAdapterProvesCommitted()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
            "tx-1", "local:notes", MultiMemoryTransactionState.Committed,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now);
        MultiMemoryTransactionRecordCodec.Write(directory, record);
        var adapter = new TestAdapter("a", "op-a", []) { Status = MultiMemoryCommitStatus.Committed };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
        adapter.Calls.Should().NotContain("commit:a");
    }

    [Fact]
    public async Task RecoverAsync_RecoveryRequiredRecordFailsClosedWithoutCommit()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new MultiMemoryTransactionRecord(MultiMemoryTransactionRecordCodec.FormatName, 1,
            "tx-2", "local:notes", MultiMemoryTransactionState.RecoveryRequired,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now);
        MultiMemoryTransactionRecordCodec.Write(directory, record);
        var adapter = new TestAdapter("a", "op-a", []);

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Ambiguous);
        adapter.Calls.Should().NotContain("commit:a");
        File.Exists(MultiMemoryTransactionRecordCodec.GetPath(directory)).Should().BeTrue();
    }

    [Fact]
    public void FileMemoryStoreRead_DoesNotBypassAValidCoordinatorRecord()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiMemoryTransactionRecordCodec.Write(directory, new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-3", "local:notes",
            MultiMemoryTransactionState.RecoveryRequired,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now));

        FluentActions.Invoking(() => store.LoadIndex("local:notes"))
            .Should().Throw<RestoreRecoveryException>();
        File.Exists(MultiMemoryTransactionRecordCodec.GetPath(directory)).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CommittedPublicationFailureRollsBackBeforeDecision()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a", []);
        MultiMemoryTransactionCoordinator.FailureInjector.Value = (transition, _) =>
            transition == MultiMemoryTransactionTransition.CommittedPublished ? new IOException("injected") : null;
        try
        {
            MultiMemoryAggregateResult result = await CreateCoordinator(store).ExecuteAsync(
                new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
                (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

            result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
            adapter.Calls.Should().NotContain("rollback:a");
        }
        finally { MultiMemoryTransactionCoordinator.FailureInjector.Value = null; }
    }

    private static MultiMemoryTransactionCoordinator CreateCoordinator(FileMemoryStore store) =>
        new(store, TimeSpan.FromSeconds(1));

    private static MultiMemoryTransactionRequest Request() => new("local:notes", [
        Participant("/notes/b", "op-b"), Participant("/notes/a", "op-a")]);

    private static MultiMemoryParticipantRequest Participant(string address, string operationId) =>
        new(MemoryAddress.Parse(address), operationId, "memory");

    private static string LastSegment(string value) => value[(value.LastIndexOf('/') + 1)..];

    private static FileMemoryStore CreateStore() =>
        new(Path.Combine(Path.GetTempPath(), "scrinia-b3-lifecycle-" + Guid.NewGuid().ToString("N")));

    private sealed class TestAdapter : IMultiMemoryParticipantAdapter
    {
        internal TestAdapter(string subject, string operationId, List<string> calls) =>
            (Subject, OperationId, Calls) = (subject, operationId, calls);

        public string Kind => "memory";
        public string Scope => "local:notes";
        public string Subject { get; }
        public string OperationId { get; }
        public string EvidencePath => "notes/evidence";
        public bool? ObservableStateChanged => true;
        internal List<string> Calls { get; }
        internal bool FailPrepare { get; init; }
        internal bool FailCommit { get; init; }
        internal bool FailVerify { get; init; }
        internal MultiMemoryCommitStatus Status { get; init; }
        public Task PrepareAsync(CancellationToken _) => Invoke("prepare", FailPrepare);
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken _) =>
            Task.FromResult(Status);
        public Task CommitAsync(CancellationToken _) => Invoke("commit", FailCommit);
        public Task VerifyCommittedAsync(CancellationToken _) => Invoke("verify", FailVerify);
        public Task RollbackAsync(CancellationToken _) => Invoke("rollback", false);
        public Task CleanupAsync(CancellationToken _) => Invoke("cleanup", false);

        private Task Invoke(string phase, bool fail)
        {
            Calls.Add($"{phase}:{Subject}");
            return fail ? Task.FromException(new IOException("injected")) : Task.CompletedTask;
        }
    }
}
