using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests verifying that PlanStatus and ContextResume surface
/// staleness and drift alerts when memories have ReviewAfter/ReviewWhen
/// or CodeRefs that are stale/missing.
/// </summary>
public sealed class StalenessAndDriftIntegrationTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public StalenessAndDriftIntegrationTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── PlanStatus staleness alerts ───────────────────────────────────────────

    [Fact]
    public async Task PlanStatus_WithStaleMemory_ContainsPassedReviewDateAlert()
    {
        // Arrange — initialize project, then add a memory with past ReviewAfter
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("stale-entry", "file://s1", 100, 1,
            DateTimeOffset.UtcNow, "A stale memory",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert
        result.Should().Contain("passed their review date",
            "PlanStatus should alert when memories have passed their ReviewAfter date");
    }

    [Fact]
    public async Task PlanStatus_WithReviewWhenMemory_ContainsReviewConditionsAlert()
    {
        // Arrange — initialize project, then add a memory with ReviewWhen
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("conditional-entry", "file://c1", 100, 1,
            DateTimeOffset.UtcNow, "Needs review when auth changes",
            ReviewWhen: "when auth changes"));

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert
        result.Should().Contain("review conditions set",
            "PlanStatus should alert when memories have ReviewWhen conditions");
    }

    [Fact]
    public async Task PlanStatus_WithNoStalenessOrDrift_DoesNotContainAlerts()
    {
        // Arrange — initialize project with no staleness or drift entries
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — none of the alert messages should appear
        result.Should().NotContain("passed their review date",
            "PlanStatus should not show staleness alert when no entries are stale");
        result.Should().NotContain("review conditions set",
            "PlanStatus should not show review alert when no entries have ReviewWhen");
        result.Should().NotContain("have drifted",
            "PlanStatus should not show drift alert when no code refs have drifted");
        result.Should().NotContain("point to missing files",
            "PlanStatus should not show missing alert when no code refs are missing");
    }

    // ── ContextResume staleness alerts ───────────────────────────────────────────

    [Fact]
    public async Task ContextResume_WithStaleMemory_ContainsPassedReviewDateAlert()
    {
        // Arrange — initialize project, then add a memory with past ReviewAfter
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("stale-entry", "file://s1", 100, 1,
            DateTimeOffset.UtcNow, "A stale memory",
            ReviewAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert
        result.Should().Contain("passed their review date",
            "ContextResume should alert when memories have passed their ReviewAfter date");
    }

    [Fact]
    public async Task ContextResume_WithReviewWhenMemory_ContainsReviewConditionsAlert()
    {
        // Arrange — initialize project, then add a memory with ReviewWhen
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("conditional-entry", "file://c1", 100, 1,
            DateTimeOffset.UtcNow, "Needs review when auth changes",
            ReviewWhen: "when auth changes"));

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert
        result.Should().Contain("review conditions set",
            "ContextResume should alert when memories have ReviewWhen conditions");
    }

    [Fact]
    public async Task ContextResume_WithNoStalenessOrDrift_DoesNotContainAlerts()
    {
        // Arrange — initialize project with no staleness or drift entries
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert — none of the alert messages should appear
        result.Should().NotContain("passed their review date",
            "ContextResume should not show staleness alert when no entries are stale");
        result.Should().NotContain("review conditions set",
            "ContextResume should not show review alert when no entries have ReviewWhen");
        result.Should().NotContain("have drifted",
            "ContextResume should not show drift alert when no code refs have drifted");
        result.Should().NotContain("point to missing files",
            "ContextResume should not show missing alert when no code refs are missing");
    }
}
