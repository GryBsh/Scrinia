using Scrinia.Core.Configuration;

namespace Scrinia.Core;

// B5 Task 6 — lock-scoped Core maintenance execution.
// Owns orchestration only: policy is loaded once, scopes resolve in ordinal
// order, one invocation-wide journal budget is allocated greedily with no
// reset, and the single FileMemoryStore admission seam performs all deletion.
// This service never enumerates, classifies, deletes, or retries itself.
internal interface IMaintenanceScopeExecutor
{
    Task<MaintenanceScopeExecution> ExecuteScopeAsync(
        string scope,
        MaintenancePolicy policy,
        int remainingJournalBudget,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken);
}

internal sealed record MaintenanceScopeExecution(
    MaintenanceScopeResult ScopeResult,
    int JournalConsumed,
    bool RecoveryRetained = false);

internal sealed class StoreMaintenanceScopeExecutor : IMaintenanceScopeExecutor
{
    private readonly FileMemoryStore _store;

    internal StoreMaintenanceScopeExecutor(FileMemoryStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<MaintenanceScopeExecution> ExecuteScopeAsync(
        string scope,
        MaintenancePolicy policy,
        int remainingJournalBudget,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken) =>
        _store.ExecuteMaintenanceScopeAsync(scope, policy, remainingJournalBudget, deadlineUtc, cancellationToken);
}

internal sealed class FileMemoryMaintenanceService
{
    private readonly FileMemoryStore _store;
    private readonly IWorkspaceConfig _config;
    private readonly IMaintenanceScopeExecutor _executor;

    internal FileMemoryMaintenanceService(
        FileMemoryStore store,
        IWorkspaceConfig config,
        IMaintenanceScopeExecutor? executor = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _executor = executor ?? new StoreMaintenanceScopeExecutor(store);
    }

    internal async Task<MaintenanceResult> ExecuteAsync(
        MaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ContinuationToken is not null)
            return InvalidContinuation();

        MaintenancePolicyLoadResult loaded = MaintenancePolicy.Load(_config);
        if (!loaded.IsValid || loaded.Policy is null)
            return PolicyInvalid(loaded.Errors);
        MaintenancePolicy policy = loaded.Policy;

        string[] scopes = ResolveScopes(request.Scopes);
        if (scopes.Length == 0)
            return CompletedEmpty();

        int journalBudget = MaintenancePlanner.EffectiveJournalBudget(policy, request.BatchSizeOverride);
        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(policy.TimeBudgetSeconds);

        var scopeResults = new List<MaintenanceScopeResult>(scopes.Length);
        var warnings = new List<string>();
        int inspected = 0;
        int deletedArchives = 0;
        int deletedRecoveryEvidence = 0;
        int remainingJournalBudget = journalBudget;
        bool lockContended = false;
        bool recoveryRetained = false;
        bool partialFailure = false;
        bool stoppedEarly = false;

        foreach (string scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                stoppedEarly = true;
                break;
            }
            if (inspected >= MaintenanceLimits.CandidatePageSize)
            {
                stoppedEarly = true;
                break;
            }
            if (remainingJournalBudget < 1)
            {
                stoppedEarly = true;
                break;
            }

            MaintenanceScopeExecution execution;
            try
            {
                execution = await _executor.ExecuteScopeAsync(
                    scope, policy, remainingJournalBudget, deadlineUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (FileLockTimeoutException)
            {
                lockContended = true;
                scopeResults.Add(ContendedScope(scope));
                continue;
            }
            catch (TimeoutException)
            {
                stoppedEarly = true;
                break;
            }

            remainingJournalBudget = Math.Max(0, remainingJournalBudget - execution.JournalConsumed);
            inspected += execution.ScopeResult.Inspected;
            deletedArchives += execution.ScopeResult.DeletedArchives;
            deletedRecoveryEvidence += execution.ScopeResult.DeletedRecoveryEvidence;
            if (execution.RecoveryRetained || execution.ScopeResult.Retained > 0)
                recoveryRetained = true;
            if (!execution.ScopeResult.LockContended && execution.ScopeResult.Warnings.Count > 0)
                partialFailure = true;
            if (execution.ScopeResult.LockContended)
                lockContended = true;
            scopeResults.Add(execution.ScopeResult);
            warnings.AddRange(execution.ScopeResult.Warnings);
        }

        return new MaintenanceResult(
            inspected, deletedArchives, deletedRecoveryEvidence,
            !stoppedEarly, false, recoveryRetained, lockContended, false, partialFailure, false,
            null, scopeResults, warnings, null);
    }

    private static readonly string[] InvalidContinuationWarning = ["Invalid continuation."];

    private string[] ResolveScopes(IReadOnlyList<string>? requested)
    {
        IReadOnlyList<string> scopes = requested ?? _store.ResolveReadScopes(null);
        return scopes.OrderBy(static scope => scope, StringComparer.Ordinal).ToArray();
    }

    private static MaintenanceResult InvalidContinuation() =>
        new(0, 0, 0, false, false, false, false, false, false, false,
            null, Array.Empty<MaintenanceScopeResult>(),
            InvalidContinuationWarning, null);

    private static MaintenanceResult PolicyInvalid(IReadOnlyList<MaintenancePolicyError> errors)
    {
        MaintenancePolicyError first = errors.Count > 0
            ? errors[0]
            : new MaintenancePolicyError("Scrinia:Maintenance", "INVALID_POLICY");
        return new MaintenanceResult(0, 0, 0, false, false, false, false, true, false, false,
            null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(),
            $"INVALID_POLICY:{first.Key}:{first.Code}");
    }

    private static MaintenanceResult CompletedEmpty() =>
        new(0, 0, 0, true, false, false, false, false, false, false,
            null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(), null);

    private static MaintenanceScopeResult ContendedScope(string scope) =>
        new(scope, 0, 0, 0, 0, true, false, null,
            Array.Empty<MaintenanceCandidateResult>(),
            new[] { $"Scope '{scope}' lock contended." });
}
