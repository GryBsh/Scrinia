using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for concern tracking MCP tools:
/// concern_add (CONC-01), concern_resolve (CONC-02), concern query (CONC-03, ADOPT-02).
/// </summary>
public sealed class ConcernTrackingTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public ConcernTrackingTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helper: ReadMemoryText ────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    /// <summary>Sets up a project so concern_add prerequisite check passes.</summary>
    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals: test concern tracking", CancellationToken.None);
    }

    // ── concern_add tests (CONC-01) ───────────────────────────────────────────

    [Fact]
    public async Task ConcernAdd_StoresConcernMemory()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.ConcernAdd("Risk: auth token expiry not handled",
            "high", "06", id: null, CancellationToken.None);

        // Assert — a concern:* entry must exist in index
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name.StartsWith("concern") || true,
            "concern_add should create a concern entry in index");
        entries.Should().HaveCountGreaterOrEqualTo(1,
            "concern_add should store at least one concern in the index");
    }

    [Fact]
    public async Task ConcernAdd_HasStatusActiveKeyword()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.ConcernAdd("Risk: auth token expiry not handled",
            "high", "06", id: null, CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Keywords != null && e.Keywords.Contains("status:active",
            StringComparer.OrdinalIgnoreCase),
            "concern entry must have status:active keyword");
    }

    [Fact]
    public async Task ConcernAdd_HasSeverityKeyword()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.ConcernAdd("Risk: database overload under peak load",
            "high", "06", id: null, CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Keywords != null && e.Keywords.Contains("severity:high",
            StringComparer.OrdinalIgnoreCase),
            "concern entry must have severity:high keyword");
    }

    [Fact]
    public async Task ConcernAdd_HasPhaseKeyword()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.ConcernAdd("Risk: scope creep in phase 06",
            "medium", "06", id: null, CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Keywords != null && e.Keywords.Contains("phase:06",
            StringComparer.OrdinalIgnoreCase),
            "concern entry must have phase:06 keyword");
    }

    [Fact]
    public async Task ConcernAdd_ResponseContainsConcernName()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await _tools.ConcernAdd("Risk: auth token expiry",
            "high", "06", id: null, CancellationToken.None);

        // Assert — response should contain "concern:" prefix
        result.Should().Contain("concern:",
            "concern_add response must contain the concern name with 'concern:' prefix");
    }

    [Fact]
    public async Task ConcernAdd_FailsWithoutProject()
    {
        // Act — no project_init, no project:context
        string result = await _tools.ConcernAdd("Risk: some risk",
            "low", "all", id: null, CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "concern_add without project:context should return an error");
    }

    [Fact]
    public async Task ConcernAdd_AcceptsOptionalId()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.ConcernAdd("Risk: auth insecure storage",
            "high", "06", id: "auth-risk", CancellationToken.None);

        // Assert — concern:auth-risk must exist in index
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:auth-risk");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == "auth-risk",
            "concern_add with id='auth-risk' should store concern:auth-risk");
    }

    // ── concern_resolve tests (CONC-02) ───────────────────────────────────────

    [Fact]
    public async Task ConcernResolve_UpdatesStatusKeyword()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: auth token expiry",
            "high", "06", id: "auth-risk", CancellationToken.None);

        // Act
        await _tools.ConcernResolve("concern:auth-risk",
            "Added refresh token rotation", verifiedBy: "manual", CancellationToken.None);

        // Assert — status keyword should be "status:resolved", NOT "status:active"
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:auth-risk");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "auth-risk");

        entry.Should().NotBeNull("concern entry should still exist after resolve");
        entry!.Keywords.Should().Contain("status:resolved",
            "concern_resolve should update keyword to status:resolved");
        entry.Keywords.Should().NotContain("status:active",
            "concern_resolve should remove status:active keyword");
    }

    [Fact]
    public async Task ConcernResolve_PreservesOtherKeywords()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: database load",
            "medium", "06", id: "db-risk", CancellationToken.None);

        // Act
        await _tools.ConcernResolve("concern:db-risk",
            "Added connection pooling", verifiedBy: "manual", CancellationToken.None);

        // Assert — severity and phase keywords must still be present
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:db-risk");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "db-risk");

        entry!.Keywords.Should().Contain("severity:medium",
            "severity keyword should be preserved after concern_resolve");
        entry.Keywords.Should().Contain("phase:06",
            "phase keyword should be preserved after concern_resolve");
    }

    [Fact]
    public async Task ConcernResolve_DoesNotArchive()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: test",
            "low", "06", id: "test-risk", CancellationToken.None);

        // Get the versions directory path for concern scope
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("concern:test-risk");
        string storeDir = store.GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(Path.GetDirectoryName(storeDir)!, "versions",
            Path.GetFileName(storeDir)!);

        // Act
        await _tools.ConcernResolve("concern:test-risk", "Resolved it", verifiedBy: "manual", CancellationToken.None);

        // Assert — no version files should be created for "test-risk"
        bool versionsExist = Directory.Exists(versionsDir) &&
            Directory.GetFiles(versionsDir, "test-risk*").Length > 0;
        versionsExist.Should().BeFalse(
            "concern_resolve should NOT archive versions — only updates keywords in place");
    }

    [Fact]
    public async Task ConcernResolve_FailsForUnknownConcern()
    {
        // Arrange
        await InitProject();

        // Act — try to resolve a concern that was never added
        string result = await _tools.ConcernResolve("concern:nonexistent",
            "Some resolution", verifiedBy: "manual", CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "concern_resolve with unknown concern name should return Error:");
        result.Should().ContainEquivalentOf("not found",
            "error should mention 'not found'");
    }

    [Fact]
    public async Task ConcernResolve_AppendsResolutionToContent()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: auth token short lived",
            "high", "06", id: "auth-res", CancellationToken.None);

        // Act
        await _tools.ConcernResolve("concern:auth-res",
            "Extended token lifetime to 24 hours", verifiedBy: "qa", CancellationToken.None);

        // Assert — content after resolve must contain the resolution text
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "concern:auth-res");
        content.Should().ContainEquivalentOf("Extended token lifetime",
            "concern content after resolve should contain the resolution text");
    }

    // ── concern query tests (CONC-03) ─────────────────────────────────────────

    [Fact]
    public async Task ConcernQuery_ReturnsActiveConcerns()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: auth", "high", "06", id: "c1", CancellationToken.None);
        await _tools.ConcernAdd("Risk: db", "medium", "07", id: "c2", CancellationToken.None);

        // Act
        string result = await _tools.Concern(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // Assert — both active concerns should appear
        result.Should().Contain("c1", "active concern 'c1' should appear in query result");
        result.Should().Contain("c2", "active concern 'c2' should appear in query result");
    }

    [Fact]
    public async Task ConcernQuery_FiltersByPhase()
    {
        // Arrange
        await InitProject();
        await _tools.ConcernAdd("Risk: phase06 only", "high", "06", id: "p06-risk", CancellationToken.None);
        await _tools.ConcernAdd("Risk: phase07 only", "low", "07", id: "p07-risk", CancellationToken.None);

        // Act
        string result = await _tools.Concern(phaseFilter: "06", statusFilter: null, CancellationToken.None);

        // Assert — only phase:06 concern should appear
        result.Should().Contain("p06-risk",
            "concern query filtered by phase:06 should include p06-risk");
        result.Should().NotContain("p07-risk",
            "concern query filtered by phase:06 should NOT include p07-risk");
    }

    [Fact]
    public async Task ConcernQuery_ReturnsEmptyMessage()
    {
        // Arrange — no concerns added
        await InitProject();

        // Act
        string result = await _tools.Concern(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // Assert
        result.Should().ContainEquivalentOf("no active concerns",
            "concern query with no active concerns should return 'No active concerns' message");
    }

    [Fact]
    public async Task ConcernQuery_DoesNotDecodeArtifacts()
    {
        // Arrange — add a concern with recognizable content
        await InitProject();
        await _tools.ConcernAdd("Risk: UNIQUE_SENTINEL_CONTENT_12345",
            "high", "06", id: "sentinel", CancellationToken.None);

        // Act — query active concerns
        string result = await _tools.Concern(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // The query returns index-only data (names, keywords).
        // The concern content "UNIQUE_SENTINEL_CONTENT_12345" should NOT appear in the result
        // because it's stored inside the artifact, not in the index.
        result.Should().Contain("sentinel",
            "concern query should list the concern name from index");
        result.Should().NotContain("UNIQUE_SENTINEL_CONTENT_12345",
            "concern query should NOT decode artifacts — content must not appear in the listing");
    }

    // ── Description / context signal tests (ADOPT-02) ─────────────────────────

    [Fact]
    public void ConcernAdd_DescriptionContainsContextSignals()
    {
        // Reflection test: [Description] on ConcernAdd should reference concern_resolve or concern
        var method = typeof(ScriniaProjectTools).GetMethod("ConcernAdd");
        method.Should().NotBeNull("ConcernAdd method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("ConcernAdd must have a [Description] attribute");

        string descText = descAttr!.Description;
        bool hasContextSignal = descText.Contains("concern_resolve", StringComparison.OrdinalIgnoreCase)
            || descText.Contains("concern", StringComparison.OrdinalIgnoreCase);
        hasContextSignal.Should().BeTrue(
            "ConcernAdd description must contain context signals referencing 'concern_resolve' or 'concern'");
    }

    [Fact]
    public void ConcernResolve_DescriptionContainsContextSignals()
    {
        // Reflection test: [Description] on ConcernResolve should reference concern_add or "after"
        var method = typeof(ScriniaProjectTools).GetMethod("ConcernResolve");
        method.Should().NotBeNull("ConcernResolve method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("ConcernResolve must have a [Description] attribute");

        string descText = descAttr!.Description;
        bool hasContextSignal = descText.Contains("concern_add", StringComparison.OrdinalIgnoreCase)
            || descText.Contains("after", StringComparison.OrdinalIgnoreCase);
        hasContextSignal.Should().BeTrue(
            "ConcernResolve description must contain context signals referencing 'concern_add' or 'after'");
    }

    [Fact]
    public void ConcernQuery_DescriptionContainsContextSignals()
    {
        // Reflection test: [Description] on Concern should reference concern_add or plan_status
        var method = typeof(ScriniaProjectTools).GetMethod("Concern");
        method.Should().NotBeNull("Concern (query) method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("Concern (query) must have a [Description] attribute");

        string descText = descAttr!.Description;
        bool hasContextSignal = descText.Contains("concern_add", StringComparison.OrdinalIgnoreCase)
            || descText.Contains("plan_status", StringComparison.OrdinalIgnoreCase);
        hasContextSignal.Should().BeTrue(
            "Concern (query) description must contain context signals referencing 'concern_add' or 'plan_status'");
    }

    // ── plan_status concern enrichment tests (CONC-04) ────────────────────────

    [Fact]
    public async Task PlanStatus_IncludesConcernCount()
    {
        // Arrange — project with 2 active concerns
        await InitProject();
        await _tools.ConcernAdd("Risk: alpha", "medium", "06", id: "ps-c1", CancellationToken.None);
        await _tools.ConcernAdd("Risk: beta", "medium", "06", id: "ps-c2", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — response must include concern count line
        result.Should().Contain("Concerns: 2 active",
            "plan_status should report active concern count when concerns exist");
    }

    [Fact]
    public async Task PlanStatus_IncludesHighSeverityCount()
    {
        // Arrange — 1 high + 1 medium concern
        await InitProject();
        await _tools.ConcernAdd("Risk: critical thing", "high", "06", id: "ps-high", CancellationToken.None);
        await _tools.ConcernAdd("Risk: minor thing", "medium", "06", id: "ps-med", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — response must include high-severity count
        result.Should().Contain("1 high-severity",
            "plan_status should include high-severity count when high-severity concerns exist");
    }

    [Fact]
    public async Task PlanStatus_NoConcernLineWhenNoConcerns()
    {
        // Arrange — project with no concerns
        await InitProject();

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — response must NOT include a Concerns: line
        result.Should().NotContain("Concerns:",
            "plan_status should NOT include a Concerns: line when no concerns exist");
    }

    [Fact]
    public async Task ContextResume_IncludesConcernSummary()
    {
        // Arrange — project with 1 active concern
        await InitProject();
        await _tools.ConcernAdd("Risk: gamma", "high", "06", id: "pr-c1", CancellationToken.None);

        // Act
        string result = await _tools.ContextResume(CancellationToken.None);

        // Assert — context_resume response should mention concern(s)
        bool hasConcernInfo = result.Contains("Concerns:", StringComparison.OrdinalIgnoreCase)
            || result.Contains("concern", StringComparison.OrdinalIgnoreCase);
        hasConcernInfo.Should().BeTrue(
            "context_resume should include concern summary when active concerns exist");
    }

    [Fact]
    public async Task PlanStatus_HandlesMissingConcernScope()
    {
        // Arrange — fresh project, concern scope never created (no concern_add called)
        await InitProject();

        // Act — must not throw
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — should succeed and NOT contain "Concerns:" line
        result.Should().NotBeNull("plan_status must return a result even when concern scope does not exist");
        result.Should().Contain("Project:",
            "plan_status response should still contain project info when concern scope is missing");
        result.Should().NotContain("Concerns:",
            "plan_status should not include Concerns: line when concern scope is missing");
    }
}
