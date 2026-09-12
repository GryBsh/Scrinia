using FluentAssertions;
using System.Diagnostics;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MultiMemoryTransactionAdmissionTests
{
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
    public async Task ExecuteAsync_DoesNotClaimCompletionBeforeLifecycleTask()
    {
        using FileMemoryStore store = CreateStore();
        var request = new MultiMemoryTransactionRequest("local:notes", [Participant("/notes/a", "op-1")]);

        Func<Task> action = () => new MultiMemoryTransactionCoordinator(store).ExecuteAsync(request, (_, _) =>
            Task.FromResult<IMultiMemoryParticipantAdapter>(new TestAdapter("a", "op-1")));

        await action.Should().ThrowAsync<NotSupportedException>();
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

    private static MultiMemoryParticipantRequest Participant(string address, string operationId) =>
        new(MemoryAddress.Parse(address), operationId, "memory");

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
        public string Subject { get; }
        public string OperationId { get; }
        public string EvidencePath => "notes/evidence";
        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyCommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
