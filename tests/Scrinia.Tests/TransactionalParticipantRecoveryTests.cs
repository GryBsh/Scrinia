using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantRecoveryTests
{
    [Fact]
    public async Task RejectedOnlyRecoveryBuildsProjectionWithoutFactoryOrAdapterInvocation()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiMemoryTransactionRecord record = new(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-rejected", "local:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now,
            rejectedTransactionalParticipants:
            [new B4ParticipantRequestMetadata("tx-rejected", "local:notes", "/notes/b", "plugin", 1,
                "plugin-key", "op-b", "memory", TransactionalFailureCode.Permanent, "not available")]);
        MultiMemoryTransactionRecordCodec.Write(directory, record);
        var b3 = new TestAdapter("a", "op-a", "local:notes");
        var factory = new CountingFactory();

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(b3), factory).RecoverAsync("local:notes");

        result.NonAdmittedParticipants.Should().ContainSingle(item => item.ParticipantIdentity == "plugin");
        result.NonAdmittedParticipants.Single().Ordinal.Should().Be(1);
        factory.RehydrateCalls.Should().Be(0);
        factory.CreateCalls.Should().Be(0);
    }

    private static FileMemoryStore CreateStore() =>
        new(Path.Combine(Path.GetTempPath(), "scrinia-b4-recovery-" + Guid.NewGuid().ToString("N")));

    private sealed class CountingFactory : IMultiMemoryParticipantAdapterFactory
    {
        internal int CreateCalls;
        internal int RehydrateCalls;

        public Task<IMultiMemoryParticipantAdapter> CreateAsync(MultiMemoryParticipantRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException();
        }

        public Task<IMultiMemoryParticipantAdapter> RehydrateAsync(B4ParticipantRehydrationRequest request, CancellationToken cancellationToken)
        {
            RehydrateCalls++;
            throw new InvalidOperationException();
        }
    }

    private sealed class TestAdapter(string subject, string operationId, string scope) : IMultiMemoryParticipantAdapter
    {
        public string Kind => "memory";
        public string Scope => scope;
        public string Subject => subject;
        public string OperationId => operationId;
        public string EvidencePath => "notes/evidence";
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MultiMemoryCommitStatus.Prepared);
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyCommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
