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
    private readonly ScriniaMcpTools _memTools;

    public ProjectLifecycleTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── project_init tests (PROJ-01) ──────────────────────────────────────────

    [Fact]
    public async Task ProjectInit_StoresProjectContext()
    {
        // Act
        await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);

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
        string result = await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Content.Should().Contain("project:context",
            "project_init result should reference the stored project:context memory");
    }

    [Fact]
    public async Task ProjectInit_CreatesProjectState()
    {
        // Act
        await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);

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
        string result = await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);

        // Assert — result contains workspace-derived project ID (sanitized directory name)
        string expectedId = Path.GetFileName(_scope.WorkspaceDir)
            .Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        // The result must contain at least the workspace dir basename or sanitized form
        string workspaceName = Path.GetFileName(_scope.WorkspaceDir);
        var content = ResponseParser.Parse(result).Content!;
        content.Should().NotBeNullOrEmpty("result should be a non-empty string");
        // The result message should reference the project ID
        content.Should().MatchRegex(@"Initialized project '[\w\-_]+'",
            "result should contain 'Initialized project' with an ID");
    }

    [Fact]
    public async Task ProjectInit_IncludesOwnershipHint()
    {
        // Act
        string result = await ScriniaProjectTools.ProjectInit("Goals: build X", cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Content.Should().Contain(".scrinia/",
            "result should include .scrinia/ ownership hint");
    }

    // ── plan_requirements tests (PROJ-02) ─────────────────────────────────────

    [Fact]
    public async Task PlanRequirements_StoresRequirements()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build something", cancellationToken: CancellationToken.None);

        // Act
        await ScriniaProjectTools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: requirements", cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.ProjectInit("Goals: build something", cancellationToken: CancellationToken.None);

        // Act
        await ScriniaProjectTools.PlanRequirements(
            "- PROJ-01: init\n- PROJ-02: reqs", cancellationToken: CancellationToken.None);

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
        string result = await ScriniaProjectTools.PlanRequirements(
            "- PROJ-01: init", cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "plan_requirements without project_init should return an error");
        parsed.Error.Should().Contain("project_init",
            "error should mention 'project_init'");
    }

    [Fact]
    public async Task PlanRequirements_IncludesOwnershipHint()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build something", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanRequirements(
            "- PROJ-01: init", cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Content.Should().Contain(".scrinia/",
            "plan_requirements result should include .scrinia/ ownership hint");
    }

    // ── context_resume tests (PROJ-04) ──────────────────────────────────────

    [Fact]
    public async Task ContextResume_ReturnsStructuredSummary()
    {
        // Arrange — full state via init + requirements
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- PROJ-01: init\n- PROJ-02: reqs", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — all required fields present
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Project:", "context_resume must include project name");
        content.Should().Contain("Phase:", "context_resume must include current phase");
        content.Should().Contain("Progress:", "context_resume must include progress");
        content.Should().Contain("Last action:", "context_resume must include last action");
        content.Should().Contain("Next:", "context_resume must include next step");
    }

    [Fact]
    public async Task ContextResume_RespectsResponseCap()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "context_resume response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task ContextResume_IncludesNextActionSuggestion()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — must contain a concrete suggestion (tool name or action verb)
        var content = ResponseParser.Parse(result).Content ?? "";
        var instruction = ResponseParser.Parse(result).Instruction ?? "";
        string combined = content + instruction;
        bool hasConcreteAction = combined.Contains("run ") || combined.Contains("plan_")
            || combined.Contains("task_") || combined.Contains("goal_update")
            || combined.Contains("concern") || combined.Contains("research")
            || combined.Contains("memory(") || combined.Contains("task(");
        hasConcreteAction.Should().BeTrue(
            "context_resume must return a concrete next action (contains a tool name or action)");
    }

    [Fact]
    public async Task ContextResume_RebuildsFromMemories()
    {
        // Arrange — initialize project so memories exist
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server for AI agents", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- PROJ-01: init\n- PROJ-02: reqs", cancellationToken: CancellationToken.None);

        // Delete project:state artifact so rebuild is triggered
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:state");
        store.DeleteArtifact(subject, scope);
        store.Remove(subject, scope);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — rebuilt from memories prefix must be present
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error",
            "context_resume should succeed even without project:state if other memories exist");
        (parsed.Content ?? "").Should().ContainEquivalentOf("State rebuilt from memories",
            "context_resume should indicate state was rebuilt when project:state is missing");
    }

    [Fact]
    public async Task ContextResume_FailsWithoutAnyMemories()
    {
        // Act — no project memories at all
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "context_resume with no project memories should return an error");
        parsed.Error.Should().Contain("memory('remember', { path: '/project/...' })",
            "error should direct user to run memory('remember', { path: '/project/...' })");
    }

    [Fact]
    public async Task ContextResume_IncludesCheckpointWhenPresent()
    {
        // Arrange — init project, add a goal, complete it (creates checkpoint:latest)
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Goal for resume checkpoint test", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-4", "Resume checkpoint outcome", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — followUp must include checkpoint:latest (no longer inlined)
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().Contain("/checkpoint/latest",
            "context_resume followUp should include /checkpoint/latest when it exists");
    }

    [Fact]
    public async Task ContextResume_OmitsCheckpointWhenAbsent()
    {
        // Arrange — init project without completing any goals (no checkpoint:latest)
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — response must NOT contain the checkpoint section and must not error
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error",
            "context_resume should succeed without a checkpoint");
        (parsed.Content ?? "").Should().NotContain("Last checkpoint",
            "context_resume should not include 'Last checkpoint' when no checkpoint:latest exists");
    }

    // ── context_resume enrichment tests ─────────────────────────────────────

    [Fact]
    public async Task ContextResume_IncludesAgentProfile()
    {
        // Arrange — init project, store an agent:profile memory
        await ScriniaProjectTools.ProjectInit("Goals: enrich test", cancellationToken: CancellationToken.None);
        await _memTools.Store(["Memory persistence: always use scrinia"], "agent:profile",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — followUp must include agent:profile (no longer inlined)
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().Contain("/agent/profile",
            "context_resume followUp should include /agent/profile when it exists");
    }

    [Fact]
    public async Task ContextResume_IncludesActiveGoalDescription()
    {
        // Arrange — init project and add a goal (which becomes active)
        await ScriniaProjectTools.ProjectInit("Goals: enrich test", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Build the authentication system", null, null,
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — response should mention the active goal
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Active goal",
            "context_resume should include 'Active goal:' when a goal is active");
        content.Should().Contain("authentication",
            "context_resume should include the goal description text");
    }

    [Fact]
    public async Task ContextResume_IncludesSessionLog()
    {
        // Arrange — init project, store a session log for today
        await ScriniaProjectTools.ProjectInit("Goals: enrich test", cancellationToken: CancellationToken.None);
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        await _memTools.Store(["- Completed feature X"], $"sessions:{today}",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — followUp must include today's session (no longer inlined)
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().Contain($"/sessions/{today}",
            "context_resume followUp should include today's session log when it exists");
    }

    [Fact]
    public async Task ContextResume_IncludesTaskNudge()
    {
        // Arrange — full lifecycle: init, requirements, tasks
        // PlanTasks updates project:state to include "Phase 01" which the nudge regex needs,
        // and creates pending tasks that trigger the task('next') nudge.
        await ScriniaProjectTools.ProjectInit("Goals: task nudge test", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- REQ-01: Feature A", cancellationToken: CancellationToken.None);
        string taskInput =
            "## Task 01\nWave: 1\nDepends on: none\nAction: Implement feature\nAcceptance criteria:\n- done";
        await ScriniaProjectTools.PlanTasks("01", taskInput, cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — response should nudge agent to call task('next')
        var parsed = ResponseParser.Parse(result);
        string combined = (parsed.Content ?? "") + (parsed.Instruction ?? "");
        combined.Should().Contain("task('next',",
            "context_resume should include a task('next') nudge with path when pending tasks exist");
    }

    [Fact]
    public async Task ContextResume_OmitsEnrichmentsWhenAbsent()
    {
        // Arrange — init project only (no agent:profile, no session log, no goals, no tasks)
        await ScriniaProjectTools.ProjectInit("Goals: bare minimum project", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — none of the enrichment sections should appear
        var parsed = ResponseParser.Parse(result);
        string combined = (parsed.Content ?? "") + (parsed.Instruction ?? "");
        combined.Should().NotContain("Agent profile",
            "context_resume should not include 'Agent profile' when no agent:profile exists");
        combined.Should().NotContain("Session log",
            "context_resume should not include 'Session log' when no session log exists for today");
        combined.Should().NotContain("Active goal",
            "context_resume should not include 'Active goal' when no goal is active");
        combined.Should().NotContain("task('next')",
            "context_resume should not include task('next') nudge when no pending tasks exist");
    }

    // ── plan_status tests (PROJ-05) ───────────────────────────────────────────

    [Fact]
    public async Task PlanStatus_ReturnsPhaseAndProgress()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- PROJ-01: init", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Phase:", "plan_status must include current phase");
        content.Should().Contain("Progress:", "plan_status must include progress percentage");
        content.Should().Contain("%", "plan_status progress must include percentage sign");
    }

    [Fact]
    public async Task PlanStatus_RespectsResponseCap()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_status response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task PlanStatus_WorksWithPartialState()
    {
        // Arrange — only project_init, no roadmap
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — should return useful info, not an error
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "plan_status with partial state (only project:context + project:state) should return useful info");
        result.Should().NotBeNullOrEmpty("plan_status should always return a non-empty response");
    }

    [Fact]
    public async Task PlanStatus_IncludesBlockers()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build a memory server", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Content.Should().ContainEquivalentOf("Blockers:",
            "plan_status must include a Blockers field (even if value is 'none')");
    }

    [Fact]
    public void PlanRequirements_DescriptionMentionsScope()
    {
        // After consolidation, PlanRequirements is an internal method called by RequirementDispatch.
        // Verify the method exists and still has a parameter-level [Description] mentioning v1/v2 scope.
        var method = typeof(ScriniaProjectTools).GetMethod("PlanRequirements",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("PlanRequirements must exist as an internal method");

        // Check parameter-level [Description] on the 'requirements' parameter for v1 scope guidance
        var reqParam = method!.GetParameters().FirstOrDefault(p => p.Name == "requirements");
        reqParam.Should().NotBeNull("PlanRequirements must have a 'requirements' parameter");

        var descAttr = reqParam!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("PlanRequirements 'requirements' parameter must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("v1",
            "PlanRequirements parameter description must mention 'v1' so agents know to include v1/v2 scope labels");
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
        Depends on: none
        Action: Implement authentication
        Acceptance criteria:
        - Users can log in

        ## Task 02
        Depends on: 01
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
        await ScriniaProjectTools.ProjectInit("Goals: build a test project", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- PLAN-01: task storage\n- PLAN-02: research guidance", cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task PlanTasks_StoresTaskMemories()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.PlanTasks("01", MakeMultiWaveInput(), cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.PlanTasks("01", MakeDependencyInput(), cancellationToken: CancellationToken.None);

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
        await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

        // Assert — task:01-1-01 content should contain the action text
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "task:01-1-01");
        content.Should().Contain("Implement authentication",
            "task content should contain the action text");
        content.Should().Contain("Users can log in",
            "task content should contain the acceptance criteria");
    }

    [Fact]
    public async Task PlanTasks_SucceedsWithoutRoadmap()
    {
        // Arrange — no roadmap (just init)
        await ScriniaProjectTools.ProjectInit("Goals: build something", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

        // Assert — should succeed (roadmap is no longer a prerequisite)
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error", "plan_tasks should succeed without a roadmap");
        parsed.Content.Should().Contain("Created", "plan_tasks should confirm task creation");
    }

    [Fact]
    public async Task PlanTasks_UpdatesProjectState()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanTasks("01", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

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
        // PlanTasks is an internal method routed through TaskDispatch("plan").
        var method = typeof(ScriniaProjectTools).GetMethod("PlanTasks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("PlanTasks must exist as an internal method");

        // TaskDispatch is the public entry point for plan operations
        var dispatcher = typeof(ScriniaProjectTools).GetMethod("TaskDispatch",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        dispatcher.Should().NotBeNull("TaskDispatch must exist as the task routing entry point");
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
        string result = await ScriniaProjectTools.PlanTasks("01", manyTasks.ToString(), cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_tasks response must be <= 8192 characters (MaxResponseChars)");
    }

    // -- task_next tests (EXEC-01, EXEC-03) --

    private async Task SetupProjectWithTasks(string phaseId, string tasksInput)
    {
        await SetupProjectAndRoadmap();
        await ScriniaProjectTools.PlanTasks(phaseId, tasksInput, cancellationToken: CancellationToken.None);
    }

    private static string MakeThreeTaskInput() =>
        """
        ## Task 01
        Wave: 1
        Depends on: none
        Action: Implement authentication
        Acceptance criteria:
        - Users can log in

        ## Task 02
        Wave: 1
        Depends on: none
        Action: Implement user profile
        Acceptance criteria:
        - Profile data is stored

        ## Task 03
        Wave: 1
        Depends on: none
        Action: Implement dashboard
        Acceptance criteria:
        - Dashboard renders
        """;

    private static string MakeWaveDependencyInput() =>
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
        Action: Implement something requiring auth
        Acceptance criteria:
        - Requires auth
        """;

    [Fact]
    public async Task TaskNext_ReturnsUnblockedTasks()
    {
        // Arrange - 2 wave-1 tasks with no dependencies
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - result should contain both task names
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("01-1-01", "task_next should include first task");
        content.Should().Contain("01-1-02", "task_next should include second task");
    }

    [Fact]
    public async Task TaskNext_ReturnsAllInWave()
    {
        // Arrange - 3 wave-1 tasks
        await SetupProjectWithTasks("01", MakeThreeTaskInput());

        // Act
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - ALL 3 should appear (EXEC-03: returns batch, not single)
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("01-1-01", "task_next should include task 01");
        content.Should().Contain("01-1-02", "task_next should include task 02");
        content.Should().Contain("01-1-03", "task_next should include task 03");
    }

    [Fact]
    public async Task TaskNext_SkipsBlockedTasks()
    {
        // Arrange - wave 1: task A (no deps), wave 2: task B (depends on A)
        await SetupProjectWithTasks("01", MakeWaveDependencyInput());

        // Act - call task_next for phase 01
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - only wave-1 task should appear; wave-2 task should not
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("01-1-01", "wave-1 unblocked task should appear");
        content.Should().NotContain("01-2-02", "wave-2 blocked task should NOT appear");
    }

    [Fact]
    public async Task TaskNext_SkipsCompletedTasks()
    {
        // Arrange - 2 wave-1 tasks; complete task 1
        await SetupProjectWithTasks("01", MakeTwoTaskInput());
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Completed authentication", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - only task 2 should appear; task 1 is complete
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("01-1-02", "incomplete task 02 should appear");
        content.Should().NotContain("01-1-01", "completed task 01 should NOT appear in pending list");
    }

    [Fact]
    public async Task TaskNext_ReturnsEmptyWhenAllComplete()
    {
        // Arrange - 2 user tasks + auto-injected gate tasks; complete them all
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Provide gate artifacts so gate validation passes
        await _memTools.Store(content: ["## QA Report\nAll pass"], name: "qa:latest", cancellationToken: CancellationToken.None);
        await _memTools.Store(content: ["## Retro\nLessons learned"], name: "learn:retro-01", cancellationToken: CancellationToken.None);
        await _memTools.Store(content: ["## Evolutionary scan complete"], name: "sessions:evolutionary-g0", cancellationToken: CancellationToken.None);
        await _memTools.Store(content: ["## Cartography report"], name: "cartography:2026-01-01", cancellationToken: CancellationToken.None);

        // Create docs/reports/ with a march report so march-gate validation passes
        var store = MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        string workspaceRoot = Path.GetDirectoryName(scriniaDir) ?? scriniaDir;
        string reportsDir = Path.Combine(workspaceRoot, "docs", "reports");
        Directory.CreateDirectory(reportsDir);
        string marchReportPath = Path.Combine(reportsDir, "march-report.md");
        await File.WriteAllTextAsync(marchReportPath, "# March Report\nGoal complete.");
        File.Exists(marchReportPath).Should().BeTrue($"march report should exist at {marchReportPath}");

        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);

        // Discover and complete all gate tasks dynamically (wave numbers may vary)
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allEntries = store.LoadIndex(taskScope);
        var gateOrder = new[] { "qa-gate", "self-reflector-gate", "evolutionary-gate", "cartographer-gate", "march-gate" };
        foreach (var gateSuffix in gateOrder)
        {
            var entry = allEntries.FirstOrDefault(e => e.Name.Contains(gateSuffix));
            entry.Should().NotBeNull($"{gateSuffix} task should exist");
            var gr = await ScriniaProjectTools.TaskComplete($"task:{entry!.Name}", "Done", cancellationToken: CancellationToken.None);
            ResponseParser.Parse(gr).Status.Should().Be("success",
                $"{gateSuffix} should complete: {ResponseParser.Parse(gr).Error}");
        }

        // Act
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - should indicate no pending tasks
        ResponseParser.Parse(result).Content.Should().ContainEquivalentOf("no pending", "result should indicate no pending tasks when all complete");
    }

    [Fact]
    public async Task TaskNext_FiltersCorrectPhase()
    {
        // Arrange - create tasks for phase "01"; create tasks for a "02" project
        await SetupProjectWithTasks("01", MakeTwoTaskInput());
        // Add phase 02 tasks too
        await ScriniaProjectTools.PlanTasks("02", MakeTwoTaskInput(), cancellationToken: CancellationToken.None);

        // Act - only request phase 01
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - only phase-01 tasks should appear
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("01-1-", "phase-01 tasks should appear");
        // Phase-02 tasks should not dominate the result (task names contain phase identifier)
        // Phase 02 tasks are named with "02-1-01", "02-1-02"
        content.Should().NotContain("02-1-01", "phase-02 tasks should NOT appear when filtering for phase 01");
    }

    [Fact]
    public async Task TaskNext_FailsWithoutProject()
    {
        // Act - call task_next without any project setup
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert - should return an error or "no pending tasks"
        var parsed = ResponseParser.Parse(result);
        bool isError = parsed.Status == "error"
            || (parsed.Content ?? "").Contains("no pending", StringComparison.OrdinalIgnoreCase);
        isError.Should().BeTrue("task_next without any project should return error or no-pending response");
    }

    [Fact]
    public async Task TaskNext_RespectsResponseCap()
    {
        // Arrange - create many tasks
        await SetupProjectAndRoadmap();
        var manyTasks = new System.Text.StringBuilder();
        for (int i = 1; i <= 20; i++)
        {
            manyTasks.AppendLine($"## Task {i:D2}");
            manyTasks.AppendLine("Wave: 1");
            manyTasks.AppendLine("Depends on: none");
            manyTasks.AppendLine($"Action: Implement feature {i} with a very long description that adds many characters to test the 8KB cap");
            manyTasks.AppendLine("Acceptance criteria:");
            manyTasks.AppendLine($"- Feature {i} works correctly with many detailed criteria spanning multiple lines");
            manyTasks.AppendLine();
        }
        await ScriniaProjectTools.PlanTasks("01", manyTasks.ToString(), cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "task_next response must be <= 8192 characters (MaxResponseChars)");
    }

    // -- task_complete tests (EXEC-02, EXEC-04) --

    [Fact]
    public async Task TaskComplete_UpdatesStatusKeyword()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Completed authentication", cancellationToken: CancellationToken.None);

        // Assert - entry should have status:complete, NOT status:pending
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-1-01");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "01-1-01");

        entry.Should().NotBeNull("task entry should still exist after task_complete");
        entry!.Keywords.Should().Contain("status:complete",
            "task_complete should update keyword to status:complete");
        entry.Keywords.Should().NotContain("status:pending",
            "task_complete should remove status:pending keyword");
    }

    [Fact]
    public async Task TaskComplete_PreservesOtherKeywords()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);

        // Assert - wave, phase keywords still present
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-1-01");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "01-1-01");

        entry!.Keywords.Should().Contain("wave:1",
            "wave keyword should be preserved after task_complete");
        entry.Keywords.Should().Contain("phase:01",
            "phase keyword should be preserved after task_complete");
    }

    [Fact]
    public async Task TaskComplete_CreatesExecutionLog()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Completed authentication", cancellationToken: CancellationToken.None);

        // Assert - task:01-execution-log memory must exist
        var store = MemoryStoreContext.Current!;
        var (logScope, logSubject) = store.ParseQualifiedName("task:01-execution-log");
        var logEntries = store.LoadIndex(logScope);
        logEntries.Should().Contain(e => e.Name == logSubject,
            "task_complete should create task:{phaseId}-execution-log memory");
    }

    [Fact]
    public async Task TaskComplete_AppendsToExistingLog()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act - complete two different tasks
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Completed first task", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Completed second task", cancellationToken: CancellationToken.None);

        // Assert - execution log should have 2 chunks
        var store = MemoryStoreContext.Current!;
        var (logScope, logSubject) = store.ParseQualifiedName("task:01-execution-log");
        string logArtifact = await store.ReadArtifactAsync(logSubject, logScope);
        int chunkCount = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.GetChunkCount(logArtifact);
        chunkCount.Should().Be(2,
            "task_complete called twice should produce an execution log with 2 chunks");
    }

    [Fact]
    public async Task TaskComplete_LogContainsOutcome()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Fixed the parser bug", cancellationToken: CancellationToken.None);

        // Assert - reading the log should contain the outcome text
        var store = MemoryStoreContext.Current!;
        string logText = await ReadMemoryText(store, "task:01-execution-log");
        logText.Should().Contain("Fixed the parser bug",
            "execution log should contain the outcome text passed to task_complete");
    }

    [Fact]
    public async Task TaskComplete_LogContainsTimestamp()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Some outcome", cancellationToken: CancellationToken.None);

        // Assert - log should contain ISO timestamp pattern
        var store = MemoryStoreContext.Current!;
        string logText = await ReadMemoryText(store, "task:01-execution-log");
        // ISO 8601 timestamp pattern: YYYY-MM-DDTHH:MM:SS
        logText.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}",
            "execution log should contain an ISO 8601 timestamp");
    }

    [Fact]
    public async Task TaskComplete_FailsForUnknownTask()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        string result = await ScriniaProjectTools.TaskComplete("task:99-9-99", "Done", cancellationToken: CancellationToken.None);

        // Assert - should return error for unknown task
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "task_complete with unknown task name should return an error");
        parsed.Error.Should().Contain("not found",
            "error should mention 'not found'");
    }

    [Fact]
    public async Task TaskComplete_DoesNotArchive()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Get the versions directory path for task scope
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("task:01-1-01");
        string storeDir = store.GetStoreDirForScope(scope);
        string versionsDir = Path.Combine(Path.GetDirectoryName(storeDir)!, "versions",
            Path.GetFileName(storeDir)!);

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);

        // Assert - no version files should be created
        bool versionsExist = Directory.Exists(versionsDir) &&
            Directory.GetFiles(versionsDir, "01-1-01*").Length > 0;
        versionsExist.Should().BeFalse(
            "task_complete should NOT archive versions — only updates keywords in place");
    }

    [Fact]
    public async Task TaskComplete_UpdatesProjectState()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Completed authentication", cancellationToken: CancellationToken.None);

        // Assert - project:state should reference the completed task
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        stateText.Should().ContainEquivalentOf("01-1-01",
            "project:state last action should reference the completed task");
    }

    [Fact]
    public async Task TaskComplete_RespectsResponseCap()
    {
        // Arrange
        await SetupProjectWithTasks("01", MakeTwoTaskInput());

        // Act
        string result = await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "task_complete response must be <= 8192 characters (MaxResponseChars)");
    }

    // -- plan_verify tests (PLAN-03, VERI-01, VERI-02) --

    [Fact]
    public async Task PlanVerify_ReturnsVerificationChecklist()
    {
        // Arrange — full lifecycle with criteria tasks + all tasks complete
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Implemented auth", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Implemented profile", cancellationToken: CancellationToken.None);

        // Act — no evidence → checklist mode
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — must contain the verification checklist header
        ResponseParser.Parse(result).Content.Should().Contain("Verification Checklist",
            "plan_verify without evidence returns a checklist showing criteria to verify");
    }

    [Fact]
    public async Task PlanVerify_ChecksAllCriteriaInPhase()
    {
        // Arrange — roadmap with 2 success criteria
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — both requirement criteria should appear in output
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("CRIT-01",
            "plan_verify should include the CRIT-01 requirement criterion");
        content.Should().Contain("CRIT-02",
            "plan_verify should include the CRIT-02 requirement criterion");
    }

    [Fact]
    public async Task PlanVerify_WithoutEvidence_ReturnsChecklist()
    {
        // Arrange — setup tasks but do NOT complete them
        await SetupProjectWithCriteria("01");

        // Act — call without evidence to get checklist
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — returns a verification checklist, not auto-evaluated results
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Verification Checklist",
            "plan_verify without evidence should return a checklist");
        content.Should().Contain("[ ]",
            "checklist items should be unchecked");
    }

    [Fact]
    public async Task PlanVerify_PassesWhenAllTasksComplete()
    {
        // Arrange — complete both tasks
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done auth", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done profile", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — checklist mode (no evidence) should show task completion and QA gate guidance
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Verification Checklist",
            "plan_verify should return a verification checklist when called without evidence");
        content.Should().Contain("QA gate",
            "plan_verify checklist should reference the QA gate for structured verification");
    }

    [Fact]
    public async Task PlanVerify_WithEvidence_RecordsResults()
    {
        // Arrange
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);

        // Write qa:latest so the QA gate passes
        await _memTools.Store(["## QA Report\nBuild: 0 errors\nTests: 8 passed, 0 failed"],
            "qa:latest", cancellationToken: CancellationToken.None);

        // Act — provide agent-verified evidence (include test output to pass QA gate)
        string result = await ScriniaProjectTools.PlanVerify("01",
            evidence: "PASS: Auth endpoint created — 5 passed, 0 failed\nPASS: Profile endpoint created — 3 passed, 0 failed",
            cancellationToken: CancellationToken.None);

        // Assert — must contain evidence strings from the agent
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("Evidence:",
            "plan_verify with evidence must include Evidence: strings for each criterion");
        content.Should().Contain("ALL_PASS");
    }

    [Fact]
    public async Task PlanVerify_ScopesToTargetPhase()
    {
        // Arrange — requirements for two phases; tasks in phase 01 reference SCOPE-01 only,
        // tasks in phase 02 reference SCOPE-02 only. plan_verify("01") should scope to phase 01.
        await ScriniaProjectTools.ProjectInit("Goals: test", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- SCOPE-01: phase one req\n- SCOPE-02: phase two req", cancellationToken: CancellationToken.None);
        // Phase 01 task references SCOPE-01
        await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: Implement SCOPE-01 — phase one work\nAcceptance criteria:\n- phase one done",
            cancellationToken: CancellationToken.None);
        // Phase 02 task references SCOPE-02
        await ScriniaProjectTools.PlanTasks("02",
            "## Task 01\nWave: 1\nDepends on: none\nAction: Implement SCOPE-02 — phase two work\nAcceptance criteria:\n- phase two done",
            cancellationToken: CancellationToken.None);

        // Act — verify only phase 01
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — only SCOPE-01 should appear; SCOPE-02 must NOT appear
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("SCOPE-01",
            "plan_verify('01') should include phase 01 criteria (SCOPE-01)");
        content.Should().NotContain("SCOPE-02",
            "plan_verify('01') must NOT include phase 02 criteria (SCOPE-02)");
    }

    [Fact]
    public async Task PlanVerify_FailsWithoutRequirements()
    {
        // Arrange — only init, no requirements
        await ScriniaProjectTools.ProjectInit("Goals: test", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Status.Should().Be("error",
            "plan_verify without requirements should return an error");
    }

    [Fact]
    public async Task PlanVerify_RespectsResponseCap()
    {
        // Arrange
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_verify response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task PlanVerify_WorksBeforeExecution()
    {
        // Arrange — requirements + plan_tasks but NO task_complete calls
        await SetupProjectWithCriteria("01");
        // No task_complete calls — testing pre-execution quality check (PLAN-03)

        // Act
        string result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — should return checklist (no evidence), not an error
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error",
            "plan_verify before execution should not return an error");
        parsed.Content.Should().Contain("Verification Checklist",
            "plan_verify before execution should return a verification checklist");
    }

    [Fact]
    public async Task PlanVerify_RecordsEvidenceWithoutQaLatest()
    {
        // Arrange — full lifecycle with roadmap + tasks completed, no qa:latest needed
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);

        // Act — provide evidence (QA gate removed — no qa:latest required)
        string result = await ScriniaProjectTools.PlanVerify("01",
            evidence: "PASS: criterion 1 — I verified it looks good",
            cancellationToken: CancellationToken.None);

        // Assert — should record evidence without blocking
        var content = ResponseParser.Parse(result).Content!;
        content.Should().NotStartWith("Blocked:",
            "plan_verify should not block since QA gate was removed");
        content.Should().MatchRegex("(ALL_PASS|PARTIAL|Status:)",
            "plan_verify should return structured results");
    }

    // -- plan_gaps tests (VERI-03) --

    [Fact]
    public async Task PlanGaps_CreatesGapTasks()
    {
        // Arrange — phase with incomplete tasks (plan_verify would show failures)
        await SetupProjectWithCriteria("01");
        string failedCriteria = "All tasks must be complete";

        // Act
        await ScriniaProjectTools.PlanGaps("01", failedCriteria, cancellationToken: CancellationToken.None);

        // Assert — gap task memory must exist in index
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-gap-01");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == "01-gap-01",
            "plan_gaps should create task:01-gap-01 in index");
    }

    [Fact]
    public async Task PlanGaps_GapTaskHasGapClosureKeyword()
    {
        // Arrange
        await SetupProjectWithCriteria("01");

        // Act
        await ScriniaProjectTools.PlanGaps("01", "Failed criterion", cancellationToken: CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-gap-01");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "01-gap-01");

        entry.Should().NotBeNull("gap task should exist");
        entry!.Keywords.Should().Contain("gap_closure:true",
            "gap task must have gap_closure:true keyword");
    }

    [Fact]
    public async Task PlanGaps_GapTaskHasStatusPending()
    {
        // Arrange
        await SetupProjectWithCriteria("01");

        // Act
        await ScriniaProjectTools.PlanGaps("01", "Failed criterion", cancellationToken: CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-gap-01");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "01-gap-01");

        entry!.Keywords.Should().Contain("status:pending",
            "gap task must have status:pending keyword");
    }

    [Fact]
    public async Task PlanGaps_GapTaskHasCorrectPhase()
    {
        // Arrange
        await SetupProjectWithCriteria("01");

        // Act
        await ScriniaProjectTools.PlanGaps("01", "Failed criterion", cancellationToken: CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-gap-01");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == "01-gap-01");

        entry!.Keywords.Should().Contain("phase:01",
            "gap task must have phase:01 keyword");
    }

    [Fact]
    public async Task PlanGaps_ReopensPhaseStatus()
    {
        // Arrange
        await SetupProjectWithCriteria("01");

        // Act
        await ScriniaProjectTools.PlanGaps("01", "Failed criterion", cancellationToken: CancellationToken.None);

        // Assert — project:state should indicate phase was re-opened
        var store = MemoryStoreContext.Current!;
        string stateText = await ReadMemoryText(store, "project:state");
        stateText.Should().ContainEquivalentOf("re-open",
            "project:state should reflect phase re-opened for gap closure");
    }

    [Fact]
    public async Task PlanGaps_CreatesMultipleGapTasks()
    {
        // Arrange
        await SetupProjectWithCriteria("01");
        string threeCriteria = "Criterion one\nCriterion two\nCriterion three";

        // Act
        await ScriniaProjectTools.PlanGaps("01", threeCriteria, cancellationToken: CancellationToken.None);

        // Assert — three gap tasks: gap-01, gap-02, gap-03
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("task:01-gap-01");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == "01-gap-01", "gap-01 should exist");
        entries.Should().Contain(e => e.Name == "01-gap-02", "gap-02 should exist");
        entries.Should().Contain(e => e.Name == "01-gap-03", "gap-03 should exist");
    }

    [Fact]
    public async Task PlanGaps_GapTaskContentIncludesCriterion()
    {
        // Arrange
        await SetupProjectWithCriteria("01");
        string criterion = "Tasks must produce verified output artifacts";

        // Act
        await ScriniaProjectTools.PlanGaps("01", criterion, cancellationToken: CancellationToken.None);

        // Assert — gap task content contains criterion text
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "task:01-gap-01");
        content.Should().Contain(criterion,
            "gap task content must include the failed criterion text");
    }

    [Fact]
    public async Task PlanGaps_FailsWithoutProject()
    {
        // Arrange — no project_init called (fresh scope)
        // No setup at all

        // Act
        string result = await ScriniaProjectTools.PlanGaps("01", "Some criterion", cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Status.Should().Be("error",
            "plan_gaps without project should return an error");
    }

    [Fact]
    public async Task PlanGaps_RespectsResponseCap()
    {
        // Arrange
        await SetupProjectWithCriteria("01");

        // Act
        string result = await ScriniaProjectTools.PlanGaps("01", "Failed criterion", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_gaps response must be <= 8192 characters (MaxResponseChars)");
    }

    // -- plan_retrospective tests (LEARN-01, LEARN-03, LEARN-04) --

    [Fact]
    public async Task PlanRetrospective_StoresLearnMemory()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Nothing failed", "Write tests first", cancellationToken: CancellationToken.None);

        // Assert — per-phase retro file must exist in learn scope
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("learn:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name.StartsWith("retro-g") && e.Name.EndsWith("-01"),
            "plan_retrospective should store a per-phase learn:retro-g*-01 memory");
    }

    [Fact]
    public async Task PlanRetrospective_ContentContainsSections()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed quickly", "Build was slow", "Use incremental builds", cancellationToken: CancellationToken.None);

        // Assert — content must contain all required section headers
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "learn:retro-g0-01");
        content.Should().Contain("## What Worked", "retrospective content must include '## What Worked' section");
        content.Should().Contain("## What Failed", "retrospective content must include '## What Failed' section");
        content.Should().Contain("## Lessons", "retrospective content must include '## Lessons' section");
        content.Should().Contain("## Provenance", "retrospective content must include '## Provenance' section");
    }

    [Fact]
    public async Task PlanRetrospective_HasProvenanceKeyword()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Nothing failed", "Write tests first", cancellationToken: CancellationToken.None);

        // Assert — index entry Keywords must contain "provenance:agent"
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("learn:placeholder");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e => e.Name.StartsWith("retro-g") && e.Name.EndsWith("-01"));
        entry.Keywords.Should().Contain("provenance:agent",
            "per-phase retro must have provenance:agent keyword");
    }

    [Fact]
    public async Task PlanRetrospective_AccumulatesChunks()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act — two calls for different phases produce separate files
        await ScriniaProjectTools.PlanRetrospective("01", "Worked well", "Minor issues", "Lessons from 01", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRetrospective("02", "Worked well", "Some failures", "Lessons from 02", cancellationToken: CancellationToken.None);

        // Assert — each phase gets its own memory file
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("learn:placeholder");
        var entries = store.LoadIndex(scope);
        var retroEntries = entries.Where(e => e.Name.StartsWith("retro-g")).ToList();
        retroEntries.Should().HaveCount(2,
            "two plan_retrospective calls for different phases should produce two separate retro files");
    }

    [Fact]
    public async Task PlanRetrospective_ContainsPhaseId()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Build slow", "Speed up pipeline", cancellationToken: CancellationToken.None);

        // Assert — content must reference phase ID
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "learn:retro-g0-01");
        content.Should().ContainAny("Phase 01", "## Phase 01 Retrospective",
            "retrospective content must reference the phase ID '01'");
    }

    [Fact]
    public async Task PlanRetrospective_ContainsTimestamp()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Build slow", "Speed up pipeline", cancellationToken: CancellationToken.None);

        // Assert — content must contain ISO 8601 date pattern YYYY-MM-DD
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "learn:retro-g0-01");
        content.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}",
            "retrospective content must contain an ISO 8601 date pattern");
    }

    [Fact]
    public async Task PlanRetrospective_RespectsResponseCap()
    {
        // Arrange
        await SetupProjectAndRoadmap();

        // Act
        string result = await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Build slow", "Speed up pipeline", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_retrospective response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task PlanRetrospective_SearchableViaStandardSearch()
    {
        // Arrange
        await SetupProjectAndRoadmap();
        await ScriniaProjectTools.PlanRetrospective("01", "Tests passed", "Build slow", "Speed up pipeline", cancellationToken: CancellationToken.None);

        // Act — search via standard search with no excludeTopics
        var store = MemoryStoreContext.Current!;
        var results = store.SearchAll("retrospective", scopes: null, limit: 10);

        // Assert — per-phase retro file should appear in results
        bool found = results
            .OfType<Scrinia.Core.Search.EntryResult>()
            .Any(er => er.Item.Entry.Name.StartsWith("retro-g"));
        found.Should().BeTrue(
            "per-phase retro file must be discoverable via standard search for 'retrospective'");
    }

    // -- plan_profile tests (LEARN-02) --

    [Fact]
    public async Task PlanProfile_StoresUserProfile()
    {
        // Act — no project_init required; user preferences are project-independent
        await _tools.PlanProfile("autonomy_level: high\nreview_depth: detailed", cancellationToken: CancellationToken.None);

        // Assert — .scrinia/agent/profile.md must exist on disk
        string mdPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "profile.md");
        File.Exists(mdPath).Should().BeTrue(
            "plan_profile should store agent:profile as .scrinia/agent/profile.md");
    }

    [Fact]
    public async Task PlanProfile_ContentContainsPreferences()
    {
        // Act
        await _tools.PlanProfile("autonomy_level: high\nreview_depth: detailed", cancellationToken: CancellationToken.None);

        // Assert — .md file content must contain the preference text
        string mdPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "profile.md");
        string content = await File.ReadAllTextAsync(mdPath);
        content.Should().Contain("autonomy_level: high",
            "agent:profile content must contain 'autonomy_level: high'");
        content.Should().Contain("review_depth: detailed",
            "agent:profile content must contain 'review_depth: detailed'");
    }

    [Fact]
    public async Task PlanProfile_HasSidecarMeta()
    {
        // Act
        await _tools.PlanProfile("autonomy_level: high", cancellationToken: CancellationToken.None);

        // Assert — sidecar .meta.json must exist with timestamps
        string metaPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "profile.meta.json");
        File.Exists(metaPath).Should().BeTrue(
            "agent:profile should have a .meta.json sidecar file");
        string metaJson = await File.ReadAllTextAsync(metaPath);
        metaJson.Should().Contain("createdAt",
            "sidecar metadata must have a createdAt field");
    }

    [Fact]
    public async Task PlanProfile_OverwritesOnSecondCall()
    {
        // Act — two calls with different content
        await _tools.PlanProfile("autonomy_level: high", cancellationToken: CancellationToken.None);
        await _tools.PlanProfile("autonomy_level: low\nreview_depth: minimal", cancellationToken: CancellationToken.None);

        // Assert — .md file should contain only the second call's content
        string mdPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "profile.md");
        string content = await File.ReadAllTextAsync(mdPath);
        content.Should().Contain("autonomy_level: low",
            "agent:profile should contain content from the second call (overwrite semantics)");
    }

    [Fact]
    public async Task PlanProfile_ArchivesPreviousVersion()
    {
        // Act — two calls with different content
        await _tools.PlanProfile("autonomy_level: high", cancellationToken: CancellationToken.None);
        await _tools.PlanProfile("autonomy_level: low\nreview_depth: minimal", cancellationToken: CancellationToken.None);

        // Assert — .md file should contain content from the second call only
        string mdPath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "profile.md");
        string content = await File.ReadAllTextAsync(mdPath);
        content.Should().Contain("autonomy_level: low",
            "agent:profile should contain content from the second call (overwrite semantics)");
        content.Should().NotContain("autonomy_level: high",
            "agent:profile must not contain content from first call");

        // Assert — previous version should be archived
        string versionsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent", "versions");
        Directory.Exists(versionsDir).Should().BeTrue(
            "previous version should be archived in versions directory");
        Directory.GetFiles(versionsDir, "profile_*.md").Should().HaveCount(1,
            "exactly one archived version expected after two writes");
    }

    [Fact]
    public async Task PlanProfile_RespectsResponseCap()
    {
        // Act
        string result = await _tools.PlanProfile("autonomy_level: high\nreview_depth: detailed", cancellationToken: CancellationToken.None);

        // Assert
        result.Length.Should().BeLessOrEqualTo(8192,
            "plan_profile response must be <= 8192 characters (MaxResponseChars)");
    }

    [Fact]
    public async Task Guide_MentionsLearningMemories()
    {
        // Arrange — Guide() does not access store but use scope for safety
        var mcpTools = new ScriniaMcpTools();

        // Act
        string result = await mcpTools.Guide(CancellationToken.None);

        // Assert — must mention learning memory topics and self-reflector skill
        var content = ResponseParser.Parse(result).Content!;
        content.Should().Contain("/learn/",
            "guide() must mention /learn/ reserved path");
        content.Should().Contain("/agent/",
            "guide() must mention /agent/ reserved path for behavioral norms");
    }

    // ── Gate tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanVerify_RejectsIfOpenConcerns()
    {
        // Arrange — full lifecycle with roadmap + tasks completed + open high concern
        await SetupProjectWithCriteria("01");
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.TaskComplete("task:01-1-02", "Done", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: auth bypass vulnerability",
            "high", "01", id: "auth-bypass", CancellationToken.None);

        // Write qa:latest so the QA gate passes (testing concern gate, not QA gate)
        await _memTools.Store(["## QA Report\nBuild: 0 errors\nTests: 42 passed, 0 failed"],
            "qa:latest", cancellationToken: CancellationToken.None);

        // Act — provide valid test evidence (would pass QA gate)
        string result = await ScriniaProjectTools.PlanVerify("01",
            evidence: "PASS: criterion 1 — 42 passed, 0 failed, build clean",
            cancellationToken: CancellationToken.None);

        // Assert — should hard reject due to open concern
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "plan_verify should reject when open high-severity concerns exist for the phase");
        parsed.Error.Should().Contain("auth-bypass",
            "rejection message should mention the open concern name");
    }

    [Fact]
    public async Task ConcernResolve_RequiresVerifiedBy()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: test verifiedBy validation", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.ConcernAdd("Risk: test concern",
            "medium", "01", id: "vb-test", CancellationToken.None);

        // Act — try with invalid verifiedBy value
        string errorResult = await ScriniaProjectTools.ConcernResolve("concern:vb-test",
            "Fixed it", verifiedBy: "invalid", CancellationToken.None);

        // Assert — should reject invalid verifiedBy
        var errorParsed = ResponseParser.Parse(errorResult);
        errorParsed.Status.Should().Be("error",
            "concern_resolve should reject invalid verifiedBy value");
        errorParsed.Error.Should().Contain("invalid",
            "error message should mention the invalid value");

        // Act — resolve with valid verifiedBy
        string successResult = await ScriniaProjectTools.ConcernResolve("concern:vb-test",
            "Fixed it properly", verifiedBy: "qa", CancellationToken.None);

        // Assert — should succeed
        var successParsed = ResponseParser.Parse(successResult);
        successParsed.Status.Should().NotBe("error",
            "concern_resolve should succeed with verifiedBy='qa'");
        successParsed.Content.Should().Contain("resolved",
            "success message should confirm resolution");
    }

    // ── checkpoint:latest tests (CKPT-01) ───────────────────────────────────

    [Fact]
    public async Task GoalComplete_CreatesCheckpointLatest()
    {
        // Arrange — init project, add a goal, then complete it
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Goal for checkpoint test", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-4", "Checkpoint outcome", cancellationToken: CancellationToken.None);

        // Assert — checkpoint:latest memory must exist as a persistent (non-ephemeral) memory
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("checkpoint:latest");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "goal_update(complete) should auto-create checkpoint:latest memory");

        // Verify checkpoint is in the 'checkpoint' topic (not ephemeral)
        scope.Should().NotStartWith("~",
            "checkpoint:latest must be persistent, not ephemeral");

        // Verify checkpoint content contains key fields
        string content = await ReadMemoryText(store, "checkpoint:latest");
        content.Should().Contain("G-4",
            "checkpoint content should contain the completed goal ID");
        content.Should().Contain("Checkpoint outcome",
            "checkpoint content should contain the outcome");
        content.Should().Contain("Progress",
            "checkpoint content should contain progress information");
    }

    [Fact]
    public async Task GoalComplete_CheckpointContainsProjectName()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Goal with project name check", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-3", "Done", cancellationToken: CancellationToken.None);

        // Assert — checkpoint content should contain the project name (derived from workspace dir)
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "checkpoint:latest");
        content.Should().Contain("Checkpoint",
            "checkpoint content should have the Checkpoint heading");
        content.Should().Contain("Goals",
            "checkpoint content should contain goals summary");
    }

    [Fact]
    public async Task GoalComplete_CheckpointHasRecoveryKeyword()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Ship it",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Goal for keyword test", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-3", "Keyword outcome", cancellationToken: CancellationToken.None);

        // Assert — checkpoint entry must have 'recovery' keyword for context_resume discovery
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("checkpoint:latest");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == subject);
        entry.Should().NotBeNull("checkpoint:latest entry must exist");
        entry!.Keywords.Should().NotBeNull("checkpoint entry must have keywords");
        entry.Keywords.Should().Contain("recovery",
            "checkpoint entry must have 'recovery' keyword for context_resume discovery");
        entry.Keywords.Should().Contain("checkpoint",
            "checkpoint entry must have 'checkpoint' keyword");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up a project with roadmap containing explicit success criteria for the given phase,
    /// plus two tasks in that phase (without completing them).
    /// </summary>
    private async Task SetupProjectWithCriteria(string phaseId)
    {
        await ScriniaProjectTools.ProjectInit("Goals: build a test project", cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("- CRIT-01: task storage\n- CRIT-02: verification support", cancellationToken: CancellationToken.None);
        // Tasks reference CRIT-01 and CRIT-02 so plan_verify can discover the criteria
        string taskInput =
            $"## Task 01\nWave: 1\nDepends on: none\nAction: Implement CRIT-01 — authentication\nAcceptance criteria:\n- Users can log in\n- JWT tokens are returned\n\n" +
            $"## Task 02\nWave: 1\nDepends on: none\nAction: Implement CRIT-02 — user profile\nAcceptance criteria:\n- Profile data is stored";
        await ScriniaProjectTools.PlanTasks(phaseId, taskInput, cancellationToken: CancellationToken.None);
    }

    // -- Execution log compaction tests (COMPACT-01, COMPACT-02) --

    /// <summary>
    /// Pre-seeds the execution log for a given phase with <paramref name="chunkCount"/> chunks,
    /// each containing distinguishable content like "entry-1", "entry-2", etc.
    /// Uses Nmp2ChunkedEncoder.EncodeChunks directly for speed, then writes the artifact
    /// and index entry so TaskComplete's compaction check sees the correct chunk count.
    /// </summary>
    private static async Task SeedExecutionLog(IMemoryStore store, string phaseId, int chunkCount)
    {
        string logName = $"task:{phaseId}-execution-log";
        var (logScope, logSubject) = store.ParseQualifiedName(logName);

        // Build distinguishable chunks
        var chunks = new string[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            chunks[i] = $"entry-{i + 1}";

        // Encode and write the artifact
        string artifact = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.EncodeChunks(chunks);
        await store.WriteArtifactAsync(logSubject, logScope, artifact);

        // Compute total original bytes for the index entry
        long totalBytes = chunks.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c));

        // Upsert index entry so the compaction guard (logEntry.ChunkCount > 50) sees the right count
        var entry = new Scrinia.Core.Models.ArtifactEntry(
            Name: logSubject,
            Uri: $"file://{logSubject}.nmp2",
            OriginalBytes: totalBytes,
            ChunkCount: chunkCount,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "execution log",
            Keywords: ["execution-log", $"phase:{phaseId}"]);
        store.Upsert(entry, logScope);
    }

    [Fact]
    public async Task TaskComplete_LogOver50Chunks_AutoCompacts()
    {
        // Arrange — project with tasks, then pre-seed log with 51 chunks
        await SetupProjectWithTasks("01", MakeTwoTaskInput());
        var store = MemoryStoreContext.Current!;
        await SeedExecutionLog(store, "01", 51);

        // Act — completing a task appends chunk 52, triggering compaction (> 50)
        string result = await ScriniaProjectTools.TaskComplete("task:01-1-01", "Final outcome", cancellationToken: CancellationToken.None);

        // Assert — response contains compaction notice (in info field)
        ResponseParser.Parse(result).Info.Should().Contain(s => s.Contains("auto-compacted"),
            "task_complete should report auto-compaction when execution log exceeds 50 chunks");

        // Assert — log is compacted to 20 chunks
        var (logScope, logSubject) = store.ParseQualifiedName("task:01-execution-log");
        string logArtifact = await store.ReadArtifactAsync(logSubject, logScope);
        int finalChunkCount = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.GetChunkCount(logArtifact);
        finalChunkCount.Should().Be(20,
            "compaction should keep only the 20 most recent chunks");
    }

    [Fact]
    public async Task TaskComplete_LogAt50Chunks_NoCompaction()
    {
        // Arrange — project with tasks, then pre-seed log with exactly 49 chunks
        // (TaskComplete will append one more → 50 total, which is NOT > 50)
        await SetupProjectWithTasks("01", MakeTwoTaskInput());
        var store = MemoryStoreContext.Current!;
        await SeedExecutionLog(store, "01", 49);

        // Act — completing a task appends chunk 50 (exactly 50 ≤ 50, no compaction)
        string result = await ScriniaProjectTools.TaskComplete("task:01-1-01", "Boundary outcome", cancellationToken: CancellationToken.None);

        // Assert — no compaction notice
        ResponseParser.Parse(result).Info.Should().NotContain(s => s.Contains("auto-compacted"),
            "task_complete should NOT compact when execution log has exactly 50 chunks");

        // Assert — log still has 50 chunks (49 seeded + 1 appended)
        var (logScope, logSubject) = store.ParseQualifiedName("task:01-execution-log");
        string logArtifact = await store.ReadArtifactAsync(logSubject, logScope);
        int finalChunkCount = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.GetChunkCount(logArtifact);
        finalChunkCount.Should().Be(50,
            "log should retain all 50 chunks when at the boundary (no compaction)");
    }

    [Fact]
    public async Task TaskComplete_CompactionPreservesRecentChunks()
    {
        // Arrange — project with tasks, then pre-seed log with 60 chunks
        // Each chunk is "entry-N" so we can verify which ones survived compaction.
        await SetupProjectWithTasks("01", MakeTwoTaskInput());
        var store = MemoryStoreContext.Current!;
        await SeedExecutionLog(store, "01", 60);

        // Act — completing a task appends chunk 61, triggering compaction
        string result = await ScriniaProjectTools.TaskComplete("task:01-1-01", "Trigger compaction", cancellationToken: CancellationToken.None);

        // Assert — compaction occurred (in info field)
        ResponseParser.Parse(result).Info.Should().Contain(s => s.Contains("auto-compacted"),
            "task_complete should report auto-compaction when execution log exceeds 50 chunks");

        // Assert — 20 chunks retained
        var (logScope, logSubject) = store.ParseQualifiedName("task:01-execution-log");
        string logArtifact = await store.ReadArtifactAsync(logSubject, logScope);
        int finalChunkCount = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.GetChunkCount(logArtifact);
        finalChunkCount.Should().Be(20,
            "compaction should keep exactly 20 chunks");

        // Assert — retained chunks are the 20 most recent (entries 42-61),
        // not the oldest (entries 1-20).
        // After seeding 60 chunks + 1 appended by TaskComplete = 61 total before compaction.
        // Compaction keeps chunks 42-61. Chunk 61 is the TaskComplete-appended entry.

        // The oldest retained chunk (chunk 1 after compaction) should be entry-42
        string firstRetained = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.DecodeChunk(logArtifact, 1);
        firstRetained.Should().Contain("entry-42",
            "the first retained chunk should be entry-42 (most recent 20 from 61 total)");

        // The oldest entries should NOT be present
        string fullLog = string.Concat(
            Enumerable.Range(1, 20)
                .Select(c => Scrinia.Core.Encoding.Nmp2ChunkedEncoder.DecodeChunk(logArtifact, c)));
        fullLog.Should().NotContain("entry-1\n",
            "entry-1 is old and should have been dropped by compaction");
        fullLog.Should().NotContain("entry-41\n",
            "entry-41 is old and should have been dropped by compaction");

        // The last chunk should be the TaskComplete-appended entry containing the outcome
        string lastRetained = Scrinia.Core.Encoding.Nmp2ChunkedEncoder.DecodeChunk(logArtifact, 20);
        lastRetained.Should().Contain("Trigger compaction",
            "the last retained chunk should be the outcome appended by task_complete");
    }

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }
}
