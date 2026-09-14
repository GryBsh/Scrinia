using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantRegistryTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task Rehydrate_accepts_handleless_evidence_only_recovery_states(
        int lifecycleStateValue, bool ambiguous)
    {
        var lifecycleState = (TransactionalParticipantLifecycleState)lifecycleStateValue;
        var participant = new TestParticipant();
        var factory = new TransactionalParticipantAdapterFactory([participant]);
        var state = new B4ParticipantRecoveryState("tx-1", "op-1", "local-topic:notes", "/notes/b",
            "participant", 1, "participant-key", "memory", ["alpha"],
            new Dictionary<string, string> { ["body"] = "value" },
            new Dictionary<string, string> { ["source"] = "test" }, lifecycleState, null, null, 1, 10,
            true, ambiguous, Scrinia.Core.TransactionalFailureCode.UnknownTransient, "evidence/1", "journal", "v1",
            ambiguous ? TransactionalQuarantineState.Quarantined : TransactionalQuarantineState.None,
            ambiguous ? "ambiguous participant state" : null);
        var request = new B4ParticipantRehydrationRequest(state,
            new MultiMemoryParticipantManifest("/notes/b", "op-1", "memory"), ["alpha"]);

        var adapter = await factory.RehydrateAsync(request, CancellationToken.None);

        adapter.RecoveryState.Should().BeEquivalentTo(state);
    }

    [Fact]
    public void Transactional_request_copies_and_orders_its_durable_input()
    {
        var payload = new Dictionary<string, string> { ["body"] = "value" };
        var metadata = new Dictionary<string, string> { ["source"] = "test" };
        var participant = new MultiMemoryTransactionalParticipantRequest(
            MemoryAddress.Parse("/notes/a"), "participant", 1, "participant-key", "op-1", "memory",
            payload, metadata);

        payload["body"] = "changed";
        metadata.Clear();

        var request = new MultiMemoryTransactionRequest("local-topic:notes",
            [new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/b"), "op-b", "memory")], [participant]);
        request.TransactionalParticipants.Should().ContainSingle();
        request.TransactionalParticipants[0].Payload["body"].Should().Be("value");
        request.TransactionalParticipants[0].Metadata["source"].Should().Be("test");
    }

    private sealed class TestParticipant : ITransactionalMemoryParticipant
    {
        public string Name => "test";
        public string Version => "1.0";
        public TransactionalParticipantDescriptor Descriptor { get; } = new(
            "participant", 1, ["alpha"], new Scrinia.Plugin.Abstractions.TransactionalRetryPolicy(), "participant-key");
        public Task<TransactionalPrepareResult> PrepareAsync(TransactionalPrepareContext context, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TransactionalParticipantResult> CommitAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TransactionalParticipantResult> RollbackAsync(TransactionalRollbackContext context, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TransactionalCommitStatusResult> GetCommitStatusAsync(TransactionalCommitContext context, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
