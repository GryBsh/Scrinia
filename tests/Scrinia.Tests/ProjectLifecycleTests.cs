using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for the project lifecycle write-path MCP tools:
/// project_init (PROJ-01), plan_requirements (PROJ-02), plan_roadmap (PROJ-03).
/// </summary>
public sealed class ProjectLifecycleTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public ProjectLifecycleTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── project_init tests (PROJ-01) ──────────────────────────────────────────

    [Fact]
    public async Task ProjectInit_StoresProjectContext()
    {
        // Act
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Assert — project:context memory must exist
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:context");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "project_init should store a project:context memory");
    }

    [Fact]
    public async Task ProjectInit_ResultContainsProjectContextReference()
    {
        // Act
        string result = await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Assert
        result.Should().Contain("project:context",
            "project_init result should reference the stored project:context memory");
    }

    [Fact]
    public async Task ProjectInit_CreatesProjectState()
    {
        // Act
        await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Assert — project:state memory must exist with expected fields
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        stateText.Should().Contain("Project:", "project:state should contain 'Project:' field");
        stateText.Should().Contain("Phase:", "project:state should contain 'Phase:' field");
    }

    [Fact]
    public async Task ProjectInit_ReturnsProjectId()
    {
        // Act
        string result = await _tools.ProjectInit("Goals: build X\nConstraints: none", CancellationToken.None);

        // Assert — result contains workspace-derived project ID (sanitized directory name)
        string expectedId = Path.GetFileName(_scope.WorkspaceDir)
            .Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        // The result must contain at least the workspace dir basename or sanitized form
        string workspaceName = Path.GetFileName(_scope.WorkspaceDir);
        result.Should().NotBeNullOrEmpty("result should be a non-empty string");
        // The result message should reference the project ID
        result.Should().MatchRegex(@"Initialized project '[\w\-_]+'",
            "result should contain 'Initialized project' with an ID");
    }

    [Fact]
    public async Task ProjectInit_IncludesOwnershipHint()
    {
        // Act
        string result = await _tools.ProjectInit("Goals: build X", CancellationToken.None);

        // Assert
        result.Should().Contain(".scrinia/",
            "result should include .scrinia/ ownership hint");
    }

    // ── plan_requirements tests (PROJ-02) ─────────────────────────────────────

    [Fact]
    public async Task PlanRequirements_StoresRequirements()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);

        // Act
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:requirements");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "plan_requirements should store a project:requirements memory");
    }

    [Fact]
    public async Task PlanRequirements_UpdatesState()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);

        // Act
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: reqs", CancellationToken.None);

        // Assert — state should be updated with recent timestamp
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        stateText.Should().Contain("Last action:", "project:state should contain 'Last action:' after plan_requirements");
        stateText.Should().Contain(DateTime.UtcNow.Year.ToString(),
            "project:state should contain a recent year in the timestamp");
    }

    [Fact]
    public async Task PlanRequirements_FailsWithoutInit()
    {
        // Act — call plan_requirements without calling project_init first
        string result = await _tools.PlanRequirements(
            "- PROJ-01: init", CancellationToken.None);

        // Assert
        result.Should().Contain("project_init",
            "plan_requirements without project_init should return an error mentioning 'project_init'");
        result.Should().StartWith("Error:",
            "error responses should start with 'Error:'");
    }

    [Fact]
    public async Task PlanRequirements_IncludesOwnershipHint()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);

        // Act
        string result = await _tools.PlanRequirements(
            "- PROJ-01: init", CancellationToken.None);

        // Assert
        result.Should().Contain(".scrinia/",
            "plan_requirements result should include .scrinia/ ownership hint");
    }

    // ── plan_roadmap tests (PROJ-03) ──────────────────────────────────────────

    [Fact]
    public async Task PlanRoadmap_StoresRoadmap()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act
        string roadmap = "### Phase 1\nPROJ-01, PROJ-02\n- implement init";
        await _tools.PlanRoadmap(roadmap, CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("plan:roadmap");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "plan_roadmap should store a plan:roadmap memory");
    }

    [Fact]
    public async Task PlanRoadmap_ValidatesReqIds()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act — roadmap contains all required REQ-IDs
        string roadmap = "### Phase 1\nPROJ-01 and PROJ-02 tasks here";
        string result = await _tools.PlanRoadmap(roadmap, CancellationToken.None);

        // Assert — should succeed (not an error)
        result.Should().NotStartWith("Error:",
            "plan_roadmap with all REQ-IDs present should succeed");
        result.Should().Contain("plan:roadmap",
            "successful plan_roadmap should reference the stored memory");
    }

    [Fact]
    public async Task PlanRoadmap_RejectsMissingReqIds()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act — roadmap is missing PROJ-02
        string result = await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01 tasks only", CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "plan_roadmap with missing REQ-IDs should return an error");
        result.Should().Contain("missing",
            "error should mention 'missing' REQ-IDs");

        // Verify nothing was stored
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("plan:roadmap");
        var entries = store.LoadIndex(scope);
        entries.Should().NotContain(e => e.Name == subject,
            "plan_roadmap should store nothing when REQ-IDs are missing");
    }

    [Fact]
    public async Task PlanRoadmap_AcceptsExtraReqIds()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init", CancellationToken.None);

        // Act — roadmap has PROJ-01 (required) plus NEW-99 (not in requirements)
        string result = await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01 and NEW-99 tasks", CancellationToken.None);

        // Assert — extra REQ-IDs are allowed (agent may define new ones)
        result.Should().NotStartWith("Error:",
            "plan_roadmap with extra REQ-IDs should succeed (with optional warning)");
    }

    [Fact]
    public async Task PlanRoadmap_UpdatesState()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act
        await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01, PROJ-02", CancellationToken.None);

        // Assert — state should be updated
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        stateText.Should().Contain("Roadmap",
            "project:state should reference roadmap after plan_roadmap");
    }

    [Fact]
    public async Task PlanRoadmap_IncludesOwnershipHint()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements("- PROJ-01: init", CancellationToken.None);

        // Act
        string result = await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01 tasks", CancellationToken.None);

        // Assert
        result.Should().Contain(".scrinia/",
            "plan_roadmap result should include .scrinia/ ownership hint");
    }

    // ── plan_resume tests (PROJ-04) ───────────────────────────────────────────

    [Fact]
    public async Task PlanResume_ReturnsStructuredSummary()
    {
        // Arrange — full state via all three write tools
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);
        await _tools.PlanRequirements("- PROJ-01: init\n- PROJ-02: reqs", CancellationToken.None);
        await _tools.PlanRoadmap("### Phase 1\nPROJ-01, PROJ-02 tasks", CancellationToken.None);

        // Act
        string result = await _tools.PlanResume(CancellationToken.None);

        // Assert — all required fields present
        result.Should().Contain("Project:", "plan_resume must include project name");
        result.Should().Contain("Phase:", "plan_resume must include current phase");
        result.Should().Contain("Progress:", "plan_resume must include progress");
        result.Should().Contain("Last action:", "plan_resume must include last action");
        result.Should().Contain("Next:", "plan_resume must include next step");
    }

    [Fact]
    public async Task PlanResume_RespectsResponseCap()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);

        // Act
        string result = await _tools.PlanResume(CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_resume response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task PlanResume_IncludesNextActionSuggestion()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);

        // Act
        string result = await _tools.PlanResume(CancellationToken.None);

        // Assert — must contain a concrete suggestion
        bool hasConcreteAction = result.Contains("run ") || result.Contains("plan_") || result.Contains("task_");
        hasConcreteAction.Should().BeTrue(
            "plan_resume must return a concrete next action (contains 'run ', 'plan_', or 'task_')");
    }

    [Fact]
    public async Task PlanResume_RebuildsFromMemories()
    {
        // Arrange — initialize project so memories exist
        await _tools.ProjectInit("Goals: build a memory server for AI agents", CancellationToken.None);
        await _tools.PlanRequirements("- PROJ-01: init\n- PROJ-02: reqs", CancellationToken.None);

        // Delete project:state artifact so rebuild is triggered
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:state");
        store.DeleteArtifact(subject, scope);
        store.Remove(subject, scope);

        // Act
        string result = await _tools.PlanResume(CancellationToken.None);

        // Assert — rebuilt from memories prefix must be present
        result.Should().ContainEquivalentOf("State rebuilt from memories",
            "plan_resume should indicate state was rebuilt when project:state is missing");
        result.Should().NotStartWith("Error:",
            "plan_resume should succeed even without project:state if other memories exist");
    }

    [Fact]
    public async Task PlanResume_FailsWithoutAnyMemories()
    {
        // Act — no project memories at all
        string result = await _tools.PlanResume(CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "plan_resume with no project memories should return an error");
        result.Should().Contain("project_init",
            "error should direct user to run project_init");
    }

    // ── plan_status tests (PROJ-05) ───────────────────────────────────────────

    [Fact]
    public async Task PlanStatus_ReturnsPhaseAndProgress()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);
        await _tools.PlanRequirements("- PROJ-01: init", CancellationToken.None);
        await _tools.PlanRoadmap("### Phase 1\nPROJ-01 tasks", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert
        result.Should().Contain("Phase:", "plan_status must include current phase");
        result.Should().Contain("Progress:", "plan_status must include progress percentage");
        result.Should().Contain("%", "plan_status progress must include percentage sign");
    }

    [Fact]
    public async Task PlanStatus_RespectsResponseCap()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_status response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task PlanStatus_WorksWithPartialState()
    {
        // Arrange — only project_init, no roadmap
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert — should return useful info, not an error
        result.Should().NotStartWith("Error:",
            "plan_status with partial state (only project:context + project:state) should return useful info");
        result.Should().NotBeNullOrEmpty("plan_status should always return a non-empty response");
    }

    [Fact]
    public async Task PlanStatus_IncludesBlockers()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build a memory server", CancellationToken.None);

        // Act
        string result = await _tools.PlanStatus(CancellationToken.None);

        // Assert
        result.Should().ContainEquivalentOf("Blockers:",
            "plan_status must include a Blockers field (even if value is 'none')");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }
}
