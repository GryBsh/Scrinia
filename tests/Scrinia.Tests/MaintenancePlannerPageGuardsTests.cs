using Scrinia.Core;

namespace Scrinia.Tests;

public class MaintenancePlannerPageGuardsTests
{
    [Fact]
    public void Valid_Counters_AreAccepted()
    {
        var page = new MaintenancePlannerPage(
            projections: Array.Empty<MaintenancePlannerProjection>(),
            scanned: 2,
            projected: 0,
            metadataReads: 0,
            returned: 0,
            lookahead: 0,
            hasMore: false,
            unsafeScanned: 0,
            unsafeJournalScanned: 0,
            unsafeNonJournalScanned: 0,
            safeJournalInspected: 2,
            safeNonJournalInspected: 0,
            journalBudget: MaintenanceLimits.JournalBudgetMaximum,
            journalBudgetConsumed: 0,
            journalInspected: 2,
            nonJournalInspected: 0,
            journalCapReached: false,
            journalDeferred: 0);
        Assert.Equal(2, page.Scanned);
    }

    [Fact]
    public void Base_Equation_Violation_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MaintenancePlannerPage(
            projections: Array.Empty<MaintenancePlannerProjection>(),
            scanned: 3,
            projected: 0,
            metadataReads: 0,
            returned: 0,
            lookahead: 0,
            hasMore: false,
            unsafeScanned: 0,
            unsafeJournalScanned: 0,
            unsafeNonJournalScanned: 0,
            safeJournalInspected: 2,
            safeNonJournalInspected: 0,
            journalBudget: MaintenanceLimits.JournalBudgetMaximum,
            journalBudgetConsumed: 0,
            journalInspected: 1,
            nonJournalInspected: 0,
            journalCapReached: false,
            journalDeferred: 0));
    }

    [Fact]
    public void Bounds_Still_Enforced()
    {
        Assert.Throws<ArgumentException>(() => new MaintenancePlannerPage(
            projections: Array.Empty<MaintenancePlannerProjection>(),
            scanned: MaintenanceLimits.CandidatePageSize + 1,
            projected: 0,
            metadataReads: 0,
            returned: 0,
            lookahead: 0,
            hasMore: false,
            unsafeScanned: 0,
            unsafeJournalScanned: 0,
            unsafeNonJournalScanned: 0,
            safeJournalInspected: MaintenanceLimits.CandidatePageSize + 1,
            safeNonJournalInspected: 0,
            journalBudget: MaintenanceLimits.JournalBudgetMaximum,
            journalBudgetConsumed: 0,
            journalInspected: MaintenanceLimits.CandidatePageSize + 1,
            nonJournalInspected: 0,
            journalCapReached: false,
            journalDeferred: 0));
    }
}
