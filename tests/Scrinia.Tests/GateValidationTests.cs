using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests that gate task validation in TaskComplete blocks completion
/// when required artifacts are missing, and allows completion when they exist.
/// </summary>
public sealed class GateValidationTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public GateValidationTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up a single-phase project with tasks including auto-injected gate tasks.
    /// </summary>
    private async Task SetupProjectWithTasks()
    {
        await _tools.ProjectInit("Goals: gate validation testing", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Feature X", cancellationToken: CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Implement feature X\nAcceptance criteria:\n- Feature works",
            cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Finds the full task name for a gate task (e.g., qa-gate) in the task index.
    /// </summary>
    private string FindGateTaskName(string gateSuffix)
    {
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var entry = entries.First(e => e.Name.Contains(gateSuffix));
        return $"task:{entry.Name}";
    }

    /// <summary>
    /// Finds the full task name for the user task (task 01) in the task index.
    /// </summary>
    private string FindUserTaskName()
    {
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var entry = entries.First(e => e.Name.EndsWith("-01") && !e.Name.Contains("gate"));
        return $"task:{entry.Name}";
    }

    /// <summary>
    /// Creates a task entry with gate keywords directly in the store.
    /// </summary>
    private async Task CreateGateTask(string taskName, string gateType, string content)
    {
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName(taskName);

        string artifact = Nmp2ChunkedEncoder.Encode(content);
        await store.WriteArtifactAsync(subject, scope, artifact, CancellationToken.None);

        string uri = store.ArtifactUri(subject, scope);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        string desc = content[..Math.Min(200, content.Length)];

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: desc,
            Keywords: ["status:pending", "wave:0", "phase:01", $"tag:{gateType}"]);
        store.Upsert(entry, scope);
    }

    // ── Test 1: QA gate blocks without qa:latest ─────────────────────────────

    [Fact]
    public async Task TaskComplete_QaGate_BlocksWithoutQaLatest()
    {
        // Arrange
        await SetupProjectWithTasks();
        string qaGateTask = FindGateTaskName("qa-gate");

        // Act — try to complete the QA gate without qa:latest existing
        string result = await _tools.TaskComplete(qaGateTask, "QA done",
            cancellationToken: CancellationToken.None);

        // Assert — should be blocked
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error", "completing a QA gate without qa:latest should fail");
        parsed.Error.Should().Contain("qa:latest", "error should mention the missing artifact");
    }

    // ── Test 2: QA gate passes with qa:latest ────────────────────────────────

    [Fact]
    public async Task TaskComplete_QaGate_PassesWithQaLatest()
    {
        // Arrange
        await SetupProjectWithTasks();
        string qaGateTask = FindGateTaskName("qa-gate");

        // Store qa:latest memory so the gate validation passes
        await _memTools.Store(
            content: ["## QA Report\nAll tests pass. 42 passed, 0 failed."],
            name: "qa:latest",
            cancellationToken: CancellationToken.None);

        // Complete all prerequisite tasks first (the QA gate depends on user tasks)
        string userTask = FindUserTaskName();
        await _tools.TaskComplete(userTask, "Feature implemented",
            cancellationToken: CancellationToken.None);

        // Act — complete the QA gate
        string result = await _tools.TaskComplete(qaGateTask, "QA passed",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "should not return an error when qa:latest exists");
        parsed.Content.Should().Contain("marked complete",
            "completing a QA gate with qa:latest present should succeed");
    }

    // ── Test 3: Auditor gate blocks without requirements ─────────────────────

    [Fact]
    public async Task TaskComplete_AuditorGate_BlocksWithoutRequirements()
    {
        // Arrange — create a project without requirements, add auditor gate task
        await _tools.ProjectInit("Goals: auditor gate test", cancellationToken: CancellationToken.None);
        await CreateGateTask("task:01-0-auditor-gate", "auditor",
            "## Auditor Gate\nAction: Run auditor\nAcceptance criteria:\n- Requirements exist");

        // Act — try to complete the auditor gate
        string result = await _tools.TaskComplete("task:01-0-auditor-gate", "Auditor scan done",
            cancellationToken: CancellationToken.None);

        // Assert — should be blocked since no requirements exist
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error", "completing auditor gate without requirements should fail");
        parsed.Error.Should().Contain("requirement", "error should mention requirements");
    }

    // ── Test 4: Auditor gate passes with requirements ────────────────────────

    [Fact]
    public async Task TaskComplete_AuditorGate_PassesWithRequirements()
    {
        // Arrange — create project with requirements, then add auditor gate task
        await _tools.ProjectInit("Goals: auditor gate pass test", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Feature X", cancellationToken: CancellationToken.None);
        await CreateGateTask("task:01-0-auditor-gate", "auditor",
            "## Auditor Gate\nAction: Run auditor\nAcceptance criteria:\n- Requirements exist");

        // Act — complete the auditor gate (requirements exist from PlanRequirements)
        string result = await _tools.TaskComplete("task:01-0-auditor-gate", "Auditor scan done",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "should not return an error when requirements exist");
        parsed.Content.Should().Contain("marked complete",
            "completing auditor gate with requirements present should succeed");
    }

    // ── Test 5: Non-gate task completes normally ─────────────────────────────

    [Fact]
    public async Task TaskComplete_NonGateTask_CompletesWithoutArtifactChecks()
    {
        // Arrange
        await SetupProjectWithTasks();
        string userTask = FindUserTaskName();

        // Act — complete a normal task (no gate keywords)
        string result = await _tools.TaskComplete(userTask, "Feature implemented",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed without gate validation
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "a non-gate task should complete normally");
        parsed.Content.Should().Contain("marked complete",
            "a non-gate task should complete normally");
        parsed.Content.Should().NotContain("gate",
            "a non-gate task should not mention gate validation");
    }

    // ── Test 6: RequiredOutput missing blocks task completion ──────────────

    [Fact]
    public async Task TaskComplete_RequiredOutput_Missing_ReturnsError()
    {
        // Arrange — create a project with a goal so researcher seed tasks are created
        await _tools.ProjectInit("Goals:\n- Test required outputs",
            cancellationToken: CancellationToken.None);

        await _tools.GoalUpdate("add", "Investigate required output validation",
            null, null, cancellationToken: CancellationToken.None);

        // Find the researcher seed task (which has RequiredOutputs)
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var researcherEntry = entries.First(e => e.Name.Contains("researcher"));
        string researcherTaskName = $"task:{researcherEntry.Name}";

        // Act — complete researcher task WITHOUT storing any research:* memories
        string result = await _tools.TaskComplete(researcherTaskName, "Done investigating",
            cancellationToken: CancellationToken.None);

        // Assert — should fail because gate validation (index-prefix for research:*) is not met.
        // The gate Validation fires before RequiredOutputs, both enforce the same constraint.
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "completing researcher without storing research should fail validation");
        parsed.Error.Should().Contain("research",
            "error should reference the missing research output");
    }

    // ── Test 7: RequiredOutput present allows task completion ──────────────

    [Fact]
    public async Task TaskComplete_RequiredOutput_Present_Succeeds()
    {
        // Arrange — create project + goal, then store the required research memory
        await _tools.ProjectInit("Goals:\n- Test required outputs present",
            cancellationToken: CancellationToken.None);

        await _tools.GoalUpdate("add", "Verify required output success path",
            null, null, cancellationToken: CancellationToken.None);

        // Find the researcher seed task and extract the goal short ID
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var researcherEntry = entries.First(e => e.Name.Contains("researcher"));
        string researcherTaskName = $"task:{researcherEntry.Name}";

        // Extract goal short prefix from the task name (e.g., "g1-abc-00-0-researcher" → "g1-abc")
        string goalShort = "";
        var goalKw = researcherEntry.Keywords?.FirstOrDefault(k =>
            k.StartsWith("goal:", StringComparison.OrdinalIgnoreCase));
        if (goalKw is not null)
        {
            // goal:G-1-abc → extract the part after "goal:" and build the short form
            string goalId = goalKw["goal:".Length..];
            // Parse G-N-hex → gN-hex
            var match = System.Text.RegularExpressions.Regex.Match(goalId, @"G-(\d+)-(\w+)");
            if (match.Success)
                goalShort = $"g{match.Groups[1].Value}-{match.Groups[2].Value}-";
        }

        // Store a research:* memory matching the expected pattern
        await _memTools.Store(
            content: ["## Research Findings\nScope analysis complete. Found 3 affected files."],
            name: $"research:{goalShort}scope-analysis",
            cancellationToken: CancellationToken.None);

        // Act — complete the researcher task (required research output now exists)
        string result = await _tools.TaskComplete(researcherTaskName, "Research completed",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed since the required output is present
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "completing researcher after storing research should pass RequiredOutputs validation");
        parsed.Content.Should().Contain("marked complete",
            "researcher task should be marked complete when required outputs exist");
    }

    // ── Test 8: Task without RequiredOutputs completes normally ────────────

    [Fact]
    public async Task TaskComplete_NoRequiredOutputs_Succeeds()
    {
        // Arrange — create project + goal, then plan tasks for a phase with user tasks
        await _tools.ProjectInit("Goals:\n- Test no required outputs",
            cancellationToken: CancellationToken.None);

        await _tools.GoalUpdate("add", "Verify backward compat for tasks without RequiredOutputs",
            null, null, cancellationToken: CancellationToken.None);

        // Find researcher and store its required output to unblock further seeds
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var researcherEntry = entries.First(e => e.Name.Contains("researcher"));

        // Extract goal short from researcher entry
        string goalShort = "";
        var goalKw = researcherEntry.Keywords?.FirstOrDefault(k =>
            k.StartsWith("goal:", StringComparison.OrdinalIgnoreCase));
        if (goalKw is not null)
        {
            string goalId = goalKw["goal:".Length..];
            var match = System.Text.RegularExpressions.Regex.Match(goalId, @"G-(\d+)-(\w+)");
            if (match.Success)
                goalShort = $"g{match.Groups[1].Value}-{match.Groups[2].Value}-";
        }

        // Store research memory and complete researcher+auditor+planner seeds
        await _memTools.Store(
            content: ["## Research\nScope analysis."],
            name: $"research:{goalShort}scope",
            cancellationToken: CancellationToken.None);
        await _tools.TaskComplete($"task:{researcherEntry.Name}", "Research done",
            cancellationToken: CancellationToken.None);

        // Store requirements and complete auditor
        await _tools.PlanRequirements("## v1\n- REQ-01: Feature X", cancellationToken: CancellationToken.None);
        var auditorEntry = entries.First(e => e.Name.Contains("auditor"));
        await _tools.TaskComplete($"task:{auditorEntry.Name}", "Audit done",
            cancellationToken: CancellationToken.None);

        // Plan execution tasks, then complete planner
        await _tools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Build feature\nAcceptance criteria:\n- done",
            cancellationToken: CancellationToken.None);
        var plannerEntry = entries.First(e => e.Name.Contains("planner"));
        await _tools.TaskComplete($"task:{plannerEntry.Name}", "Planning done",
            cancellationToken: CancellationToken.None);

        // Reload entries after planning to find user tasks
        entries = store.LoadIndex(taskScope);
        var userTask = entries.First(e =>
            e.Name.EndsWith("-01") &&
            e.Keywords is not null &&
            !e.Keywords.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)));
        string userTaskName = $"task:{userTask.Name}";

        // Act — complete a user task (no RequiredOutputs defined)
        string result = await _tools.TaskComplete(userTaskName, "Feature built",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed (user tasks have no RequiredOutputs)
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "a task without RequiredOutputs should complete normally (backward compat)");
        parsed.Content.Should().Contain("marked complete",
            "user task without RequiredOutputs should be marked complete");
    }
}
