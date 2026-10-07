using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

public sealed class IndexProjectionAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedProjectionRequestsRecheckBeforeTakingExclusiveOwnership(bool legacyLayout)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, legacyLayout);
        using var requested = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        int exclusive = 0;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase == "requested")
            {
                requested.Signal();
                release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
            if (phase == "exclusive-start") Interlocked.Increment(ref exclusive);
        };
        Task<IReadOnlyList<ArtifactEntry>> first = Task.Run(() => store.LoadIndex());
        Task<IReadOnlyList<ArtifactEntry>> second = Task.Run(() => store.LoadIndex());
        try
        {
            requested.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }
        finally { release.Set(); }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var entries in results)
            entries.Should().ContainSingle().Which.Attributes!["person"].Should().Contain("maria");
        exclusive.Should().Be(1, "the queued stale request must recheck shared state and skip exclusive preparation");
    }

    [Fact]
    public void ExclusiveProjectionRefreshesPeerBytesAfterTheSharedRecheck()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        using var peer = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, false);
        bool changed = false;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase != "before-exclusive" || changed) return;
            changed = true;
            var current = new ArtifactEntry("note", "", 4, 1, DateTimeOffset.UtcNow)
            {
                Keywords = [], Attributes = new Dictionary<string, string[]>(), ContentPreview = "cached before peer"
            };
            store.Upsert(current);
            peer.Upsert(current with { ContentPreview = "fresh peer bytes" });
        };

        store.LoadIndex().Should().ContainSingle().Which.ContentPreview.Should().Be("fresh peer bytes");
        changed.Should().BeTrue("the peer update must occur in the released-shared to exclusive handoff");
        File.ReadAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "note.meta.json"))
            .Should().Contain("fresh peer bytes");
    }

    [Fact]
    public async Task CanceledProjectionGateWaitReleasesItsLifecycleLease()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, false);
        using var gateOwned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var queued = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase == "requested") Interlocked.Increment(ref requests);
            if (phase == "gate-wait" && Volatile.Read(ref requests) >= 2) queued.Set();
            if (phase == "gate-acquired" && Volatile.Read(ref requests) == 1)
            {
                gateOwned.Set();
                release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
        };
        Task first = Task.Run(() => store.LoadIndex());
        Task? second = null;
        try
        {
            gateOwned.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            second = Task.Run(async () => await store.ReadAsync(MemoryAddress.Parse("/note"), cancellation.Token));
            queued.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            cancellation.Cancel();
            Func<Task> waiting = async () => await second.WaitAsync(TimeSpan.FromSeconds(2));
            await waiting.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { cancellation.Cancel(); release.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        if (second is not null)
        {
            try { await second; }
            catch (OperationCanceledException) { }
        }
        store.IndexProjectionObserverForTest = null;
        store.LoadIndex().Should().ContainSingle();
    }

    [Fact]
    public async Task QueuedGateAndFileWaitsShareOneAcquisitionBudget()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, false);
        using var gateOwned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var queued = new ManualResetEventSlim();
        int requests = 0;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase == "requested") Interlocked.Increment(ref requests);
            if (phase == "gate-wait" && Volatile.Read(ref requests) >= 2) queued.Set();
            if (phase == "gate-acquired" && Volatile.Read(ref requests) == 1)
            {
                gateOwned.Set();
                release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
        };
        Task<Exception> first = Task.Run(() => Record.Exception(() => store.LoadIndex()));
        Task<Exception>? second = null;
        FileLock? external = null;
        try
        {
            gateOwned.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            second = Task.Run(() => Record.Exception(() => store.LoadIndex()));
            queued.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            external = FileLock.AcquireExclusive(Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", ".lock"));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(3));
            release.Set();
            Exception[] failures = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(4));
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7),
                "queueing and file acquisition spend one existing five-second budget");
            foreach (Exception? failure in failures) failure.Should().BeOfType<FileLockTimeoutException>();
        }
        finally { release.Set(); external?.Dispose(); }
        store.IndexProjectionObserverForTest = null;
        store.LoadIndex().Should().ContainSingle();
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentColdProjectionPublishesOnceAndRechecksQueuedRequests(bool legacyLayout)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, legacyLayout);
        int exclusive = 0, writes = 0, completed = 0, rechecked = 0, requested = 0;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase == "exclusive-start") Interlocked.Increment(ref exclusive);
            if (phase == "recheck-current") Interlocked.Increment(ref rechecked);
            if (phase == "requested") Interlocked.Increment(ref requested);
        };
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeWrite && Path.GetFileName(path) == "note.meta.json")
                Interlocked.Increment(ref writes);
            return null;
        };
        try
        {
            Parallel.For(0, 64, _ =>
            {
                store.LoadIndex().Should().ContainSingle().Which.Attributes!["person"].Should().Contain("maria");
                Interlocked.Increment(ref completed);
            });
        }
        finally { DurableFile.FailureInjector = null; }
        completed.Should().Be(64);
        exclusive.Should().Be(1);
        writes.Should().Be(1, "one authoritative forward-fill publication satisfies this cold cohort");
        // The coordinated two-request case separately requires the stale-request recheck;
        // a scheduler may let this cohort's other calls start after publication.
        requested.Should().Be(exclusive + rechecked,
            "each successful preparation either publishes once or completes the fresh shared recheck");
    }

    [Theory]
    [InlineData("gate-acquired")]
    [InlineData("exclusive-start")]
    public void ObserverFailureReleasesEveryAcquiredProjectionResource(string at)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, false);
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase == at) throw new UnauthorizedAccessException("owned observer failure");
        };
        Action read = () => store.LoadIndex();
        read.Should().Throw<UnauthorizedAccessException>().WithMessage("owned observer failure");
        store.IndexProjectionObserverForTest = null;
        store.LoadIndex().Should().ContainSingle().Which.Attributes!["person"].Should().Contain("maria");
    }

    [Fact]
    public void RetainedProofIntroducedAfterSharedRecheckBlocksExclusiveProjection()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        using var peer = new FileMemoryStore(scope.WorkspaceDir);
        Seed(scope.WorkspaceDir, false);
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories");
        string evidence = Path.Combine(directory, ".restore-journal.json");
        byte[]? proof = null, metadata = null;
        bool introduced = false;
        store.IndexProjectionObserverForTest = (_, phase) =>
        {
            if (phase != "before-exclusive" || introduced) return;
            introduced = true;
            FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
                transition == RestoreJournalTransition.BeforeCleanupDelete ? new IOException("retained proof") : null;
            var legacy = new ArtifactEntry("note", "", 4, 1, DateTimeOffset.UtcNow)
            {
                Keywords = [], FileHashes = new Dictionary<string, string> { ["src/peer.cs"] = "hash" }
            };
            peer.ImportTopicEntries("local", [legacy],
                new Dictionary<string, byte[]> { ["note"] = File.ReadAllBytes(Path.Combine(directory, "note.nmp2")) }, true);
            proof = File.ReadAllBytes(evidence);
            metadata = File.ReadAllBytes(Path.Combine(directory, "note.meta.json"));
        };
        try
        {
            Action read = () => store.LoadIndex();
            var failure = read.Should().Throw<RestoreRecoveryException>().Which;
            failure.DiscoveryBlock!.EvidencePath.Should().Be(evidence);
            introduced.Should().BeTrue();
            File.ReadAllBytes(evidence).Should().Equal(proof!);
            File.ReadAllBytes(Path.Combine(directory, "note.meta.json")).Should().Equal(metadata!);
        }
        finally { FileMemoryStore.RestoreJournalFailureInjector.Value = null; }
        store.IndexProjectionObserverForTest = null;
        store.LoadIndex().Should().ContainSingle().Which.Attributes!["file"].Should().Contain("src/peer.cs");
        File.Exists(evidence).Should().BeFalse();
    }

    private static void Seed(string workspace, bool legacyLayout)
    {
        string directory = Path.Combine(workspace, ".scrinia", legacyLayout ? "store" : "memories");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.nmp2"), Nmp2ChunkedEncoder.Encode("body"));
        File.WriteAllText(Path.Combine(directory, "note.meta.json"), """
            {"name":"note","uri":"file:///note.nmp2","originalBytes":4,"chunkCount":1,"createdAt":"2026-01-01T00:00:00+00:00","keywords":["person:maria"]}
            """);
    }
}
