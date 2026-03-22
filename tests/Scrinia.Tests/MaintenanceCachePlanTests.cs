using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests verifying that PlanStatus and ContextResume use MaintenanceCache when fresh,
/// and fall back to live ScanStaleness/ScanDrift when no cache exists.
/// </summary>
public sealed class MaintenanceCachePlanTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public MaintenanceCachePlanTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals: test cache integration\nConstraints: none", CancellationToken.None);
    }

    // ── PlanStatus cache-first tests ─────────────────────────────────────────

    [Fact]
    public async Task PlanStatus_UsesCacheWhenFresh()
    {
        // Arrange — init project, then write a cache with known counts
        await InitProject();
        var store = MemoryStoreContext.Current!;
        MaintenanceCache.WriteCache(store, staleCount: 3, reviewCount: 2,
            driftCount: 1, missingCount: 0, orphanCount: 0);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — should use cached values and show cache age annotation
        result.Should().Contain("3 memory(s) have passed their review date",
            "PlanStatus should use cached stale count");
        result.Should().Contain("2 memory(s) have review conditions set",
            "PlanStatus should use cached review count");
        result.Should().Contain("1 code reference(s) have drifted",
            "PlanStatus should use cached drift count");
        result.Should().Contain("cached",
            "PlanStatus should annotate alerts with cache age when using cache");
        result.Should().Contain("min ago",
            "PlanStatus should show minutes-ago suffix for cached data");
        // missingCount is 0, so no missing line should appear
        result.Should().NotContain("point to missing files",
            "PlanStatus should not show missing alert when cached missingCount is 0");
    }

    [Fact]
    public async Task PlanStatus_FallsBackToLiveScanWhenNoCache()
    {
        // Arrange — init project with NO cache, but add a stale memory
        await InitProject();
        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("stale-mem", "file://s1", 100, 1,
            DateTimeOffset.UtcNow, "A stale memory",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — should detect staleness via live scan and NOT mention cache
        result.Should().Contain("passed their review date",
            "PlanStatus should detect stale memory via live scan when no cache exists");
        result.Should().NotContain("cached",
            "PlanStatus should not mention cache when falling back to live scan");
    }

    // ── ContextResume cache-first tests ─────────────────────────────────────────

    [Fact]
    public async Task ContextResume_UsesCacheWhenFresh()
    {
        // Arrange — init project, then write a cache with known counts
        await InitProject();
        var store = MemoryStoreContext.Current!;
        MaintenanceCache.WriteCache(store, staleCount: 5, reviewCount: 1,
            driftCount: 0, missingCount: 2, orphanCount: 0);

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert — should use cached values and show cache age annotation
        result.Should().Contain("5 memory(s) have passed their review date",
            "ContextResume should use cached stale count");
        result.Should().Contain("1 memory(s) have review conditions set",
            "ContextResume should use cached review count");
        result.Should().Contain("2 code reference(s) point to missing files",
            "ContextResume should use cached missing count");
        result.Should().Contain("cached",
            "ContextResume should annotate alerts with cache age when using cache");
        result.Should().Contain("min ago",
            "ContextResume should show minutes-ago suffix for cached data");
        // driftCount is 0, so no drift line should appear
        result.Should().NotContain("have drifted",
            "ContextResume should not show drift alert when cached driftCount is 0");
    }

    [Fact]
    public async Task ContextResume_FallsBackToLiveScanWhenNoCache()
    {
        // Arrange — init project with NO cache, but add a memory with ReviewWhen
        await InitProject();
        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("review-mem", "file://r1", 100, 1,
            DateTimeOffset.UtcNow, "Needs review when auth changes",
            ReviewWhen: "when auth changes"));

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert — should detect review condition via live scan and NOT mention cache
        result.Should().Contain("review conditions set",
            "ContextResume should detect review condition via live scan when no cache exists");
        result.Should().NotContain("cached",
            "ContextResume should not mention cache when falling back to live scan");
    }
}
