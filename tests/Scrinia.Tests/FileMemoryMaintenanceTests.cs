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
}
