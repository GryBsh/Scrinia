using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

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
    public async Task InvalidateRollbackScope_DropsArtifactCacheWithoutTouchingAnotherScope()
    {
        using var store = CreateStore();
        await store.WriteArtifactAsync(MemoryAddress.Parse("/notes/a"), "old-notes");
        await store.WriteArtifactAsync(MemoryAddress.Parse("/other/a"), "old-other");
        _ = await store.ReadArtifactAsync(MemoryAddress.Parse("/notes/a"));
        _ = await store.ReadArtifactAsync(MemoryAddress.Parse("/other/a"));

        await File.WriteAllTextAsync(store.ArtifactPath("a", "local:notes"), "new-notes");
        store.InvalidateScopeCacheAfterRollback("local:notes");

        (await store.ReadArtifactAsync(MemoryAddress.Parse("/notes/a"))).Should().Be("new-notes");
        (await store.ReadArtifactAsync(MemoryAddress.Parse("/other/a"))).Should().Be("old-other");
    }

    [Fact]
    public void InvalidateRollbackScope_RebuildsFactsAndTopicsButPreservesOtherTopicDiscovery()
    {
        using var store = CreateStore();
        var entry = new ArtifactEntry("note", "", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] });
        store.Upsert(entry, "local:notes");
        store.GetEntityFactStore().Entities("person").Should().Contain(EntityRef.Of("person", "maria"));
        store.DiscoverTopics().Should().Contain("local:notes");

        string sidecarPath = store.MetaPath("note", "local:notes");
        File.WriteAllText(sidecarPath, File.ReadAllText(sidecarPath).Replace("maria", "jolene"));
        string newTopicSidecar = store.MetaPath("new", "local:new-topic");
        Directory.CreateDirectory(Path.GetDirectoryName(newTopicSidecar)!);
        File.Copy(sidecarPath, newTopicSidecar);
        store.InvalidateScopeCacheAfterRollback("local:notes");

        store.GetEntityFactStore().Entities("person").Should().Contain(EntityRef.Of("person", "jolene"));
        store.GetEntityFactStore().Entities("person").Should().NotContain(EntityRef.Of("person", "maria"));
        store.DiscoverTopics().Should().Contain("local:new-topic");
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

    [Fact]
    public async Task RecoverAsync_PreparedRollbackInvalidatesWhenVisibleFilesChanged()
    {
        using var store = CreateStore();
        WritePreparedRecord(store, "tx-changed");
        var adapter = new TestAdapter("a", "op-a") { ObservableStateChanged = true };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RolledBack);
        store.CommittedScopeInvalidationCount.Should().Be(0);
        store.RollbackScopeInvalidationCount.Should().Be(1);
        adapter.RollbackCalls.Should().Be(1);
    }

    [Fact]
    public async Task RecoverAsync_PreparedNoOpRollbackDoesNotInvalidateDerivedState()
    {
        using var store = CreateStore();
        WritePreparedRecord(store, "tx-no-op");
        var adapter = new TestAdapter("a", "op-a") { ObservableStateChanged = false };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RolledBack);
        store.CommittedScopeInvalidationCount.Should().Be(0);
        store.RollbackScopeInvalidationCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoverAsync_PreparedUnknownRollbackStateRetainsRecoveryEvidence()
    {
        using var store = CreateStore();
        WritePreparedRecord(store, "tx-unknown");
        var adapter = new TestAdapter("a", "op-a");

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store,
            (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapter)).RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Ambiguous);
        store.RollbackScopeInvalidationCount.Should().Be(0);
        File.Exists(MultiMemoryTransactionRecordCodec.GetPath(store.GetStoreDirForScope("local:notes")))
            .Should().BeTrue();
    }

    [Fact]
    public async Task RecoverAsync_PreparedRollbackKeepsEarlierChangeEvidenceWhenLaterRollbackFails()
    {
        using var store = CreateStore();
        WritePreparedRecord(store, "tx-partial-failure", [
            new MultiMemoryParticipantManifest("a", "op-a", "memory"),
            new MultiMemoryParticipantManifest("b", "op-b", "memory")]);
        var adapters = new[]
        {
            new TestAdapter("a", "op-a") { ObservableStateChanged = true, FailRollback = true },
            new TestAdapter("b", "op-b") { ObservableStateChanged = true }
        };

        Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> resolver =
            (_, manifest, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapters.Single(adapter => adapter.Subject == manifest.Subject));
        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store, resolver)
            .RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.ReplayIoFailed);
        store.RollbackScopeInvalidationCount.Should().Be(1);
        adapters.Sum(adapter => adapter.RollbackCalls).Should().Be(2);
        result.Participants.Select(participant => participant.Subject).Should().Equal("a", "b");
        result.Participants.Single(participant => participant.Subject == "a").RolledBack.Should().BeFalse();
        result.Participants.Single(participant => participant.Subject == "a").Reason.Should().NotBeNullOrWhiteSpace();
        result.Participants.Single(participant => participant.Subject == "b").RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task RecoverAsync_PreparedChangedAndUnknownRollbackInvalidatesBeforeFailingClosed()
    {
        using var store = CreateStore();
        WritePreparedRecord(store, "tx-changed-unknown", [
            new MultiMemoryParticipantManifest("a", "op-a", "memory"),
            new MultiMemoryParticipantManifest("b", "op-b", "memory")]);
        var adapters = new[]
        {
            new TestAdapter("a", "op-a") { ObservableStateChanged = null },
            new TestAdapter("b", "op-b") { ObservableStateChanged = true }
        };

        Func<string, MultiMemoryParticipantManifest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> resolver =
            (_, manifest, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(adapters.Single(adapter => adapter.Subject == manifest.Subject));
        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store, resolver)
            .RecoverAsync("local:notes");

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Ambiguous);
        store.RollbackScopeInvalidationCount.Should().Be(1);
        result.Participants.Select(participant => participant.Subject).Should().Equal("a", "b");
        result.Participants.All(participant => participant.RolledBack).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PreDecisionRollbackKeepsEarlierChangeEvidenceWhenLaterRollbackFails()
    {
        using var store = CreateStore();
        var adapters = new[]
        {
            new TestAdapter("a", "op-a") { ObservableStateChanged = true, FailRollback = true },
            new TestAdapter("b", "op-b") { ObservableStateChanged = true, FailPrepare = true }
        };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
            Request(), (participant, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(
                adapters.Single(adapter => adapter.Subject == LastSegment(participant.Address.Value))));

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        store.RollbackScopeInvalidationCount.Should().Be(1);
        adapters.Sum(adapter => adapter.RollbackCalls).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_PreDecisionChangedAndUnknownRollbackInvalidatesBeforeFailingClosed()
    {
        using var store = CreateStore();
        var adapters = new[]
        {
            new TestAdapter("a", "op-a") { ObservableStateChanged = null },
            new TestAdapter("b", "op-b") { ObservableStateChanged = true, FailPrepare = true }
        };

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
            Request(), (participant, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(
                adapters.Single(adapter => adapter.Subject == LastSegment(participant.Address.Value))));

        result.Outcome.Should().Be(MultiMemoryOutcome.RecoveryRequired);
        result.Failure.Should().Be(MultiMemoryFailure.Ambiguous);
        store.RollbackScopeInvalidationCount.Should().Be(1);
    }

    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 1, 1, DateTimeOffset.UtcNow);

    private static MultiMemoryTransactionRequest Request() => new("local:notes", [
        Participant("/notes/b", "op-b"), Participant("/notes/a", "op-a")]);

    private static MultiMemoryParticipantRequest Participant(string address, string operationId) =>
        new(MemoryAddress.Parse(address), operationId, "memory");

    private static string LastSegment(string value) => value[(value.LastIndexOf('/') + 1)..];

    private static void WritePreparedRecord(FileMemoryStore store, string transactionId,
        IReadOnlyList<MultiMemoryParticipantManifest>? participants = null)
    {
        string directory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiMemoryTransactionRecordCodec.Write(directory, new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, transactionId, "local:notes",
            MultiMemoryTransactionState.Prepared,
            participants ?? [new MultiMemoryParticipantManifest("a", "op-a", "memory")], now, now));
    }

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
        public bool FailPrepare { get; init; }
        public bool FailRollback { get; init; }
        public bool? ObservableStateChanged { get; init; }
        public int RollbackCalls { get; private set; }
        public Task PrepareAsync(CancellationToken _) =>
            FailPrepare ? Task.FromException(new IOException("injected prepare failure")) : Task.CompletedTask;
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken _) =>
            Task.FromResult(MultiMemoryCommitStatus.Prepared);
        public Task CommitAsync(CancellationToken _) => Task.CompletedTask;
        public Task VerifyCommittedAsync(CancellationToken _) =>
            FailVerify ? Task.FromException(new IOException("injected")) : Task.CompletedTask;
        public Task RollbackAsync(CancellationToken _)
        {
            RollbackCalls++;
            return FailRollback ? Task.FromException(new IOException("injected rollback failure")) : Task.CompletedTask;
        }
        public Task CleanupAsync(CancellationToken _) => Task.CompletedTask;
    }
}
