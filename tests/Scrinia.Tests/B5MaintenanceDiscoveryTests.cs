using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class B5MaintenanceDiscoveryTests
{
    [Fact]
    public void Opened_evidence_is_rejected_when_the_handle_identity_is_outside_the_checked_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string evidence = Path.Combine(root, "evidence.recovery");
        File.WriteAllText(evidence, "evidence");
        try
        {
            RecoveryHandleIdentity identity = new(evidence, Path.Combine(root, "other.recovery"), true);

            RecoveryHandleIdentityVerifier.IsSafe(identity, root).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discovery_stops_directory_enumeration_after_the_admission_ceiling()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-enumeration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var counter = new EnumerationCounter();
        try
        {
            var coordinator = new RecoveryDiscoveryCoordinator(
                _ => EnumerateDirectoriesWithCounter(root, counter),
                _ => Array.Empty<string>());

            coordinator.Discover("local", root);

            counter.Count.Should().BeLessThanOrEqualTo(MaintenanceLimits.UnsafeScanLimit + 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Page_service_returns_deterministic_bounded_pages_and_a_bound_continuation()
    {
        var candidates = Enumerable.Range(0, MaintenanceLimits.CandidatePageSize + 2)
            .Select(index => new MaintenanceCandidate(
                "local", $"/memory/{index:D3}", MaintenanceCandidateKind.ArchivePair,
                $"memory/{index:D3}.nmp2", null, null, null, null, $"memory/{index:D3}.nmp2"))
            .ToArray();
        using var registry = new MaintenanceRunRegistry();
        var service = new MaintenanceDiscoveryPageService(_ => candidates, registry);

        MaintenanceDiscoveryPage first = service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        first.Returned.Should().BeLessThanOrEqualTo(MaintenanceLimits.ReturnedProjectionLimit);
        first.Scanned.Should().Be(first.Projected);
        first.MetadataReads.Should().Be(first.Scanned);
        first.Lookahead.Should().Be(MaintenanceLimits.LookaheadSize);
        first.Projected.Should().Be(first.Returned + first.Lookahead);
        first.Continuation.Should().NotBeNull();
        first.Continuation!.Value.PageSequence.Should().Be(1);

        MaintenanceDiscoveryPage second = service.ReadNextPage(first.Continuation.Value, CancellationToken.None);

        second.Projections.Select(item => item.SafeRelativeBodyPath)
            .Should().NotIntersectWith(first.Projections.Select(item => item.SafeRelativeBodyPath));
        second.Scanned.Should().BeLessThanOrEqualTo(MaintenanceLimits.CandidatePageSize);
        second.Scanned.Should().Be(second.Projected);
        second.MetadataReads.Should().Be(second.Scanned);

        MaintenanceDiscoveryPage replay = service.ReadNextPage(first.Continuation.Value, CancellationToken.None);
        replay.Projections.Should().BeEmpty();
        replay.Continuation.Should().BeNull();
    }

    [Fact]
    public void Page_service_fails_closed_when_the_snapshot_source_is_not_totally_ordered()
    {
        MaintenanceCandidate first = CreateCandidate("memory/b");
        MaintenanceCandidate second = CreateCandidate("memory/a");
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => [first, second], registry);

        MaintenanceDiscoveryPage page = service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        page.Projections.Should().BeEmpty();
        page.Continuation.Should().BeNull();
    }

    [Fact]
    public void Page_service_observes_cancellation_before_consuming_the_source()
    {
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(
            _ => throw new InvalidOperationException("source must not be opened"), registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action read = () => service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", cancellation.Token);

        read.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Page_service_consumes_only_the_first_page_before_returning()
    {
        int consumed = 0;
        IEnumerable<MaintenanceCandidate> Source(CancellationToken _)
        {
            for (int index = 0; index < 10_000; index++)
            {
                consumed++;
                yield return CreateCandidate($"memory/{index:D5}");
            }
        }

        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(Source, registry);

        MaintenanceDiscoveryPage page = service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        page.HasMore.Should().BeTrue();
        consumed.Should().BeLessThanOrEqualTo(MaintenanceLimits.ReturnedProjectionLimit + MaintenanceLimits.LookaheadSize);
        page.Scanned.Should().Be(page.Projected);
        page.MetadataReads.Should().Be(page.Scanned);
    }

    [Fact]
    public void Page_service_disposes_and_invalidates_a_completed_run()
    {
        var source = new DisposableCandidateEnumerable([CreateCandidate("memory/item")]);
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => source, registry);
        var lifecycle = new MaintenanceDiscoveryLifecycle(service, registry);

        MaintenanceLifecyclePage page = lifecycle.Start(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        page.ContinuationToken.Should().BeNull();
        source.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Page_service_disposes_and_invalidates_a_run_when_the_source_is_cancelled()
    {
        var source = new CancellingCandidateEnumerable(CreateCandidate("memory/item"));
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => source, registry);

        Action read = () => service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        read.Should().Throw<OperationCanceledException>();
        source.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Page_service_does_not_dispose_an_enumerator_until_an_active_first_page_finishes()
    {
        var source = new BlockingCandidateEnumerable(CreateCandidate("memory/item"));
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => source, registry);

        Task<MaintenanceDiscoveryPage> read = Task.Run(() => service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None));
        source.ReadStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task dispose = Task.Run(service.Dispose);
        await Task.Delay(50);
        source.Disposed.Should().BeFalse();

        source.AllowReadToFinish.Set();
        await read;
        await dispose;
        source.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Page_service_removes_registry_bindings_when_disposed()
    {
        var candidates = Enumerable.Range(0, MaintenanceLimits.ReturnedProjectionLimit + 1)
            .Select(index => CreateCandidate($"memory/{index:D3}")).ToArray();
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => candidates, registry);
        var lifecycle = new MaintenanceDiscoveryLifecycle(service, registry);

        MaintenanceLifecyclePage page = lifecycle.Start(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);
        page.ContinuationToken.Should().NotBeNullOrEmpty();

        service.Dispose();

        MaintenanceCursorCodec.TryDecode(page.ContinuationToken!, registry,
            out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Page_service_reclaims_the_session_when_its_continuation_was_evicted()
    {
        var source = new DisposableCandidateEnumerable(
            Enumerable.Range(0, MaintenanceLimits.ReturnedProjectionLimit + 1)
                .Select(index => CreateCandidate($"memory/{index:D3}")).ToArray());
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => source, registry);
        var lifecycle = new MaintenanceDiscoveryLifecycle(service, registry);

        MaintenanceLifecyclePage page = lifecycle.Start(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);
        MaintenanceCursorCodec.TryDecode(page.ContinuationToken!, registry, out MaintenanceCursor cursor, out _)
            .Should().BeTrue();
        registry.Remove(cursor.Run).Should().BeTrue();

        MaintenanceDiscoveryPage invalid = service.ReadNextPage(cursor, CancellationToken.None);

        invalid.Projections.Should().BeEmpty();
        source.Disposed.Should().BeTrue();
        service.ReadNextPage(cursor, CancellationToken.None).Projections.Should().BeEmpty();
    }

    [Fact]
    public async Task Registry_operations_are_safe_when_dispose_races_with_create_get_and_remove()
    {
        using var registry = new MaintenanceRunRegistry();
        var workspace = new MaintenanceWorkspaceKey(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var scopes = new MaintenanceScopeBinding(Guid.NewGuid());
        var policy = new MaintenanceHash256(new byte[32]);
        var failures = new ConcurrentBag<Exception>();
        var runs = Enumerable.Range(0, 128).Select(_ => new MaintenanceRunId(Guid.NewGuid())).ToArray();

        Task[] workers = runs.Select(run => Task.Run(() =>
        {
            try
            {
                registry.Create(workspace, run, snapshot, scopes, policy, DateTimeOffset.UtcNow.AddMinutes(5));
                registry.TryAcquire(run, out MaintenanceRunLease? lease);
                lease?.Dispose();
                registry.Remove(run);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        })).ToArray();
        Task disposer = Task.Run(() => registry.Dispose());

        await Task.WhenAll(workers.Append(disposer));

        failures.Should().BeEmpty();
        registry.Dispose();
        Action createAfterDispose = () => registry.Create(workspace, new MaintenanceRunId(Guid.NewGuid()),
            snapshot, scopes, policy, DateTimeOffset.UtcNow.AddMinutes(5));
        createAfterDispose.Should().Throw<ObjectDisposedException>();
        registry.TryAcquire(runs[0], out MaintenanceRunLease? leaseAfterDispose).Should().BeFalse();
        leaseAfterDispose.Should().BeNull();
        registry.Remove(runs[0]).Should().BeFalse();
    }

    [Fact]
    public void Page_service_reports_only_metrics_owned_by_paging()
    {
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(
            _ => [new MaintenanceCandidate("local", "/memory/item", MaintenanceCandidateKind.CompletedRecoveryEvidence,
                "evidence.recovery", null, null, "operation", null, "evidence.recovery")], registry);

        MaintenanceDiscoveryPage page = service.ReadFirstPage(
            new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None);

        page.UnsafeJournalScanned.Should().Be(0);
        page.UnsafeNonJournalScanned.Should().Be(0);
        page.JournalBudgetConsumed.Should().Be(0);
        page.JournalInspected.Should().Be(0);
        page.JournalDeleted.Should().Be(0);
        page.NonJournalInspected.Should().Be(0);
        page.JournalDeferred.Should().Be(0);
    }

    [Fact]
    public void Production_lifecycle_consumer_round_trips_a_continuation_token()
    {
        var candidates = Enumerable.Range(0, MaintenanceLimits.ReturnedProjectionLimit + 1)
            .Select(index => CreateCandidate($"memory/{index:D3}")).ToArray();
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => candidates, registry);
        var lifecycle = new MaintenanceDiscoveryLifecycle(service, registry);
        MaintenanceWorkspaceKey workspace = new(Guid.NewGuid());

        MaintenanceLifecyclePage first = lifecycle.Start(workspace, "local", CancellationToken.None);

        first.ContinuationToken.Should().NotBeNullOrEmpty();
        MaintenanceLifecyclePage second = lifecycle.Continue(first.ContinuationToken!, CancellationToken.None);
        second.Page.Projections.Should().NotBeEmpty();
        second.Page.Projections.Select(item => item.SafeRelativeBodyPath)
            .Should().NotIntersectWith(first.Page.Projections.Select(item => item.SafeRelativeBodyPath));
        second.ContinuationToken.Should().BeNull();
        lifecycle.Continue(first.ContinuationToken!, CancellationToken.None).Page.Projections.Should().BeEmpty();
    }

    [Fact]
    public void Production_store_does_not_reuse_a_page_source_for_another_scope()
    {
        string storeDirectory = Path.Combine(Path.GetTempPath(), "scrinia-b5-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        File.WriteAllText(Path.Combine(storeDirectory, ".restore-journal.json"), "not-json");
        try
        {
            using var store = new FileMemoryStore(Path.GetTempPath());
            MaintenanceDiscoveryPage first = store.DiscoverRecoveryPage("first", storeDirectory,
                new MaintenanceWorkspaceKey(Guid.NewGuid()));
            MaintenanceDiscoveryPage second = store.DiscoverRecoveryPage("second", storeDirectory,
                new MaintenanceWorkspaceKey(Guid.NewGuid()));

            first.Projections.Should().ContainSingle(item => item.Scope == "first");
            second.Projections.Should().ContainSingle(item => item.Scope == "second");
        }
        finally
        {
            Directory.Delete(storeDirectory, recursive: true);
        }
    }

    [Fact]
    public void Production_page_service_does_not_mark_prepared_recovery_evidence_eligible()
    {
        string storeDirectory = Path.Combine(Path.GetTempPath(), "scrinia-b5-prepared-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(storeDirectory, "versions"));
        File.WriteAllBytes(Path.Combine(storeDirectory, "versions", ".append-journal.json"),
            RecoveryTestFixtures.BuildLegacyAppendJournal(state: "prepared"));
        try
        {
            using var store = new FileMemoryStore(Path.GetTempPath());

            MaintenanceDiscoveryPage page = store.DiscoverRecoveryPage(
                "local:notes", storeDirectory, new MaintenanceWorkspaceKey(Guid.NewGuid()));

            page.Projections.Should().ContainSingle();
            page.Projections[0].Eligible.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(storeDirectory)) Directory.Delete(storeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_append_observes_cancellation_while_waiting_for_the_scope_lock()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-lock-cancel-" + Guid.NewGuid().ToString("N"));
        using var store = new FileMemoryStore(root);
        string scopeDirectory = store.GetStoreDirForScope("local");
        string lockPath = Path.Combine(scopeDirectory, ".lock");
        using FileLock held = FileLock.AcquireExclusive(lockPath);
        using var cancellation = new CancellationTokenSource();

        Task recovery = Task.Run(() => store.RecoverAppendAsync("local", cancellation.Token));
        await Task.Delay(100);
        cancellation.Cancel();

        Func<Task> observe = async () => await recovery;
        await observe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Scope_admission_releases_the_in_process_gate_after_a_cross_thread_continuation()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-admission-gate-" + Guid.NewGuid().ToString("N"));
        using var store = new FileMemoryStore(root);
        int callbackThread = Environment.CurrentManagedThreadId;
        int continuationThread = callbackThread;

        int result = await store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5), async () =>
        {
            await new DedicatedThreadYieldAwaitable();
            continuationThread = Environment.CurrentManagedThreadId;
            return 42;
        }, CancellationToken.None);

        result.Should().Be(42);
        continuationThread.Should().NotBe(callbackThread);

        int secondResult = await store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5),
            () => Task.FromResult(7), CancellationToken.None);
        secondResult.Should().Be(7);
    }

    [Fact]
    public async Task Scope_admission_cancellation_and_disposal_release_waiters_without_deadlock()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-admission-dispose-" + Guid.NewGuid().ToString("N"));
        using var store = new FileMemoryStore(root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();

        Task first = store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5), async () =>
        {
            entered.Set();
            await Task.Run(() => release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }, CancellationToken.None);
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Func<Task> second = () => store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5),
            () => Task.FromResult(true), cancellation.Token);
        cancellation.Cancel();
        await second.Should().ThrowAsync<OperationCanceledException>();

        Task dispose = Task.Run(store.Dispose);
        await dispose;
        release.Set();
        await first;
    }

    [Fact]
    public async Task Scope_admission_gate_is_cleaned_after_disposal_and_active_work_finish()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-admission-cleanup-" + Guid.NewGuid().ToString("N"));
        using var store = new FileMemoryStore(root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task first = store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5), async () =>
        {
            entered.Set();
            await Task.Run(() => release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }, CancellationToken.None);
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        store.Dispose();
        AdmissionGateCount(store).Should().Be(1);

        release.Set();
        await first;

        AdmissionGateCount(store).Should().Be(0);
    }

    [Fact]
    public async Task Completed_unique_scope_admissions_do_not_accumulate_gates()
    {
        using var store = new FileMemoryStore(Path.Combine(Path.GetTempPath(),
            "scrinia-b5-admission-bounded-" + Guid.NewGuid().ToString("N")));

        for (int index = 0; index < 128; index++)
        {
            await store.WithExclusiveScopeAdmissionAsync($"local:topic-{index}", TimeSpan.FromSeconds(5),
                () => Task.FromResult(true), CancellationToken.None);
        }

        AdmissionGateCount(store).Should().Be(0);
    }

    [Fact]
    public async Task Scope_admission_rejects_late_creation_after_store_disposal()
    {
        using var store = new FileMemoryStore(Path.Combine(Path.GetTempPath(),
            "scrinia-b5-admission-late-" + Guid.NewGuid().ToString("N")));
        store.Dispose();

        Func<Task> admit = () => store.WithExclusiveScopeAdmissionAsync("local", TimeSpan.FromSeconds(5),
            () => Task.FromResult(true), CancellationToken.None);

        await admit.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Continuation_encoding_returns_invalid_page_when_registry_is_disposed_during_encoding()
    {
        var candidates = Enumerable.Range(0, MaintenanceLimits.ReturnedProjectionLimit + 1)
            .Select(index => CreateCandidate($"memory/{index:D3}")).ToArray();
        using var registry = new MaintenanceRunRegistry();
        using var service = new MaintenanceDiscoveryPageService(_ => candidates, registry);
        var lifecycle = new MaintenanceDiscoveryLifecycle(service, registry);
        using var encodingEntered = new ManualResetEventSlim();
        using var releaseEncoding = new ManualResetEventSlim();
        MaintenanceDiscoveryLifecycle.ContinuationEncodingBarrierForTest = () =>
        {
            encodingEntered.Set();
            releaseEncoding.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        };
        try
        {
            Task<MaintenanceLifecyclePage> start = Task.Run(() => lifecycle.Start(
                new MaintenanceWorkspaceKey(Guid.NewGuid()), "local", CancellationToken.None));
            encodingEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            registry.Dispose();
            releaseEncoding.Set();

            MaintenanceLifecyclePage result = await start;
            result.Page.Projections.Should().BeEmpty();
            result.ContinuationToken.Should().BeNull();
        }
        finally
        {
            MaintenanceDiscoveryLifecycle.ContinuationEncodingBarrierForTest = null;
        }
    }

    [Fact]
    public void Maintenance_evidence_paths_are_ordered_independently_of_filesystem_enumeration()
    {
        string[] paths = [".file-entity-z.recovery", ".file-entity-a.recovery", ".file-entity-m.recovery"];

        RecoveryDiscoveryCoordinator.OrderMaintenanceEvidencePaths(paths.Reverse())
            .Should().Equal(paths.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void B2_projection_uses_shared_verification_accounting_for_terminal_evidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        File.WriteAllBytes(Path.Combine(root, "versions", ".append-journal.json"),
            RecoveryTestFixtures.BuildLegacyAppendJournal(state: "committed"));
        try
        {
            var discovery = new RecoveryDiscoveryCoordinator().Discover("local:notes", root, includeCoordinator: false);
            B2RecoveryMaintenanceProjection projection = new B2RecoveryMaintenanceAdapter(root)
                .Project(discovery, "local:notes").Single();

            projection.IsTerminalAndVerified.Should().BeTrue();
            projection.VerificationOutcome.Should().Be(RecoveryVerificationOutcome.Verified);
            projection.VerificationBytesRead.Should().BeGreaterThan(0);
            projection.VerificationFinalZeroByteRead.Should().BeTrue();
            new B2RecoveryMaintenanceAdapter(root).EvaluateCleanup(projection, new HashSet<string>())
                .Eligible.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Store_rejects_late_page_service_creation_after_disposal()
    {
        string storeDirectory = Path.Combine(Path.GetTempPath(), "scrinia-b5-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        try
        {
            using var store = new FileMemoryStore(Path.GetTempPath());
            using var creationEntered = new ManualResetEventSlim();
            using var releaseCreation = new ManualResetEventSlim();
            store.MaintenancePageServiceCreationBarrierForTest = () =>
            {
                creationEntered.Set();
                releaseCreation.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };

            Task creation = Task.Run(() =>
            {
                Action discover = () => store.DiscoverMaintenancePage(
                    "local", storeDirectory, new MaintenanceWorkspaceKey(Guid.NewGuid()));
                discover.Should().Throw<ObjectDisposedException>();
            });
            creationEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            await Task.Run(store.Dispose);
            releaseCreation.Set();
            await creation;
            store.MaintenancePageServiceCreationBarrierForTest = null;

            MaintenancePageServices(store).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(storeDirectory, recursive: true);
        }
    }

    private static MaintenanceCandidate CreateCandidate(string relativePath) => new(
        "local", "/memory/item", MaintenanceCandidateKind.ArchivePair, relativePath,
        null, null, null, null, relativePath);

    private static IReadOnlyCollection<MaintenanceDiscoveryPageService> MaintenancePageServices(FileMemoryStore store)
    {
        FieldInfo field = typeof(FileMemoryStore).GetField("_maintenancePages",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.IDictionary)field.GetValue(store)!).Values
            .Cast<MaintenanceDiscoveryPageService>()
            .ToArray();
    }

    private static int AdmissionGateCount(FileMemoryStore store)
    {
        FieldInfo field = typeof(FileMemoryStore).GetField("_admissionLocks",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.IDictionary)field.GetValue(store)!).Count;
    }

    private static IEnumerable<string> EnumerateDirectoriesWithCounter(string root, EnumerationCounter counter)
    {
        while (counter.Count < MaintenanceLimits.UnsafeScanLimit + 100)
        {
            counter.Count++;
            yield return Path.Combine(root, $"unrelated-{counter.Count:D3}");
        }
    }

    private sealed class EnumerationCounter
    {
        internal int Count { get; set; }
    }

    private sealed class DisposableCandidateEnumerable(IReadOnlyList<MaintenanceCandidate> candidates) :
        IEnumerable<MaintenanceCandidate>
    {
        internal bool Disposed { get; private set; }

        public IEnumerator<MaintenanceCandidate> GetEnumerator() => new Enumerator(this, candidates);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(DisposableCandidateEnumerable owner, IReadOnlyList<MaintenanceCandidate> candidates) :
            IEnumerator<MaintenanceCandidate>
        {
            private int _index = -1;
            public MaintenanceCandidate Current => candidates[_index];
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext() => ++_index < candidates.Count;
            public void Reset() => _index = -1;
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class CancellingCandidateEnumerable(MaintenanceCandidate candidate) :
        IEnumerable<MaintenanceCandidate>
    {
        internal bool Disposed { get; private set; }

        public IEnumerator<MaintenanceCandidate> GetEnumerator() => new Enumerator(this, candidate);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(CancellingCandidateEnumerable owner, MaintenanceCandidate candidate) :
            IEnumerator<MaintenanceCandidate>
        {
            private int _index = -1;
            public MaintenanceCandidate Current => candidate;
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (++_index == 0) return true;
                throw new OperationCanceledException();
            }
            public void Reset() => _index = -1;
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class BlockingCandidateEnumerable(MaintenanceCandidate candidate) :
        IEnumerable<MaintenanceCandidate>
    {
        internal ManualResetEventSlim ReadStarted { get; } = new();
        internal ManualResetEventSlim AllowReadToFinish { get; } = new();
        internal bool Disposed { get; private set; }

        public IEnumerator<MaintenanceCandidate> GetEnumerator() => new Enumerator(this, candidate);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(BlockingCandidateEnumerable owner, MaintenanceCandidate candidate) :
            IEnumerator<MaintenanceCandidate>
        {
            private int _index = -1;
            public MaintenanceCandidate Current => candidate;
            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (++_index == 0) return true;
                owner.ReadStarted.Set();
                owner.AllowReadToFinish.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                return false;
            }

            public void Reset() => _index = -1;
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class DedicatedThreadYieldAwaitable
    {
        public DedicatedThreadYieldAwaiter GetAwaiter() => new();
    }

    private sealed class DedicatedThreadYieldAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        public bool IsCompleted => false;

        public void OnCompleted(Action continuation) =>
            Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        public void GetResult() { }
    }
}
