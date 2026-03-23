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
            Keywords: ["status:pending", "wave:0", "phase:01", $"gate:{gateType}"]);
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
        result.Should().Contain("Error", "completing a QA gate without qa:latest should fail");
        result.Should().Contain("qa:latest", "error should mention the missing artifact");
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
        result.Should().Contain("marked complete",
            "completing a QA gate with qa:latest present should succeed");
        result.Should().NotContain("Error",
            "should not return an error when qa:latest exists");
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
        result.Should().Contain("Error", "completing auditor gate without requirements should fail");
        result.Should().Contain("requirement", "error should mention requirements");
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
        result.Should().Contain("marked complete",
            "completing auditor gate with requirements present should succeed");
        result.Should().NotContain("Error",
            "should not return an error when requirements exist");
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
        result.Should().Contain("marked complete",
            "a non-gate task should complete normally");
        result.Should().NotContain("gate",
            "a non-gate task should not mention gate validation");
    }
}
