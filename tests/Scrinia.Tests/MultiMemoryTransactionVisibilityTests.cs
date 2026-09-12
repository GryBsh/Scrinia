using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionVisibilityTests
{
    [Fact]
    public void InvalidateCommittedScope_DropsOnlyTheAffectedScopeViews()
    {
        using var store = CreateStore();
        store.SaveIndex([Entry("old-notes")], "local:notes");
        store.SaveIndex([Entry("old-other")], "local:other");

        _ = store.LoadIndex("local:notes");
        _ = store.LoadIndex("local:other");

        string existingSidecar = File.ReadAllText(store.MetaPath("old-notes", "local:notes"));
        File.WriteAllText(store.MetaPath("new-notes", "local:notes"), existingSidecar.Replace("old-notes", "new-notes"));
        store.InvalidateCommittedScope("local:notes");

        store.LoadIndex("local:notes").Should().ContainSingle(entry => entry.Name == "new-notes");
        store.LoadIndex("local:other").Should().ContainSingle(entry => entry.Name == "old-other");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidatesTheScopeOnceAfterAggregateVerification()
    {
        using var store = CreateStore();
        var adapters = new[] { new TestAdapter("a", "op-a"), new TestAdapter("b", "op-b") };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
            Request(), (participant, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(
                adapters.Single(adapter => adapter.Subject == LastSegment(participant.Address.Value))));

        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
        store.CommittedScopeInvalidationCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_VerificationFailureDoesNotPublishCommittedInvalidation()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a") { FailVerify = true };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
            new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        store.CommittedScopeInvalidationCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupPendingKeepsCommittedVisibilityAndInvalidatesOnce()
    {
        using var store = CreateStore();
        var adapter = new TestAdapter("a", "op-a");
        MultiMemoryTransactionCoordinator.FailureInjector.Value = (transition, _) =>
            transition == MultiMemoryTransactionTransition.RecordDeleted ? new IOException("injected") : null;
        try
        {
            MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
                new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]),
                (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter));

            result.Outcome.Should().Be(MultiMemoryOutcome.CommittedCleanupPending);
            store.CommittedScopeInvalidationCount.Should().Be(1);
            store.LoadIndex("local:notes").Should().BeEmpty();
        }
        finally
        {
            MultiMemoryTransactionCoordinator.FailureInjector.Value = null;
        }
    }

    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 1, 1, DateTimeOffset.UtcNow);

    private static MultiMemoryTransactionRequest Request() => new("local:notes", [
        Participant("/notes/b", "op-b"), Participant("/notes/a", "op-a")]);

    private static MultiMemoryParticipantRequest Participant(string address, string operationId) =>
        new(MemoryAddress.Parse(address), operationId, "memory");

    private static string LastSegment(string value) => value[(value.LastIndexOf('/') + 1)..];

    private static FileMemoryStore CreateStore() =>
        new(Path.Combine(Path.GetTempPath(), "scrinia-b3-visibility-" + Guid.NewGuid().ToString("N")));

    private sealed class TestAdapter(string subject, string operationId) : IMultiMemoryParticipantAdapter
    {
        public string Kind => "memory";
        public string Scope => "local:notes";
        public string Subject { get; } = subject;
        public string OperationId { get; } = operationId;
        public string EvidencePath => "notes/evidence";
        public bool FailVerify { get; init; }
        public Task PrepareAsync(CancellationToken _) => Task.CompletedTask;
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken _) =>
            Task.FromResult(MultiMemoryCommitStatus.Prepared);
        public Task CommitAsync(CancellationToken _) => Task.CompletedTask;
        public Task VerifyCommittedAsync(CancellationToken _) =>
            FailVerify ? Task.FromException(new IOException("injected")) : Task.CompletedTask;
        public Task RollbackAsync(CancellationToken _) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken _) => Task.CompletedTask;
    }
}
