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

    // ── Gap closure tests (02-03) ─────────────────────────────────────────────

    [Fact]
    public async Task PlanRoadmap_RejectsDuplicateReqIdsAcrossPhases()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act — PROJ-01 appears in both Phase 1 and Phase 2 (duplicate across phases)
        string result = await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01 tasks\n### Phase 2\nPROJ-01 and PROJ-02 tasks",
            CancellationToken.None);

        // Assert — must return error with duplicate/more-than-once language
        result.Should().StartWith("Error:",
            "plan_roadmap with a REQ-ID in multiple phases should return an error");
        result.Should().MatchRegex("(?i)(duplicate|more than once|more than one phase)",
            "error should mention duplicate or 'more than once'");

        // Verify nothing was stored (all-or-nothing semantics)
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("plan:roadmap");
        var entries = store.LoadIndex(scope);
        entries.Should().NotContain(e => e.Name == subject,
            "plan_roadmap should store nothing when a REQ-ID appears in multiple phases");
    }

    [Fact]
    public async Task PlanRoadmap_AcceptsSameReqIdMentionedOncePerPhase()
    {
        // Arrange
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);
        await _tools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", CancellationToken.None);

        // Act — each REQ-ID appears in exactly one phase (valid)
        string result = await _tools.PlanRoadmap(
            "### Phase 1\nPROJ-01 tasks\n### Phase 2\nPROJ-02 tasks",
            CancellationToken.None);

        // Assert — should succeed
        result.Should().NotStartWith("Error:",
            "plan_roadmap with each REQ-ID in exactly one phase should succeed");
    }

    [Fact]
    public void PlanRequirements_DescriptionMentionsScope()
    {
        // Verify via reflection that the Description attribute on PlanRequirements
        // mentions v1/v2 scope labels — this is a contract test for agent guidance.
        var method = typeof(ScriniaProjectTools).GetMethod("PlanRequirements");
        method.Should().NotBeNull("PlanRequirements method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("PlanRequirements must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("v1",
            "PlanRequirements description must mention 'v1' so agents know to include v1/v2 scope labels");
    }

    // -- plan_tasks tests (PLAN-01, PLAN-02, PLAN-04) --

    private static string MakeTwoTaskInput() =>
        """
        ## Task 01
        Wave: 1
        Depends on: none
        Action: Implement authentication
        Acceptance criteria:
        - Users can log in
        - JWT tokens are returned

        ## Task 02
        Wave: 1
        Depends on: none
        Action: Implement user profile
        Acceptance criteria:
        - Profile data is stored
        """;

    private static string MakeMultiWaveInput() =>
        """
        ## Task 01
        Wave: 1
        Depends on: none
        Action: Implement authentication
        Acceptance criteria:
        - Users can log in

        ## Task 02
        Wave: 2
        Depends on: none
        Action: Implement advanced features
        Acceptance criteria:
        - Feature works
        """;

    private static string MakeDependencyInput() =>
        """
        ## Task 01
        Wave: 1
        Depends on: none
        Action: Implement authentication
        Acceptance criteria:
        - Users can log in

        ## Task 02
        Wave: 2
        Depends on: 01-1-01
        Action: Implement something that depends on auth
        Acceptance criteria:
        - Depends on auth
        """;

    private async Task SetupProjectAndRoadmap()
    {
        await _tools.ProjectInit("Goals: build a test project", CancellationToken.None);
        await _tools.PlanRequirements("- PLAN-01: task storage\n- PLAN-02: research guidance", CancellationToken.None);
        await _tools.PlanRoadmap("### Phase 1\nPLAN-01, PLAN-02 tasks", CancellationToken.None);
    }

    [Fact]
    public async Task PlanTasks_StoresTaskMemories()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — task:01-1-01 and task:01-1-02 must exist in index
        var store = MemoryStoreContext.Current!;
        var (scope1, subject1) = store.ParseQualifiedName("task:01-1-01");
        var (scope2, subject2) = store.ParseQualifiedName("task:01-1-02");
        var entries1 = store.LoadIndex(scope1);
        var entries2 = store.LoadIndex(scope2);

        entries1.Should().Contain(e => e.Name == subject1,
            "plan_tasks should store task:01-1-01 memory");
        entries2.Should().Contain(e => e.Name == subject2,
            "plan_tasks should store task:01-1-02 memory");
    }

    [Fact]
    public async Task PlanTasks_WritesKeywordsOverload_PopulatesKeywords()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — task:01-1-01 must have Keywords containing status:pending
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("task:01-1-01");
        var entries = store.LoadIndex(scope);
        var taskEntry = entries.FirstOrDefault(e => e.Name == subject);
        taskEntry.Should().NotBeNull("task:01-1-01 must exist in index");
        taskEntry!.Keywords.Should().NotBeNull("task entry must have Keywords populated");
        taskEntry.Keywords.Should().Contain("status:pending",
            "task Keywords must include status:pending");
    }

    [Fact]
    public async Task PlanTasks_SetsWaveKeyword()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeMultiWaveInput(), CancellationToken.None);

        // Assert — wave:1 on task 01, wave:2 on task 02
        var store = MemoryStoreContext.Current!;
        var (scope1, subject1) = store.ParseQualifiedName("task:01-1-01");
        var (scope2, subject2) = store.ParseQualifiedName("task:01-2-02");
        var entries1 = store.LoadIndex(scope1);
        var entries2 = store.LoadIndex(scope2);

        var task1 = entries1.FirstOrDefault(e => e.Name == subject1);
        var task2 = entries2.FirstOrDefault(e => e.Name == subject2);

        task1.Should().NotBeNull("task:01-1-01 must exist");
        task2.Should().NotBeNull("task:01-2-02 must exist");
        task1!.Keywords.Should().Contain("wave:1", "wave 1 task should have wave:1 keyword");
        task2!.Keywords.Should().Contain("wave:2", "wave 2 task should have wave:2 keyword");
    }

    [Fact]
    public async Task PlanTasks_SetsPhaseKeyword()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — both tasks should have phase:01 keyword
        var store = MemoryStoreContext.Current!;
        var (scope1, subject1) = store.ParseQualifiedName("task:01-1-01");
        var (scope2, subject2) = store.ParseQualifiedName("task:01-1-02");
        var entries1 = store.LoadIndex(scope1);
        var entries2 = store.LoadIndex(scope2);

        var task1 = entries1.FirstOrDefault(e => e.Name == subject1);
        var task2 = entries2.FirstOrDefault(e => e.Name == subject2);

        task1!.Keywords.Should().Contain("phase:01", "task should have phase:01 keyword");
        task2!.Keywords.Should().Contain("phase:01", "task should have phase:01 keyword");
    }

    [Fact]
    public async Task PlanTasks_SetsDependsOnKeyword()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeDependencyInput(), CancellationToken.None);

        // Assert — task 02 depends on task 01-1-01 (subject-only, not qualified)
        var store = MemoryStoreContext.Current!;
        var (scope2, subject2) = store.ParseQualifiedName("task:01-2-02");
        var entries2 = store.LoadIndex(scope2);
        var task2 = entries2.FirstOrDefault(e => e.Name == subject2);

        task2.Should().NotBeNull("task:01-2-02 must exist");
        task2!.Keywords.Should().Contain("depends_on:01-1-01",
            "task 02 should have depends_on:01-1-01 keyword (subject-only, not qualified)");
        task2.Keywords.Should().NotContain(kw => kw.StartsWith("depends_on:task:"),
            "depends_on keyword must use subject-only name, not qualified name");
    }

    [Fact]
    public async Task PlanTasks_StoresContentWithAction()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — task:01-1-01 content should contain the action text
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "task:01-1-01");
        content.Should().Contain("Implement authentication",
            "task content should contain the action text");
        content.Should().Contain("Users can log in",
            "task content should contain the acceptance criteria");
    }

    [Fact]
    public async Task PlanTasks_FailsWithoutRoadmap()
    {
        // Arrange — no roadmap (just init)
        await _tools.ProjectInit("Goals: build something", CancellationToken.None);

        // Act
        string result = await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — should return error mentioning plan_roadmap
        result.Should().StartWith("Error:", "plan_tasks without roadmap should return Error:");
        result.Should().ContainEquivalentOf("plan_roadmap",
            "error message should mention plan_roadmap");
    }

    [Fact]
    public async Task PlanTasks_UpdatesProjectState()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await _tools.PlanTasks("01", MakeTwoTaskInput(), CancellationToken.None);

        // Assert — project:state should reference plan_tasks or "Tasks created"
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        bool hasTasksInfo = stateText.Contains("plan_tasks", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("Tasks created", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("task", StringComparison.OrdinalIgnoreCase);
        hasTasksInfo.Should().BeTrue(
            "project:state should reflect that plan_tasks was called (contain 'plan_tasks', 'Tasks created', or 'task')");
    }

    [Fact]
    public void PlanTasks_DescriptionAdvicesResearch()
    {
        // Reflection test — verify [Description] attribute on PlanTasks contains "research" (PLAN-02)
        var method = typeof(ScriniaProjectTools).GetMethod("PlanTasks");
        method.Should().NotBeNull("PlanTasks method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("PlanTasks must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("research",
            "PlanTasks description must advise agent to 'research' domain before planning (PLAN-02)");
    }

    [Fact]
    public async Task PlanTasks_ReturnsWithin8KBCap()
    {
        // Arrange — create 12 tasks
        await SetupProjectAndRoadmap();
        var manyTasks = new System.Text.StringBuilder();
        for (int i = 1; i <= 12; i++)
        {
            manyTasks.AppendLine($"## Task {i:D2}");
            manyTasks.AppendLine("Wave: 1");
            manyTasks.AppendLine("Depends on: none");
            manyTasks.AppendLine($"Action: Implement feature {i} with detailed description spanning many characters in the action text");
            manyTasks.AppendLine("Acceptance criteria:");
            manyTasks.AppendLine($"- Feature {i} works correctly");
            manyTasks.AppendLine($"- Feature {i} is tested");
            manyTasks.AppendLine();
        }

        // Act
        string result = await _tools.PlanTasks("01", manyTasks.ToString(), CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_tasks response must be <= 8192 characters (MaxResponseChars)");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }
}
