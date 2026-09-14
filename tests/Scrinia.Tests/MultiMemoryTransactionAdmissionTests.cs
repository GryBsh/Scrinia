using FluentAssertions;
using System.Diagnostics;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionAdmissionTests
{
    [Fact]
    public void TransactionalParticipantsAreOrderedAfterB3Participants()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes",
            [Participant("/notes/z", "op-z")],
            [TransactionalParticipant("/notes/a", "plugin-a", "plugin-key", "op-a")]);

        MultiMemoryTransactionCoordinator.NormalizeAndOrder(store, request)
            .Select(participant => participant.Address.Value)
            .Should().Equal("/notes/z", "/notes/a");
    }

    [Fact]
    public async Task ExecuteAsync_PersistsProjectionOnlyMetadataForRejectedB4Participant()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/z", "op-z")],
            [TransactionalParticipant("/notes/a", "plugin-a", "plugin-key", "op-a")]);

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(
            request,
            (participant, _) => participant.ParticipantIdentity is not null
                ? throw new InvalidOperationException("participant unavailable")
                : Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("z", "op-z")));

        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
        result.NonAdmittedParticipants.Should().ContainSingle(item => item.ParticipantIdentity == "plugin-a");
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"), MultiMemoryTransactionRecordCodec.FileName))
            .Should().BeFalse();
    }

    [Fact]
    public void NormalizeAndOrder_UsesCanonicalSubjectThenOperationId()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/z", "op-2"),
            Participant("/notes/a", "op-1"),
            Participant("/notes/a2", "op-3")]);

        MultiMemoryTransactionCoordinator.NormalizeAndOrder(store, request)
            .Select(participant => participant.Address.Value)
            .Should().Equal("/notes/a", "/notes/a2", "/notes/z");
    }

    [Fact]
    public async Task AdmitAsync_ProducesTheSameParticipantOrderForEveryPermutation()
    {
        using FileMemoryStore store = CreateStore();
        MultiMemoryParticipantRequest[] participants = [
            Participant("/notes/c", "op-c"), Participant("/notes/a", "op-a"), Participant("/notes/b", "op-b")];
        foreach (MultiMemoryParticipantRequest[] permutation in Permutations(participants))
        {
            MultiMemoryTransactionRequest request = new("local:notes", permutation);
            MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(
                request, (participant, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter(
                    participant.Address.Value[(participant.Address.Value.LastIndexOf('/') + 1)..],
                    participant.OperationId)));

            result.Participants.Select(adapter => adapter.Subject).Should().Equal("a", "b", "c");
        }
    }

    [Fact]
    public void NormalizeAndOrder_RejectsDuplicateOperationIds()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/a", "op-1"), Participant("/notes/b", "op-1")]);

        FluentActions.Invoking(() => MultiMemoryTransactionCoordinator.NormalizeAndOrder(store, request))
            .Should().Throw<MultiMemoryAdmissionException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
    }

    [Fact]
    public void NormalizeAndOrder_RejectsSubjectsThatOnlyDifferByCase()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/A", "op-1"), Participant("/notes/a", "op-2")]);

        FluentActions.Invoking(() => MultiMemoryTransactionCoordinator.NormalizeAndOrder(store, request))
            .Should().Throw<MultiMemoryAdmissionException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
    }

    [Fact]
    public void NormalizeAndOrder_RejectsEphemeralFileEntityAndCrossScopeSubjects()
    {
        using FileMemoryStore store = CreateStore();
        var cases = new[]
        {
            new MultiMemoryTransactionRequest("local:notes", [Participant("~scratch", "op-1")]),
            new MultiMemoryTransactionRequest("local:notes", [Participant("/skills/qa", "op-1")]),
            new MultiMemoryTransactionRequest("local:notes", [Participant("/agent/profile", "op-1")]),
            new MultiMemoryTransactionRequest("local:notes", [Participant("/other/item", "op-1")])
        };

        foreach (MultiMemoryTransactionRequest request in cases)
        {
            FluentActions.Invoking(() => MultiMemoryTransactionCoordinator.NormalizeAndOrder(store, request))
                .Should().Throw<MultiMemoryAdmissionException>()
                .Which.Failure.Should().Be(MultiMemoryFailure.Admission);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsBeforeFactoryWhenRecoveryEvidenceBlocksScope()
    {
        using FileMemoryStore store = CreateStore();
        string scopeDirectory = store.GetStoreDirForScope("local:notes");
        Directory.CreateDirectory(scopeDirectory);
        File.WriteAllText(Path.Combine(scopeDirectory, ".multi-memory-transaction.json"), "{");
        int factoryCalls = 0;
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromMilliseconds(100)).AdmitAsync(request, (_, _) =>
        {
            factoryCalls++;
            return Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1"));
        });

        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.Malformed);
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_OrdersFactoryCallsAndRejectsIncompatibleAdapterWithoutPrepare()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/z", "op-z"), Participant("/notes/a", "op-a")]);
        var calls = new List<string>();

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromMilliseconds(100)).AdmitAsync(request, (participant, _) =>
        {
            calls.Add(participant.Address.Value);
            string subject = participant.Address.Value[(participant.Address.Value.LastIndexOf('/') + 1)..];
            return Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter(subject, participant.OperationId,
                subject == "a" ? "wrong-kind" : "memory"));
        });

        calls.Should().Equal("/notes/a");
        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
    }

    [Fact]
    public async Task AdmitAsync_ReleasesEarlierAdaptersWhenLaterFactoryFails()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/a", "op-a"), Participant("/notes/b", "op-b")]);
        var first = new TestAdapter("a", "op-a");

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(
            request, (participant, _) => participant.Address.Value.EndsWith("/a", StringComparison.Ordinal)
                ? Task.FromResult<IMultiMemoryParticipantAdapter>(first)
                : throw new InvalidOperationException("factory failure"));

        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.Admission);
        first.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task AdmitAsync_DisposesARepeatedAdapterReferenceOnlyOnce()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/a", "op-a"), Participant("/notes/b", "op-b")]);
        var shared = new TestAdapter("a", "op-a");

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(
            request, (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(shared));

        result.IsAdmitted.Should().BeFalse();
        shared.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_ContinuesCleanupWhenOneAdapterDisposeThrows()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [
            Participant("/notes/a", "op-a"), Participant("/notes/b", "op-b"), Participant("/notes/c", "op-c")]);
        var first = new TestAdapter("a", "op-a") { ThrowsOnDispose = true };
        var second = new TestAdapter("b", "op-b");

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(
            request, (participant, _) => participant.Address.Value.EndsWith("/a", StringComparison.Ordinal)
                ? Task.FromResult<IMultiMemoryParticipantAdapter>(first)
                : participant.Address.Value.EndsWith("/b", StringComparison.Ordinal)
                    ? Task.FromResult<IMultiMemoryParticipantAdapter>(second)
                    : throw new InvalidOperationException("factory failure"));

        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.Admission);
        result.CleanupFailed.Should().BeTrue();
        first.DisposeCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_RejectsBlankAdapterIdentity()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]);

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(
            request, (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("", "op-a")));

        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
    }

    [Fact]
    public async Task ExecuteAsync_MapsLockTimeoutToTypedRejection()
    {
        using FileMemoryStore store = CreateStore();
        string lockPath = Path.Combine(store.GetStoreDirForScope("local:notes"), ".lock");
        using FileLock held = FileLock.AcquireExclusive(lockPath);
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);

        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromMilliseconds(25)).AdmitAsync(request, (_, _) =>
            Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1")));

        result.IsAdmitted.Should().BeFalse();
        result.Failure.Should().Be(MultiMemoryFailure.LockTimeout);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsCancellationBeforeAdmission()
    {
        using FileMemoryStore store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int factoryCalls = 0;
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);

        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> factory =
            (_, _) =>
            {
                factoryCalls++;
                return Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1"));
            };

        Func<Task> action = () => new MultiMemoryTransactionCoordinator(store).AdmitAsync(
            request, factory, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        factoryCalls.Should().Be(0);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"),
            MultiMemoryTransactionRecordCodec.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotUseAnotherScopeLock()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:other", [Participant("/other/a", "op-1")]);
        MultiMemoryAdmissionResult result = await new MultiMemoryTransactionCoordinator(store).AdmitAsync(request, (_, _) =>
            Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1")));

        result.IsAdmitted.Should().BeTrue();
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"), ".lock")).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CompletesTheLifecycleAfterAdmission()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);

        MultiMemoryAggregateResult result = await new MultiMemoryTransactionCoordinator(store).ExecuteAsync(request, (_, _) =>
            Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1")));

        result.Outcome.Should().Be(MultiMemoryOutcome.Committed);
        File.Exists(Path.Combine(store.GetStoreDirForScope("local:notes"),
            MultiMemoryTransactionRecordCodec.FileName)).Should().BeFalse();
    }

    [Fact]
    public async Task AdmitAsync_CancellationDuringLockWaitIsPromptAndDistinctFromTimeout()
    {
        using FileMemoryStore store = CreateStore();
        string lockPath = Path.Combine(store.GetStoreDirForScope("local:notes"), ".lock");
        using FileLock held = FileLock.AcquireExclusive(lockPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);
        Stopwatch timer = Stopwatch.StartNew();

        Func<Task> action = () => new MultiMemoryTransactionCoordinator(store, TimeSpan.FromSeconds(5))
            .AdmitAsync(request, (_, _) => Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1")),
                cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AdmitAsync_SerializesOneScopeWhileAnotherTopicProgresses()
    {
        using FileMemoryStore store = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-a")]);

        Task<MultiMemoryAdmissionResult> first = new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromSeconds(2)).AdmitAsync(firstRequest, async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new TestAdapter("a", "op-a");
        });
        await entered.Task;

        MultiMemoryAdmissionResult sameScope = await new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromMilliseconds(30)).AdmitAsync(firstRequest, (_, _) =>
            Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-a-2")));
        MultiMemoryAdmissionResult otherScope = await new MultiMemoryTransactionCoordinator(store,
            TimeSpan.FromMilliseconds(30)).AdmitAsync(
                new MultiMemoryTransactionRequest("local:other", [Participant("/other/a", "op-a")]), (_, _) =>
                    Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-a")));

        sameScope.Failure.Should().Be(MultiMemoryFailure.LockTimeout);
        otherScope.IsAdmitted.Should().BeTrue();
        release.SetResult();
        await first;
    }

    private static MultiMemoryParticipantRequest Participant(string address, string operationId) =>
        new(MemoryAddress.Parse(address), operationId, "memory");

    private static MultiMemoryTransactionalParticipantRequest TransactionalParticipant(
        string address, string identity, string orderingKey, string operationId) =>
        new(MemoryAddress.Parse(address), identity, 1, orderingKey, operationId, "plugin",
            new Dictionary<string, string>(), new Dictionary<string, string>());

    private static FileMemoryStore CreateStore() =>
        new(Path.Combine(Path.GetTempPath(), "scrinia-b3-admission-" + Guid.NewGuid().ToString("N")));

    private static IEnumerable<MultiMemoryParticipantRequest[]> Permutations(
        MultiMemoryParticipantRequest[] source)
    {
        yield return source;
        yield return [source[0], source[2], source[1]];
        yield return [source[1], source[0], source[2]];
        yield return [source[1], source[2], source[0]];
        yield return [source[2], source[0], source[1]];
    }

    private sealed class TestAdapter : IMultiMemoryParticipantAdapter, IDisposable
    {
        internal TestAdapter(string subject, string operationId, string kind = "memory") =>
            (Subject, OperationId, Kind) = (subject, operationId, kind);

        public string Kind { get; }
        public string Scope => "local:notes";
        public string Subject { get; }
        public string OperationId { get; }
        public string EvidencePath => "notes/evidence";
        public Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken _) =>
            Task.FromResult(MultiMemoryCommitStatus.Unknown);
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyCommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ThrowsOnDispose { get; init; }
        public void Dispose()
        {
            DisposeCount++;
            IsDisposed = true;
            if (ThrowsOnDispose)
                throw new InvalidOperationException("dispose failure");
        }
    }
}
