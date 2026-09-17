using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

// B5 Task 6 — Core service execution (TDD: failing first).
// Covers execution, deletion safety, revalidation, accounting,
// and cancellation/deadline against FileMemoryMaintenanceService
// plus the single FileMemoryStore.ExecuteMaintenanceScopeAsync seam.
public sealed class FileMemoryMaintenanceTests
{
    private sealed class FakeExecutor : IMaintenanceScopeExecutor
    {
        private readonly Func<string, MaintenancePolicy, int, DateTimeOffset, CancellationToken, Task<MaintenanceScopeExecution>> _handler;

        internal FakeExecutor(Func<string, MaintenancePolicy, int, DateTimeOffset, CancellationToken, Task<MaintenanceScopeExecution>> handler) =>
            _handler = handler;

        public List<(string Scope, int RemainingBudget)> Calls { get; } = [];

        public Task<MaintenanceScopeExecution> ExecuteScopeAsync(
            string scope, MaintenancePolicy policy, int remainingJournalBudget,
            DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
        {
            Calls.Add((scope, remainingJournalBudget));
            return _handler(scope, policy, remainingJournalBudget, deadlineUtc, cancellationToken);
        }
    }

    private static MaintenanceScopeExecution ScopeExecution(
        string scope,
        int inspected = 0,
        int deletedArchives = 0,
        int deletedRecoveryEvidence = 0,
        int retained = 0,
        int journalConsumed = 0,
        bool lockContended = false,
        IReadOnlyList<string>? warnings = null) =>
        new(
            new MaintenanceScopeResult(
                scope, inspected, deletedArchives, deletedRecoveryEvidence, retained,
                lockContended, false, null,
                Array.Empty<MaintenanceCandidateResult>(),
                warnings ?? Array.Empty<string>()),
            journalConsumed);

    private static (FileMemoryStore Store, IWorkspaceConfig Config) RealStore()
    {
        FileMemoryStore store = (FileMemoryStore)TestHelpers.Store;
        IWorkspaceConfig config = new WorkspaceConfig();
        return (store, config);
    }

    // Execution: invalid policy fails closed with zero enumeration/deletion.
    [Fact]
    public async Task ExecuteAsync_InvalidPolicy_ReturnsPolicyInvalidWithZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, _) = RealStore();
        var config = new WorkspaceConfig(new Dictionary<string, string>
        {
            ["Scrinia:Maintenance:JournalCleanupBatchSize"] = "0",
        });
        var executor = new FakeExecutor((_, _, _, _, _) =>
            Task.FromResult(ScopeExecution("local", inspected: 5, deletedRecoveryEvidence: 5)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);

        result.PolicyInvalid.Should().BeTrue();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.PolicyError.Should().NotBeNullOrEmpty();
        executor.Calls.Should().BeEmpty();
    }

