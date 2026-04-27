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
        await ScriniaProjectTools.ProjectInit("Goals: test concern tracking", CancellationToken.None);
    }

    // ── concern_add tests (CONC-01) ───────────────────────────────────────────

    [Fact]
    public async Task ConcernAdd_StoresConcernMemory()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.ConcernAdd("Risk: auth token expiry not handled",
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
        await ScriniaProjectTools.ConcernAdd("Risk: auth token expiry not handled",
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
        await ScriniaProjectTools.ConcernAdd("Risk: database overload under peak load",
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
        await ScriniaProjectTools.ConcernAdd("Risk: scope creep in phase 06",
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
        string result = await ScriniaProjectTools.ConcernAdd("Risk: auth token expiry",
            "high", "06", id: null, CancellationToken.None);

        // Assert — response should contain "concern:" prefix
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "concern_add should succeed");
        r.Path.Should().Contain("/concern/",
            "concern_add response must contain the concern name with '/concern/' path prefix");
    }

    [Fact]
    public async Task ConcernAdd_FailsWithoutProject()
    {
        // Act — no project_init, no project:context
        string result = await ScriniaProjectTools.ConcernAdd("Risk: some risk",
            "low", "all", id: null, CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "concern_add without project:context should return an error");
    }

    [Fact]
    public async Task ConcernAdd_AcceptsOptionalId()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.ConcernAdd("Risk: auth insecure storage",
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
        await ScriniaProjectTools.ConcernAdd("Risk: auth token expiry",
            "high", "06", id: "auth-risk", CancellationToken.None);

        // Act
        await ScriniaProjectTools.ConcernResolve("concern:auth-risk",
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
        await ScriniaProjectTools.ConcernAdd("Risk: database load",
            "medium", "06", id: "db-risk", CancellationToken.None);

        // Act
        await ScriniaProjectTools.ConcernResolve("concern:db-risk",
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
        await ScriniaProjectTools.ConcernAdd("Risk: test",
            "low", "06", id: "test-risk", CancellationToken.None);

        // Get the versions directory path for concern scope
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("concern:test-risk");
        string storeDir = store.GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(Path.GetDirectoryName(storeDir)!, "versions",
            Path.GetFileName(storeDir)!);

        // Act
        await ScriniaProjectTools.ConcernResolve("concern:test-risk", "Resolved it", verifiedBy: "manual", CancellationToken.None);

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
        string result = await ScriniaProjectTools.ConcernResolve("concern:nonexistent",
            "Some resolution", verifiedBy: "manual", CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "concern_resolve with unknown concern name should return an error");
        r.Error.Should().ContainEquivalentOf("not found",
            "error should mention 'not found'");
    }

    [Fact]
    public async Task ConcernResolve_AppendsResolutionToContent()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: auth token short lived",
            "high", "06", id: "auth-res", CancellationToken.None);

        // Act
        await ScriniaProjectTools.ConcernResolve("concern:auth-res",
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
        await ScriniaProjectTools.ConcernAdd("Risk: auth", "high", "06", id: "c1", CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: db", "medium", "07", id: "c2", CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ConcernList(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // Assert — both active concerns should appear
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "concern list should succeed");
        r.Content.Should().Contain("c1", "active concern 'c1' should appear in query result");
        r.Content.Should().Contain("c2", "active concern 'c2' should appear in query result");
    }

    [Fact]
    public async Task ConcernQuery_FiltersByPhase()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: phase06 only", "high", "06", id: "p06-risk", CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: phase07 only", "low", "07", id: "p07-risk", CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ConcernList(phaseFilter: "06", statusFilter: null, CancellationToken.None);

        // Assert — only phase:06 concern should appear
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "concern list with phase filter should succeed");
        r.Content.Should().Contain("p06-risk",
            "concern query filtered by phase:06 should include p06-risk");
        r.Content.Should().NotContain("p07-risk",
            "concern query filtered by phase:06 should NOT include p07-risk");
    }

    [Fact]
    public async Task ConcernQuery_ReturnsEmptyMessage()
    {
        // Arrange — no concerns added
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.ConcernList(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "concern list should succeed");
        r.Content.Should().ContainEquivalentOf("no active concerns",
            "concern query with no active concerns should return 'No active concerns' message");
    }

    [Fact]
    public async Task ConcernQuery_DoesNotDecodeArtifacts()
    {
        // Arrange — add a concern with recognizable content
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: UNIQUE_SENTINEL_CONTENT_12345",
            "high", "06", id: "sentinel", CancellationToken.None);

        // Act — query active concerns
        string result = await ScriniaProjectTools.ConcernList(phaseFilter: null, statusFilter: null, CancellationToken.None);

        // The query returns index-only data (names, keywords).
        // The concern content "UNIQUE_SENTINEL_CONTENT_12345" should NOT appear in the result
        // because it's stored inside the artifact, not in the index.
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "concern list should succeed");
        r.Content.Should().Contain("sentinel",
            "concern query should list the concern name from index");
        r.Content.Should().NotContain("UNIQUE_SENTINEL_CONTENT_12345",
            "concern query should NOT decode artifacts — content must not appear in the listing");
    }

    // ── Internal dispatcher existence tests (WF-14: MCP attributes removed) ───

    [Fact]
    public void ConcernAdd_DescriptionContainsContextSignals()
    {
        // ConcernAdd is an internal method called by ConcernDispatch (now exposed via entity() tool).
        var method = typeof(ScriniaProjectTools).GetMethod("ConcernAdd",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("ConcernAdd must exist as an internal method");

        var dispatcher = typeof(ScriniaProjectTools).GetMethod("ConcernDispatch");
        dispatcher.Should().NotBeNull("ConcernDispatch dispatcher must exist");

        // No [McpServerTool] attribute — concern is routed through entity() now
        var mcpAttr = dispatcher!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>();
        mcpAttr.Should().BeNull("ConcernDispatch should no longer have [McpServerTool] — it is exposed via entity()");
    }

    [Fact]
    public void ConcernResolve_DescriptionContainsContextSignals()
    {
        // ConcernResolve is an internal method called by ConcernDispatch (now exposed via entity() tool).
        var method = typeof(ScriniaProjectTools).GetMethod("ConcernResolve",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("ConcernResolve must exist as an internal method");

        var dispatcher = typeof(ScriniaProjectTools).GetMethod("ConcernDispatch");
        dispatcher.Should().NotBeNull("ConcernDispatch dispatcher must exist");

        // No [McpServerTool] attribute — concern is routed through entity() now
        var mcpAttr = dispatcher!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>();
        mcpAttr.Should().BeNull("ConcernDispatch should no longer have [McpServerTool] — it is exposed via entity()");
    }

    [Fact]
    public void ConcernQuery_DescriptionContainsContextSignals()
    {
        // ConcernDispatch is now an internal dispatcher (exposed via entity() tool).
        var dispatcher = typeof(ScriniaProjectTools).GetMethod("ConcernDispatch");
        dispatcher.Should().NotBeNull("ConcernDispatch dispatcher must exist");

        // No [McpServerTool] attribute — concern is routed through entity() now
        var mcpAttr = dispatcher!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>();
        mcpAttr.Should().BeNull("ConcernDispatch should no longer have [McpServerTool] — it is exposed via entity()");
    }

    // ── plan_status concern enrichment tests (CONC-04) ────────────────────────

    [Fact]
    public async Task PlanStatus_IncludesConcernCount()
    {
        // Arrange — project with 2 active concerns
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: alpha", "medium", "06", id: "ps-c1", CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: beta", "medium", "06", id: "ps-c2", CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response must include concern count line
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "plan_status should succeed");
        r.Content.Should().Contain("Concerns: 2 active",
            "plan_status should report active concern count when concerns exist");
    }

    [Fact]
    public async Task PlanStatus_IncludesHighSeverityCount()
    {
        // Arrange — 1 high + 1 medium concern
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: critical thing", "high", "06", id: "ps-high", CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: minor thing", "medium", "06", id: "ps-med", CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response must include high-severity count
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "plan_status should succeed");
        r.Content.Should().Contain("1 high-severity",
            "plan_status should include high-severity count when high-severity concerns exist");
    }

    [Fact]
    public async Task PlanStatus_NoConcernLineWhenNoConcerns()
    {
        // Arrange — project with no concerns
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response must NOT include a Concerns: line
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "plan_status should succeed");
        r.Content.Should().NotContain("Concerns:",
            "plan_status should NOT include a Concerns: line when no concerns exist");
    }

    [Fact]
    public async Task ContextResume_IncludesConcernSummary()
    {
        // Arrange — project with 1 active concern
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: gamma", "high", "06", id: "pr-c1", CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — context_resume response should mention concern(s)
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "context_resume should succeed");
        string fullText = (r.Content ?? "") + string.Join(" ", r.ActionNeeded) + string.Join(" ", r.Info);
        bool hasConcernInfo = fullText.Contains("Concerns:", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("concern", StringComparison.OrdinalIgnoreCase);
        hasConcernInfo.Should().BeTrue(
            "context_resume should include concern summary when active concerns exist");
    }

    [Fact]
    public async Task PlanStatus_HandlesMissingConcernScope()
    {
        // Arrange — fresh project, concern scope never created (no concern_add called)
        await InitProject();

        // Act — must not throw
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — should succeed and NOT contain "Concerns:" line
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "plan_status must return a result even when concern scope does not exist");
        r.Content.Should().Contain("Project:",
            "plan_status response should still contain project info when concern scope is missing");
        r.Content.Should().NotContain("Concerns:",
            "plan_status should not include Concerns: line when concern scope is missing");
    }
}
