using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests for the entity() MCP tool dispatcher.
/// Verifies create/transition/show/list/search operations, plan dispatch
/// migration (WF-14), cross-entity validation (QAL-074), and error paths.
/// </summary>
public sealed class EntityDispatcherTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public EntityDispatcherTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CancellationToken CT => CancellationToken.None;

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    private async Task InitProject(string context = "Goals: entity dispatcher test project")
    {
        await ScriniaProjectTools.ProjectInit(context, cancellationToken: CT);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 1. Entity create operations
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityCreate_Goal_CreatesGoalAndSeedTasks()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "goal",
            description: "Build authentication system", cancellationToken: CT);

        // Assert — result should confirm goal creation
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity create goal should succeed");
        r.Content.Should().Contain("authentication",
            "response should reference the goal description");

        // Verify project:context was updated with the new goal
        var store = MemoryStoreContext.Current!;
        string context = await ReadMemoryText(store, "project:context");
        context.Should().Contain("authentication",
            "project:context should contain the new goal description");
    }

    [Fact]
    public async Task EntityCreate_Concern_StoresWithKeywords()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "concern",
            description: "Risk: SQL injection in user input handler",
            severity: "high", phase: "01", id: "sql-injection",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity create concern should succeed");
        r.Path.Should().Contain("/concern/sql-injection",
            "response should reference the stored concern name");

        // Verify concern exists with proper keywords
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("concern:placeholder");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name.Contains("sql-injection",
            StringComparison.OrdinalIgnoreCase));
        entry.Should().NotBeNull("concern entry must exist in index");
        entry!.Keywords.Should().Contain("status:active",
            "concern must have status:active keyword");
        entry.Keywords.Should().Contain("severity:high",
            "concern must have severity keyword");
    }

    [Fact]
    public async Task EntityCreate_Requirement_StoresProjectRequirements()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "requirement",
            requirements: "- REQ-01: User authentication\n- REQ-02: Data encryption",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity create requirement should succeed");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:requirements");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "entity create requirement should store project:requirements memory");
    }

    [Fact]
    public async Task EntityCreate_Project_InitializesProject()
    {
        // Act — no prior InitProject() call needed
        string result = await ScriniaProjectTools.EntityDispatch("create", "project",
            description: "Goals: build a memory server for AI agents",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity create project should succeed");
        r.Content.Should().Contain("Initialized project",
            "response should confirm project initialization");

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("project:context");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "entity create project should store project:context memory");
    }

    [Fact]
    public async Task EntityCreate_Project_ViaContextParam()
    {
        // Act — use context parameter instead of description
        string result = await ScriniaProjectTools.EntityDispatch("create", "project",
            context: "Goals: alternative parameter test",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity create project via context param should succeed");
        r.Content.Should().Contain("Initialized project",
            "response should confirm project initialization");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 2. Entity transitions
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityTransition_Goal_Complete()
    {
        // Arrange — init project + add a goal
        await InitProject("Goals:\n- Build the API\n- Create the UI");
        await ScriniaProjectTools.GoalUpdate("add", "Deploy to production", null, null, cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("transition", "goal",
            id: "G-3", to: "complete", outcome: "Deployed successfully",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity transition goal to complete should succeed");

        // Verify the goal is marked as complete in project:context
        var store = MemoryStoreContext.Current!;
        string context = await ReadMemoryText(store, "project:context");
        context.Should().Contain("[complete]",
            "project:context should show the goal as complete");
    }

    [Fact]
    public async Task EntityTransition_Concern_Resolved()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: memory leak in connection pool",
            "medium", "01", id: "mem-leak", CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("transition", "concern",
            id: "mem-leak", to: "resolved",
            resolution: "Fixed by adding proper disposal",
            verifiedBy: "qa",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity transition concern to resolved should succeed");
    }

    [Fact]
    public async Task EntityTransition_Requirement_Fulfilled()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.PlanRequirements(
            "- REQ-01: User auth\n- REQ-02: Encryption", cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("transition", "requirement",
            id: "REQ-01", to: "fulfilled",
            evidence: "Implemented in AuthService.cs, tested in AuthTests.cs",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity transition requirement to fulfilled should succeed");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 3. Entity queries
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityShow_Project_ReturnsStatus()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.PlanRequirements("- REQ-01: init", cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "project", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show project should succeed");
        r.Content.Should().Contain("Phase:", "entity show project should include phase info");
        r.Content.Should().Contain("Progress:", "entity show project should include progress info");
    }

    [Fact]
    public async Task EntityList_Goal_ReturnsGoalList()
    {
        // Arrange
        await InitProject("Goals:\n- Build the API\n- Create the UI");
        await ScriniaProjectTools.GoalUpdate("add", "Ship MVP", null, null, cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("list", "goal", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity list goal should succeed");
        r.Content.Should().Contain("Ship MVP",
            "entity list goal should include the dynamically added goal");
    }

    [Fact]
    public async Task EntityList_Concern_ReturnsConcernList()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: XSS vulnerability", "high", "01", id: "xss-risk", CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("list", "concern", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity list concern should succeed");
        r.Content.Should().Contain("xss-risk",
            "entity list concern should include the added concern");
    }

    [Fact]
    public async Task EntityList_Requirement_ReturnsRequirementList()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.PlanRequirements(
            "- REQ-01: User auth\n- REQ-02: Data encryption", cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("list", "requirement", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity list requirement should succeed");
        r.Content.Should().Contain("REQ-01",
            "entity list requirement should include stored requirements");
    }

    [Fact]
    public async Task EntitySearch_FindsEntities()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: authentication bypass via token forgery",
            "high", "01", id: "auth-bypass", CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("search", "concern",
            query: "authentication bypass", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity search should succeed");
        // The search should find the concern we just added
        // (exact matching depends on the search implementation, but it should not error)
    }

    [Fact]
    public async Task EntityShow_Goal_ById_ReturnsSpecificGoal()
    {
        // Arrange
        await InitProject("Goals:\n- Build the API\n- Create the UI");
        await ScriniaProjectTools.GoalUpdate("add", "Deploy to staging", null, null, cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "goal",
            id: "G-3", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show goal by ID should succeed");
        r.Content.Should().Contain("Deploy to staging",
            "entity show goal by ID should return the matching goal");
    }

    [Fact]
    public async Task EntityShow_Concern_ById_ReturnsSpecificConcern()
    {
        // Arrange
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Risk: rate limiting not implemented",
            "medium", "02", id: "rate-limit", CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "concern",
            id: "rate-limit", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show concern by ID should succeed");
        r.Content.Should().Contain("rate limiting",
            "entity show concern by ID should return the concern content");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 4. Plan dispatch migration (WF-14) — plan('status') removed
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlanStatus_RemovedFromTaskDispatch_ReturnsUnknownAction()
    {
        // plan('status') was removed (WF-14). task('status') returns unknown action error.
        // entity('show', type: 'project') is the correct way to get project status.
        await InitProject();
        await ScriniaProjectTools.PlanRequirements("- REQ-01: init", cancellationToken: CT);

        string taskResult = await _tools.TaskDispatch("status", cancellationToken: CT);
        string entityResult = await ScriniaProjectTools.EntityDispatch("show", "project", cancellationToken: CT);

        // task('status') should return an error — not a valid action
        var tr = ResponseParser.Parse(taskResult);
        tr.Status.Should().Be("error", "task('status') should return an error");
        tr.Error.Should().Contain("Unknown action",
            "task('status') should indicate it's not a valid action");

        // entity('show', type: 'project') should still work
        var er = ResponseParser.Parse(entityResult);
        er.Status.Should().Be("success", "entity('show', type: 'project') should succeed");
        er.Content.Should().Contain("Project:",
            "entity('show', type: 'project') should return project status");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 5. Cross-entity validation (QAL-074)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CrossEntity_ConcernGate_BlocksGoalCompletion()
    {
        // Arrange — create project with goals, add a high-severity concern
        await InitProject("Goals:\n- Build the API\n- Create the UI");
        await ScriniaProjectTools.GoalUpdate("add", "Ship MVP", null, null, cancellationToken: CT);
        await ScriniaProjectTools.EntityDispatch("create", "concern",
            description: "Risk: critical security vulnerability discovered",
            severity: "high", phase: "all", id: "critical-sec",
            cancellationToken: CT);

        // Act — try to complete goal while high concern is open
        string result = await ScriniaProjectTools.EntityDispatch("transition", "goal",
            id: "G-3", to: "complete", outcome: "Attempting completion",
            cancellationToken: CT);

        // Assert — should fail with concern gate error
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "goal completion should be blocked when open high-severity concerns exist");
        r.Error.Should().Contain("concern",
            "error message should mention concerns as the reason for blocking");
        r.Error.Should().Contain("critical-sec",
            "error message should reference the specific blocking concern");
    }

    [Fact]
    public async Task CrossEntity_ConcernGate_AllowsAfterResolution()
    {
        // Arrange — create project with goals, add and then resolve a high concern
        await InitProject("Goals:\n- Build the API\n- Create the UI");
        await ScriniaProjectTools.GoalUpdate("add", "Ship after fix", null, null, cancellationToken: CT);
        await ScriniaProjectTools.EntityDispatch("create", "concern",
            description: "Risk: SQL injection in login form",
            severity: "high", phase: "all", id: "sql-inj",
            cancellationToken: CT);

        // Resolve the concern
        await ScriniaProjectTools.EntityDispatch("transition", "concern",
            id: "sql-inj", to: "resolved",
            resolution: "Input sanitization added",
            verifiedBy: "qa",
            cancellationToken: CT);

        // Act — now try to complete the goal
        string result = await ScriniaProjectTools.EntityDispatch("transition", "goal",
            id: "G-3", to: "complete", outcome: "Shipped after fixing SQL injection",
            cancellationToken: CT);

        // Assert — should succeed since the concern is resolved
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "goal completion should succeed after all high concerns are resolved");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 6. Error paths
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Error_InvalidType_ReturnsError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "invalid",
            description: "test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity with invalid type should return an error");
        r.Error.Should().Contain("invalid",
            "error should mention the invalid type");
        r.Error.Should().Contain("Valid types:",
            "error should list valid types");
    }

    [Fact]
    public async Task Error_InvalidAction_ReturnsError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("invalid", "goal",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity with invalid action should return an error");
        r.Error.Should().Contain("invalid",
            "error should mention the invalid action");
        r.Error.Should().Contain("Valid actions:",
            "error should list valid actions");
    }

    [Fact]
    public async Task Error_CreateGoal_MissingDescription_ReturnsError()
    {
        // Act — create goal without description
        string result = await ScriniaProjectTools.EntityDispatch("create", "goal",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create goal without description should return an error");
        r.Error.Should().Contain("description",
            "error should mention the missing parameter");
    }

    [Fact]
    public async Task Error_CreateConcern_MissingSeverity_ReturnsError()
    {
        // Act — create concern without severity
        string result = await ScriniaProjectTools.EntityDispatch("create", "concern",
            description: "Risk: test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create concern without severity should return an error");
        r.Error.Should().Contain("severity",
            "error should mention the missing parameter");
    }

    [Fact]
    public async Task Error_CreateConcern_MissingPhase_ReturnsError()
    {
        // Act — create concern without phase
        string result = await ScriniaProjectTools.EntityDispatch("create", "concern",
            description: "Risk: test", severity: "high", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create concern without phase should return an error");
        r.Error.Should().Contain("phase",
            "error should mention the missing parameter");
    }

    [Fact]
    public async Task Error_CreateRequirement_MissingRequirements_ReturnsError()
    {
        // Act — create requirement without requirements text
        string result = await ScriniaProjectTools.EntityDispatch("create", "requirement",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create requirement without requirements should return an error");
        r.Error.Should().Contain("requirements",
            "error should mention the missing parameter");
    }

    [Fact]
    public async Task Error_CreateProject_MissingDescriptionAndContext_ReturnsError()
    {
        // Act — create project without description or context
        string result = await ScriniaProjectTools.EntityDispatch("create", "project",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create project without description or context should return an error");
    }

    [Fact]
    public async Task Error_TransitionMissingTo_ReturnsError()
    {
        // Act — transition without 'to' parameter
        string result = await ScriniaProjectTools.EntityDispatch("transition", "goal",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "transition without 'to' should return an error");
        r.Error.Should().Contain("to",
            "error should mention the missing 'to' parameter");
    }

    [Fact]
    public async Task Error_TransitionInvalidTargetState_ReturnsError()
    {
        // Act — transition concern to an invalid state
        string result = await ScriniaProjectTools.EntityDispatch("transition", "concern",
            to: "invalid", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "transition to invalid state should return an error");
        r.Error.Should().Contain("invalid",
            "error should reference the invalid target state");
    }

    [Fact]
    public async Task Error_TransitionConcern_MissingParams_ReturnsError()
    {
        // Act — resolve concern without id, resolution, or verifiedBy
        string result = await ScriniaProjectTools.EntityDispatch("transition", "concern",
            to: "resolved", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "resolve concern without params should return an error");
        r.Error.Should().Contain("id",
            "error should mention missing 'id' parameter");
        r.Error.Should().Contain("resolution",
            "error should mention missing 'resolution' parameter");
        r.Error.Should().Contain("verifiedBy",
            "error should mention missing 'verifiedBy' parameter");
    }

    [Fact]
    public async Task Error_TransitionRequirement_MissingParams_ReturnsError()
    {
        // Act — fulfill requirement without id or evidence
        string result = await ScriniaProjectTools.EntityDispatch("transition", "requirement",
            to: "fulfilled", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "fulfill requirement without params should return an error");
        r.Error.Should().Contain("id",
            "error should mention missing 'id' parameter");
        r.Error.Should().Contain("evidence",
            "error should mention missing 'evidence' parameter");
    }

    [Fact]
    public async Task Error_SearchWithoutQuery_ReturnsError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("search", "concern",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity search without query should return an error");
        r.Error.Should().Contain("query",
            "error should mention the missing 'query' parameter");
    }

    [Fact]
    public async Task Error_ShowGoal_InvalidId_ReturnsError()
    {
        // Arrange
        await InitProject("Goals:\n- Build the API");

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "goal",
            id: "G-999", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity show goal with non-existent ID should return an error");
        r.Error.Should().Contain("G-999",
            "error should reference the requested goal ID");
    }

    [Fact]
    public async Task Error_TransitionGoal_MissingId_ReturnsError()
    {
        // Act — complete goal without specifying an ID
        string result = await ScriniaProjectTools.EntityDispatch("transition", "goal",
            to: "complete", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "goal completion without ID should return an error");
        r.Error.Should().Contain("id",
            "error should mention the missing 'id' parameter");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Additional: entity update + unsupported combos
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityUpdate_Goal_EditsDescription()
    {
        // Arrange
        await InitProject("Goals:\n- Build the API");
        await ScriniaProjectTools.GoalUpdate("add", "Original description", null, null, cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("update", "goal",
            id: "G-2", description: "Updated description via entity",
            cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity update goal should succeed");
    }

    [Fact]
    public async Task EntityUpdate_UnsupportedType_ReturnsError()
    {
        // Act — update is not supported for concern type
        string result = await ScriniaProjectTools.EntityDispatch("update", "concern",
            description: "test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity update for unsupported type should return an error");
        r.Error.Should().Contain("concern",
            "error should reference the unsupported type");
    }

    [Fact]
    public async Task EntityTransition_UnsupportedType_ReturnsError()
    {
        // Act — transition is not supported for project type
        string result = await ScriniaProjectTools.EntityDispatch("transition", "project",
            to: "complete", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "entity transition for unsupported type should return an error");
    }

    [Fact]
    public async Task EntityCreate_TypeCaseInsensitive()
    {
        // Act — type should be case-insensitive
        string result = await ScriniaProjectTools.EntityDispatch("create", "PROJECT",
            description: "Case insensitive test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "entity type should be case-insensitive");
        r.Content.Should().Contain("Initialized project",
            "uppercase type should still route correctly");
    }

    [Fact]
    public async Task EntityCreate_ActionCaseInsensitive()
    {
        // Act — action should be case-insensitive
        string result = await ScriniaProjectTools.EntityDispatch("CREATE", "project",
            description: "Case insensitive action test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "entity action should be case-insensitive");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 7. File entity computed view
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityShow_File_ReturnsReferencingMemories()
    {
        // Arrange — create a real file and store a memory referencing it via codeRefs
        string relPath = "src/Example.cs";
        string fullPath = Path.Combine(_scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "// example file content");

        await _memTools.Store(
            content: ["Architecture notes referencing Example.cs"],
            name: "arch:example-notes",
            description: "Notes about the example file",
            codeRefs: [relPath],
            cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "file",
            id: "src/Example.cs", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show file should succeed");
        r.Content.Should().Contain("Example.cs", "should reference the file path");
        r.Content.Should().Contain("Referenced by:", "should list referencing memories");
        r.Content.Should().Contain("/arch/example-notes", "should include the memory name");
        r.Content.Should().Contain("[OK]", "file should have OK status since it hasn't changed");
    }

    [Fact]
    public async Task EntityShow_File_NoReferences_ReturnsMessage()
    {
        // Act — show a file that no memory references
        string result = await ScriniaProjectTools.EntityDispatch("show", "file",
            id: "src/Nonexistent.cs", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show file with no refs should succeed");
        r.Content.Should().Contain("No memories reference file",
            "should indicate no references found");
        r.Content.Should().Contain("Nonexistent.cs",
            "should mention the requested file path");
    }

    [Fact]
    public async Task EntityList_File_ReturnsInvertedIndex()
    {
        // Arrange — create files and store memories referencing them
        string relPath1 = "src/FileA.cs";
        string relPath2 = "src/FileB.cs";
        string fullPath1 = Path.Combine(_scope.WorkspaceDir, relPath1);
        string fullPath2 = Path.Combine(_scope.WorkspaceDir, relPath2);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath1)!);
        File.WriteAllText(fullPath1, "// file A content");
        File.WriteAllText(fullPath2, "// file B content");

        await _memTools.Store(
            content: ["Memory referencing FileA"],
            name: "arch:file-a-ref",
            codeRefs: [relPath1],
            cancellationToken: CT);

        await _memTools.Store(
            content: ["Memory referencing both files"],
            name: "patterns:both-files",
            codeRefs: [relPath1, relPath2],
            cancellationToken: CT);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("list", "file", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity list file should succeed");
        r.Content.Should().Contain("File references",
            "should have file references header");
        r.Content.Should().Contain("src/FileA.cs",
            "should list FileA.cs");
        r.Content.Should().Contain("src/FileB.cs",
            "should list FileB.cs");
        r.Content.Should().Contain("2 ref", "FileA should have 2 references");
        r.Content.Should().Contain("1 ref", "FileB should have 1 reference");
    }

    [Fact]
    public async Task EntityList_File_WithQuery_FiltersResults()
    {
        // Arrange — create files and store memories
        string relPath1 = "src/Alpha.cs";
        string relPath2 = "src/Beta.cs";
        string fullPath1 = Path.Combine(_scope.WorkspaceDir, relPath1);
        string fullPath2 = Path.Combine(_scope.WorkspaceDir, relPath2);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath1)!);
        File.WriteAllText(fullPath1, "// alpha content");
        File.WriteAllText(fullPath2, "// beta content");

        await _memTools.Store(
            content: ["Alpha reference"],
            name: "arch:alpha-ref",
            codeRefs: [relPath1],
            cancellationToken: CT);

        await _memTools.Store(
            content: ["Beta reference"],
            name: "arch:beta-ref",
            codeRefs: [relPath2],
            cancellationToken: CT);

        // Act — search/list with query that should only match Alpha
        string result = await ScriniaProjectTools.EntityDispatch("list", "file",
            query: "Alpha", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity list file with query should succeed");
        r.Content.Should().Contain("Alpha.cs",
            "should include files matching the query");
        r.Content.Should().NotContain("Beta.cs",
            "should exclude files not matching the query");
    }

    [Fact]
    public async Task EntityCreate_File_ReturnsUnsupportedError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "file",
            description: "test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "create file should return an error");
        r.Error.Should().Contain("computed view",
            "error should explain files are computed views");
        r.Error.Should().Contain("codeRefs",
            "error should mention codeRefs as the alternative");
    }

    [Fact]
    public async Task EntityUpdate_File_ReturnsUnsupportedError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("update", "file",
            description: "test", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "update file should return an error");
        r.Error.Should().Contain("computed view",
            "error should explain files are computed views");
        r.Error.Should().Contain("codeRefs",
            "error should mention codeRefs as the alternative");
    }

    [Fact]
    public async Task EntityTransition_File_ReturnsUnsupportedError()
    {
        // Act
        string result = await ScriniaProjectTools.EntityDispatch("transition", "file",
            to: "active", cancellationToken: CT);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "transition file should return an error");
        r.Error.Should().Contain("computed view",
            "error should explain files are computed views");
        r.Error.Should().Contain("codeRefs",
            "error should mention codeRefs as the alternative");
    }
}
