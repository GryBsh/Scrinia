using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Scrinia.Services;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantRecoveryTests
{
    [Fact]
    public async Task LegacyLocalTopicRecoveryPreservesB3AggregateAddressConstruction()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local-topic:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-legacy", "local-topic:notes",
            MultiMemoryTransactionState.Committed,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now);
        MultiMemoryTransactionRecordCodec.Write(directory, record);
        string? receivedAddress = null;

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (manifest, _) =>
            {
                receivedAddress = manifest.Subject;
                return Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-a", "local-topic:notes"));
            }).RecoverAsync("local-topic:notes");

        receivedAddress.Should().Be("a");
        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
    }

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
                "plugin-key", "op-b", "memory", Scrinia.Core.TransactionalFailureCode.Permanent, "not available")]);
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

    [Fact]
    public void RehydrationAcceptsEquivalentPayloadAndMetadataRegardlessOfInsertionOrder()
    {
        IReadOnlyDictionary<string, string> persistedPayload =
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        IReadOnlyDictionary<string, string> rehydratedPayload =
            new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        IReadOnlyDictionary<string, string> persistedMetadata =
            new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
        IReadOnlyDictionary<string, string> rehydratedMetadata =
            new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" };

        MultiMemoryTransactionCoordinator.DictionariesMatch(persistedPayload, rehydratedPayload).Should().BeTrue();
        MultiMemoryTransactionCoordinator.DictionariesMatch(persistedMetadata, rehydratedMetadata).Should().BeTrue();
        MultiMemoryTransactionCoordinator.DictionariesMatch(
            persistedPayload, new Dictionary<string, string> { ["a"] = "different", ["b"] = "2" }).Should().BeFalse();
    }

    [Fact]
    public async Task HandlelessChangedRecoveryRetainsEvidenceAndDoesNotInvokePluginCallbacks()
    {
        using var store = CreateStore();
        string directory = store.GetStoreDirForScope("local-topic:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new B4ParticipantRecoveryState("tx-handleless", "op-b", "local-topic:notes", "/notes/b",
            "participant", 1, "participant-key", "memory", ["alpha"],
            new Dictionary<string, string> { ["body"] = "value" },
            new Dictionary<string, string> { ["source"] = "test" }, TransactionalParticipantLifecycleState.Changed,
            null, null, 1, 10, true, false, Scrinia.Core.TransactionalFailureCode.UnknownTransient,
            "evidence/1", "journal", "v1", TransactionalQuarantineState.None, null);
        var record = new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-handleless", "local-topic:notes",
            MultiMemoryTransactionState.Committed,
            [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now,
            transactionalParticipants: [state]);
        MultiMemoryTransactionRecordCodec.Write(directory, record);
        var participant = new CountingParticipant();
        var factory = new TransactionalParticipantAdapterFactory([participant]);

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-a", "local-topic:notes")),
            factory).RecoverAsync("local-topic:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        participant.CallbackCount.Should().Be(0);
        var persisted = MultiMemoryTransactionRecordCodec.Read(directory, "local-topic:notes");
        persisted.TransactionalParticipants.Should().ContainSingle().Which.Should().Match<B4ParticipantRecoveryState>(
            item => item.LifecycleState == TransactionalParticipantLifecycleState.Ambiguous &&
                item.QuarantineState == TransactionalQuarantineState.Quarantined &&
                item.EvidenceReference == "evidence/1");
        result.TransactionalParticipants.Should().Contain(item => item.EvidencePath == "evidence/1");
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

    private sealed class CountingParticipant : ITransactionalMemoryParticipant
    {
        public string Name => "test";
        public string Version => "1.0";
        public int CallbackCount { get; private set; }
        public TransactionalParticipantDescriptor Descriptor { get; } = new(
            "participant", 1, ["alpha"], new Scrinia.Plugin.Abstractions.TransactionalRetryPolicy(), "participant-key");
        public Task<TransactionalPrepareResult> PrepareAsync(TransactionalPrepareContext context, CancellationToken cancellationToken = default) { CallbackCount++; throw new InvalidOperationException(); }
        public Task<TransactionalParticipantResult> CommitAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) { CallbackCount++; throw new InvalidOperationException(); }
        public Task<TransactionalParticipantResult> RollbackAsync(TransactionalRollbackContext context, CancellationToken cancellationToken = default) { CallbackCount++; throw new InvalidOperationException(); }
        public Task<TransactionalCommitStatusResult> GetCommitStatusAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) { CallbackCount++; throw new InvalidOperationException(); }
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