    // Execution: empty workspace completes with zero deletions and no invalidation.
    [Fact]
    public async Task ExecuteAsync_EmptyWorkspace_CompletesWithZeroDeletions()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        int invalidationsBefore = store.CommittedScopeInvalidationCount;
        var service = new FileMemoryMaintenanceService(store, config);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);

        result.PolicyInvalid.Should().BeFalse();
        result.Completed.Should().BeTrue();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.ContinuationToken.Should().BeNull();
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // Deletion safety: a supplied continuation token fails closed with zero work.
    [Fact]
    public async Task ExecuteAsync_SuppliedToken_FailsClosedWithZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((_, _, _, _, _) =>
            Task.FromResult(ScopeExecution("local", inspected: 3, deletedRecoveryEvidence: 3)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, "b5c2.invalid"), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.Warnings.Should().NotBeEmpty();
        executor.Calls.Should().BeEmpty();
    }

    // Revalidation/deletion safety: a failed deletion is retained with a sanitized warning.
    [Fact]
    public async Task ExecuteAsync_DeletionFailure_RetainsCandidateAndWarns()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName,
                inspected: 2, deletedRecoveryEvidence: 1, retained: 1,
                journalConsumed: 1,
                warnings: new[] { "Scope 'local' retained a candidate: deletion failed." })));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local"], null, null, null), CancellationToken.None);

        result.DeletedRecoveryEvidence.Should().Be(1);
        result.PartialFailure.Should().BeTrue();
        result.Warnings.Should().ContainSingle()
            .Which.Should().NotContain(".scrinia")
            .And.NotContain("Exception");
        result.Completed.Should().BeTrue();
    }

    // Races: lock contention on one scope does not block an independent scope.
    [Fact]
    public async Task ExecuteAsync_LockContention_ContinuesIndependentScopes()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            scopeName == "local:a"
                ? throw new FileLockTimeoutException("lock", TimeSpan.FromSeconds(1))
                : Task.FromResult(ScopeExecution(scopeName, inspected: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.LockContended.Should().BeTrue();
        result.Inspected.Should().Be(1);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a", "local:b");
    }

    // Accounting: one invocation-wide journal budget, allocated greedily in scope order.
    [Fact]
    public async Task ExecuteAsync_JournalBudgetAllocatedGreedilyWithoutReset()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, remaining, _, _) =>
            Task.FromResult(ScopeExecution(scopeName, inspected: 2, journalConsumed: 2)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], 5, null, null), CancellationToken.None);

        result.Inspected.Should().Be(4);
        executor.Calls.Select(call => call.RemainingBudget).Should().Equal(5, 3);
    }

    // Ordering: scopes are resolved and processed in ordinal order.
    [Fact]
    public async Task ExecuteAsync_ResolvesScopesInOrdinalOrder()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        await service.ExecuteAsync(
            new MaintenanceRequest(["local:b", "local:a"], null, null, null), CancellationToken.None);

        executor.Calls.Select(call => call.Scope).Should().Equal("local:a", "local:b");
    }

    // Cancellation: a cancelled token performs no work and throws without an advanced cursor.
    [Fact]
    public async Task ExecuteAsync_CancelledToken_PerformsNoWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName, inspected: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        executor.Calls.Should().BeEmpty();
    }

    // Deadline: a timed-out scope stops the batch with no advanced cursor.
    [Fact]
    public async Task ExecuteAsync_ExpiredDeadline_StopsBatchWithNoContinuation()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            scopeName == "local:a"
                ? throw new TimeoutException("The maintenance deadline has expired.")
                : Task.FromResult(ScopeExecution(scopeName, inspected: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.ContinuationToken.Should().BeNull();
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a");
    }

    // Accounting: aggregates are exact sums and deletions never exceed inspections.
    [Fact]
    public async Task ExecuteAsync_AccountingIsExactAcrossScopes()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(scopeName == "local:a"
                ? ScopeExecution(scopeName, inspected: 3, deletedArchives: 1, retained: 2, journalConsumed: 1)
                : ScopeExecution(scopeName, inspected: 2, deletedRecoveryEvidence: 1, retained: 1, journalConsumed: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.Inspected.Should().Be(5);
        result.DeletedArchives.Should().Be(1);
        result.DeletedRecoveryEvidence.Should().Be(1);
        (result.DeletedArchives + result.DeletedRecoveryEvidence).Should().BeLessThanOrEqualTo(result.Inspected);
        result.Inspected.Should().BeLessThanOrEqualTo(2 * MaintenanceLimits.CandidatePageSize);
        result.Scopes.Should().HaveCount(2);
    }

    // Store seam: an empty scope performs no deletion and no derived-state invalidation.
    [Fact]
    public async Task ExecuteMaintenanceScopeAsync_EmptyScope_ReturnsZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        MaintenancePolicy policy = MaintenancePolicy.Load(config).Policy!;
        int invalidationsBefore = store.CommittedScopeInvalidationCount;

        MaintenanceScopeExecution execution = await store.ExecuteMaintenanceScopeAsync(
            "local", policy, 50, DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None);

        execution.ScopeResult.Inspected.Should().Be(0);
        execution.ScopeResult.DeletedArchives.Should().Be(0);
        execution.ScopeResult.DeletedRecoveryEvidence.Should().Be(0);
        execution.ScopeResult.LockContended.Should().BeFalse();
        execution.JournalConsumed.Should().Be(0);
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — every invalid policy key fails closed with zero enumeration/deletion.
    [Theory]
    [InlineData("Scrinia:Maintenance:JournalCleanupBatchSize", "0")]
    [InlineData("Scrinia:Maintenance:JournalCleanupBatchSize", "51")]
    [InlineData("Scrinia:Maintenance:JournalCleanupBatchSize", "abc")]
    [InlineData("Scrinia:Maintenance:TimeBudgetSeconds", "0")]
    [InlineData("Scrinia:Retention:MaxArchivedVersions", "0")]
    [InlineData("Scrinia:Maintenance:BackgroundEnabled", "maybe")]
    public async Task ExecuteAsync_EveryInvalidPolicyKey_FailsClosedWithZeroWork(string key, string value)
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, _) = RealStore();
        var config = new WorkspaceConfig(new Dictionary<string, string> { [key] = value });
        var executor = new FakeExecutor((_, _, _, _, _) =>
            Task.FromResult(ScopeExecution("local", inspected: 5, deletedRecoveryEvidence: 5)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);

        result.PolicyInvalid.Should().BeTrue();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.PolicyError.Should().NotBeNullOrEmpty();
        executor.Calls.Should().BeEmpty();
    }

    // B5 Task 7 — a syntactically valid-looking token still fails closed: the
    // service owns no continuation yet, so any supplied token is rejected.
    [Fact]
    public async Task ExecuteAsync_ValidLookingToken_StillFailsClosedWithZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        int invalidationsBefore = store.CommittedScopeInvalidationCount;
        var executor = new FakeExecutor((_, _, _, _, _) =>
            Task.FromResult(ScopeExecution("local", inspected: 3, deletedRecoveryEvidence: 3)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, "b5c2." + new string('A', 214)),
            CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.ContinuationToken.Should().BeNull();
        result.Warnings.Should().ContainSingle();
        executor.Calls.Should().BeEmpty();
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — writer race isolation: every scope contended still reports
    // contention per scope with zero deletions and no advanced cursor.
    [Fact]
    public async Task ExecuteAsync_AllScopesContended_ReportsContentionWithZeroDeletions()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((_, _, _, _, _) =>
            throw new FileLockTimeoutException("lock", TimeSpan.FromSeconds(1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.LockContended.Should().BeTrue();
        result.Inspected.Should().Be(0);
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        result.Completed.Should().BeTrue();
        result.ContinuationToken.Should().BeNull();
        result.Scopes.Should().HaveCount(2);
        result.Scopes.Should().OnlyContain(s => s.LockContended);
        result.Scopes.SelectMany(s => s.Warnings).Should().HaveCount(2);
        result.Warnings.Should().BeEmpty();
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a", "local:b");
    }

    // B5 Task 7 — a lock-contended scope result (not just a throw) continues
    // the independent scope without marking a partial failure.
    [Fact]
    public async Task ExecuteAsync_LockContendedScopeResult_ContinuesIndependentScope()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(scopeName == "local:a"
                ? ScopeExecution(scopeName, lockContended: true)
                : ScopeExecution(scopeName, inspected: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.LockContended.Should().BeTrue();
        result.Inspected.Should().Be(1);
        result.PartialFailure.Should().BeFalse();
        result.Completed.Should().BeTrue();
        result.Scopes.Should().HaveCount(2);
    }

    // B5 Task 7 — partial-pair failure: the deleted candidate stays deleted,
    // the failed one is retained, and the batch still completes.
    [Fact]
    public async Task ExecuteAsync_PartialPairFailure_RetainsWithoutRollback()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(scopeName == "local:a"
                ? ScopeExecution(scopeName,
                    inspected: 2, deletedRecoveryEvidence: 1, retained: 1, journalConsumed: 1,
                    warnings: new[] { "Scope 'local:a' retained a candidate: deletion failed." })
                : ScopeExecution(scopeName, inspected: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.DeletedRecoveryEvidence.Should().Be(1);
        result.Inspected.Should().Be(3);
        result.PartialFailure.Should().BeTrue();
        result.RecoveryRetained.Should().BeTrue();
        result.Completed.Should().BeTrue();
        result.Warnings.Should().ContainSingle()
            .Which.Should().NotContain("Exception")
            .And.NotContain(".scrinia");
    }

    // B5 Task 7 — permissions/IO failure: retained with a sanitized warning
    // that discloses no path, secret, or exception text.
    [Fact]
    public async Task ExecuteAsync_PermissionDenied_RetainedWithSanitizedWarning()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName,
                inspected: 1, retained: 1,
                warnings: new[] { "Scope 'local' retained a candidate: deletion failed." })));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local"], null, null, null), CancellationToken.None);

        result.DeletedRecoveryEvidence.Should().Be(0);
        result.PartialFailure.Should().BeTrue();
        result.RecoveryRetained.Should().BeTrue();
        result.Warnings.Should().ContainSingle()
            .Which.Should().NotContain("UnauthorizedAccessException")
            .And.NotContain(".scrinia")
            .And.NotContain("Exception");
        result.Completed.Should().BeTrue();
    }

    // B5 Task 7 — concurrent maintenance invocations stay isolated: every
    // invocation keeps ordinal scope order and its own budget accounting.
    [Fact]
    public async Task ExecuteAsync_ConcurrentInvocations_AreIsolated()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new ConcurrentFakeExecutor();
        var service = new FileMemoryMaintenanceService(store, config, executor);

        Task<MaintenanceResult>[] invocations = Enumerable.Range(0, 4)
            .Select(_ => service.ExecuteAsync(
                new MaintenanceRequest(["local:a", "local:b"], null, null, null),
                CancellationToken.None))
            .ToArray();
        MaintenanceResult[] results = await Task.WhenAll(invocations);

        results.Should().HaveCount(4);
        foreach (MaintenanceResult result in results)
        {
            result.Completed.Should().BeTrue();
            result.Inspected.Should().Be(2);
            result.ContinuationToken.Should().BeNull();
        }
        executor.Calls.Should().HaveCount(8);
    }

    // B5 Task 7 — restart safety and idempotency: a repeated invocation
    // reproduces the same totals with a fresh budget and no cursor.
    [Fact]
    public async Task ExecuteAsync_SequentialRuns_AreIdempotentAndConverge()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName, inspected: 2, journalConsumed: 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult first = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a"], null, null, null), CancellationToken.None);
        MaintenanceResult second = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a"], null, null, null), CancellationToken.None);

        first.Inspected.Should().Be(second.Inspected).And.Be(2);
        first.DeletedRecoveryEvidence.Should().Be(second.DeletedRecoveryEvidence).And.Be(0);
        first.Completed.Should().BeTrue();
        second.Completed.Should().BeTrue();
        first.ContinuationToken.Should().BeNull();
        second.ContinuationToken.Should().BeNull();
        executor.Calls.Should().HaveCount(2);
        executor.Calls.Select(call => call.RemainingBudget).Should().Equal(50, 50);
    }

    // B5 Task 7 — restart safety on the real store: repeated empty runs keep
    // derived state local with no invalidation growth.
    [Fact]
    public async Task ExecuteAsync_EmptyStoreRepeatedRuns_KeepDerivedStateLocal()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var service = new FileMemoryMaintenanceService(store, config);
        int invalidationsBefore = store.CommittedScopeInvalidationCount;

        MaintenanceResult first = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);
        MaintenanceResult second = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);

        first.Completed.Should().BeTrue();
        second.Completed.Should().BeTrue();
        first.Inspected.Should().Be(0);
        second.Inspected.Should().Be(0);
        first.ContinuationToken.Should().BeNull();
        second.ContinuationToken.Should().BeNull();
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — writer-race recovery: a contended invocation followed by a
    // retry converges without losing the independent scope.
    [Fact]
    public async Task ExecuteAsync_ContendedThenRetry_RecoversOnNextInvocation()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var contended = new FakeExecutor((_, _, _, _, _) =>
            throw new FileLockTimeoutException("lock", TimeSpan.FromSeconds(1)));
        var retried = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName, inspected: 1)));
        var contendedService = new FileMemoryMaintenanceService(store, config, contended);
        var retriedService = new FileMemoryMaintenanceService(store, config, retried);

        MaintenanceResult first = await contendedService.ExecuteAsync(
            new MaintenanceRequest(["local:a"], null, null, null), CancellationToken.None);
        MaintenanceResult second = await retriedService.ExecuteAsync(
            new MaintenanceRequest(["local:a"], null, null, null), CancellationToken.None);

        first.LockContended.Should().BeTrue();
        first.DeletedRecoveryEvidence.Should().Be(0);
        second.LockContended.Should().BeFalse();
        second.Inspected.Should().Be(1);
        second.Completed.Should().BeTrue();
    }

    // B5 Task 7 — cancellation mid-batch stops before the next scope: the
    // first scope is never revisited and no cursor advances.
    [Fact]
    public async Task ExecuteAsync_CancelledMidBatch_StopsBeforeNextScope()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        using var cancelled = new CancellationTokenSource();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
        {
            if (scopeName == "local:a")
                cancelled.Cancel();
            return Task.FromResult(ScopeExecution(scopeName, inspected: 1));
        });
        var service = new FileMemoryMaintenanceService(store, config, executor);

        Func<Task> act = () => service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a");
    }

    // B5 Task 7 — journal budget matrix: an exhausted budget stops the batch
    // with no advanced cursor and no call to the next scope.
    [Fact]
    public async Task ExecuteAsync_JournalBudgetExhaustion_StopsBatchWithoutContinuation()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName, inspected: 2, journalConsumed: 3)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], 3, null, null), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.ContinuationToken.Should().BeNull();
        result.Inspected.Should().Be(2);
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a");
        executor.Calls.Select(call => call.RemainingBudget).Should().Equal(3);
    }

    // B5 Task 7 — sequence behavior: one shared deadline is computed once per
    // invocation and passed unchanged to every scope.
    [Fact]
    public async Task ExecuteAsync_SharedDeadline_IsIdenticalAcrossScopes()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var deadlines = new List<DateTimeOffset>();
        var executor = new FakeExecutor((scopeName, _, _, deadlineUtc, _) =>
        {
            lock (deadlines)
                deadlines.Add(deadlineUtc);
            return Task.FromResult(ScopeExecution(scopeName));
        });
        var service = new FileMemoryMaintenanceService(store, config, executor);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        deadlines.Should().HaveCount(2);
        deadlines[0].Should().Be(deadlines[1]);
        deadlines[0].Should().BeOnOrAfter(before.AddSeconds(5));
        deadlines[0].Should().BeOnOrBefore(after.AddSeconds(5));
    }

    // B5 Task 7 — 128 shared-admission boundary: a full page stops the batch
    // with no advanced cursor.
    [Fact]
    public async Task ExecuteAsync_InspectionPageBound_StopsBatch()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(ScopeExecution(scopeName,
                inspected: scopeName == "local:a" ? MaintenanceLimits.CandidatePageSize : 1)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.ContinuationToken.Should().BeNull();
        result.Inspected.Should().Be(MaintenanceLimits.CandidatePageSize);
        executor.Calls.Select(call => call.Scope).Should().Equal("local:a");
    }

    // B5 Task 7 — HasMore/continuation: the service never emits a continuation
    // token or more-work flag today; every path returns a terminal result.
    [Fact]
    public async Task ExecuteAsync_NeverEmitsContinuationOrMoreWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        var executor = new FakeExecutor((scopeName, _, _, _, _) =>
            Task.FromResult(scopeName == "local:a"
                ? ScopeExecution(scopeName, inspected: 1)
                : ScopeExecution(scopeName, lockContended: true)));
        var service = new FileMemoryMaintenanceService(store, config, executor);

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(["local:a", "local:b"], null, null, null), CancellationToken.None);

        result.ContinuationToken.Should().BeNull();
        result.MoreWork.Should().BeFalse();
        result.Scopes.Should().OnlyContain(s => s.ContinuationToken == null && !s.MoreWork);
    }

    // B5 Task 7 — deadline-before-first-yield: a past deadline throws with
    // zero work and no derived-state invalidation.
    [Fact]
    public async Task ExecuteMaintenanceScopeAsync_PastDeadline_ThrowsWithZeroWork()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        MaintenancePolicy policy = MaintenancePolicy.Load(config).Policy!;
        int invalidationsBefore = store.CommittedScopeInvalidationCount;

        Func<Task> act = () => store.ExecuteMaintenanceScopeAsync(
            "local", policy, 50, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — journal budget fail-fast: out-of-range remaining budgets
    // never reach enumeration.
    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task ExecuteMaintenanceScopeAsync_InvalidBudget_FailsFast(int remainingBudget)
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        MaintenancePolicy policy = MaintenancePolicy.Load(config).Policy!;
        int invalidationsBefore = store.CommittedScopeInvalidationCount;

        Func<Task> act = () => store.ExecuteMaintenanceScopeAsync(
            "local", policy, remainingBudget, DateTimeOffset.UtcNow.AddSeconds(5),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — restart safety on the store seam: repeated empty scopes
    // converge with zero work and no invalidation growth.
    [Fact]
    public async Task ExecuteMaintenanceScopeAsync_RestartSafety_EmptyScopeConverges()
    {
        using var scope = new TestHelpers.StoreScope();
        (FileMemoryStore store, IWorkspaceConfig config) = RealStore();
        MaintenancePolicy policy = MaintenancePolicy.Load(config).Policy!;
        int invalidationsBefore = store.CommittedScopeInvalidationCount;

        MaintenanceScopeExecution first = await store.ExecuteMaintenanceScopeAsync(
            "local", policy, 50, DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None);
        MaintenanceScopeExecution second = await store.ExecuteMaintenanceScopeAsync(
            "local", policy, 50, DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None);

        first.ScopeResult.Inspected.Should().Be(0);
        second.ScopeResult.Inspected.Should().Be(0);
        first.JournalConsumed.Should().Be(0);
        second.JournalConsumed.Should().Be(0);
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
    }

    // B5 Task 7 — no per-candidate tasks: the service creates no parallel
    // work and no unbounded queue.
    [Fact]
    public void Service_source_creates_no_per_candidate_tasks_or_unbounded_queues()
    {
        string source = File.ReadAllText(FindServiceSource());

        source.Should().NotContain("Task.Run");
        source.Should().NotContain("Task.Factory");
        source.Should().NotContain("Parallel.");
        source.Should().NotContain("new Thread");
        source.Should().NotContain("ThreadPool.");
        source.Should().NotContain("Channel.");
        source.Should().NotContain("TaskCompletionSource");
    }

    private static string FindServiceSource()
    {
        string directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "src", "Scrinia.Core", "FileMemoryMaintenanceService.cs");
            if (File.Exists(candidate))
                return candidate;
            string parent = Path.GetDirectoryName(directory)!;
            if (parent == directory)
                break;
            directory = parent;
        }
        const string fallback = @"C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\src\Scrinia.Core\FileMemoryMaintenanceService.cs";
        if (File.Exists(fallback))
            return fallback;
        throw new FileNotFoundException("FileMemoryMaintenanceService.cs was not found.");
    }

    private sealed class ConcurrentFakeExecutor : IMaintenanceScopeExecutor
    {
        private readonly object _gate = new();
        private readonly List<(string Scope, int RemainingBudget)> _calls = [];

        internal IReadOnlyList<(string Scope, int RemainingBudget)> Calls
        {
            get
            {
                lock (_gate)
                    return _calls.ToArray();
            }
        }

        public Task<MaintenanceScopeExecution> ExecuteScopeAsync(
            string scope, MaintenancePolicy policy, int remainingJournalBudget,
            DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
        {
            lock (_gate)
                _calls.Add((scope, remainingJournalBudget));
            return Task.FromResult(ScopeExecution(scope, inspected: 1));
        }
    }
}
