using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the planner seed task auto-created by research_complete.
/// Wave 0 ensures the planner task surfaces before execution tasks (wave 1+).
/// </summary>
public sealed class PlannerSeedTaskTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public PlannerSeedTaskTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SetupResearchComplete(string phaseId = "01", string topic = "test-topic")
    {
        await _tools.ProjectInit("Goals: planner seed testing", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("- REQ-01: Feature A", cancellationToken: CancellationToken.None);
        await _tools.PlanRoadmap(
            $"## Phase {int.Parse(phaseId)}: Foundation\nREQ-IDs: REQ-01\n" +
            $"Success criteria:\n- Feature A works",
            cancellationToken: CancellationToken.None);
        await _tools.ResearchStart(phaseId, topic, "What approach should we take?",
            cancellationToken: CancellationToken.None);
        await _tools.ResearchComplete(phaseId, topic, "Findings: approach X is best.",
            cancellationToken: CancellationToken.None);
    }

    // ── Test 1: research_complete creates a planner task ──────────────────────

    [Fact]
    public async Task ResearchComplete_CreatesPlannerTask()
    {
        // Arrange & Act
        await SetupResearchComplete();

        // Assert — a task with "planner" in the name must exist with correct keywords
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var plannerEntry = entries.FirstOrDefault(e => e.Name.Contains("planner"));
        plannerEntry.Should().NotBeNull("research_complete should create a planner seed task");
        plannerEntry!.Keywords.Should().Contain("status:pending",
            "planner task must have status:pending keyword");
        plannerEntry.Keywords.Should().Contain("wave:0",
            "planner task must be wave 0 to surface before execution tasks");
        plannerEntry.Keywords.Should().Contain("phase:01",
            "planner task must have phase keyword matching the research phase");
        plannerEntry.Keywords.Should().Contain("gate:planner",
            "planner task must have gate:planner keyword");
    }

    // ── Test 2: task_next surfaces planner before execution tasks ─────────────

    [Fact]
    public async Task TaskNext_SurfacesPlannerBeforeExecutionTasks()
    {
        // Arrange — research_complete creates wave 0 planner task
        await SetupResearchComplete();

        // Manually create a wave 1 execution task via PlanTasks
        string taskInput =
            "## Task 01\nDepends on: none\nAction: Implement feature\nAcceptance criteria:\n- done";
        await _tools.PlanTasks("01", taskInput, cancellationToken: CancellationToken.None);

        // Act — task_next should return wave 0 (planner) not wave 1 (execution)
        string result = await _tools.TaskNext("01", cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("Wave 0",
            "task_next should return wave 0 planner task before wave 1 execution tasks");
        result.Should().Contain("planner",
            "task_next response should mention the planner task");
    }

    // ── Test 3: research_complete response mentions planner task ──────────────

    [Fact]
    public async Task ResearchComplete_ResponseMentionsPlannerTask()
    {
        // Arrange
        await _tools.ProjectInit("Goals: planner seed testing", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("- REQ-01: Feature A", cancellationToken: CancellationToken.None);
        await _tools.PlanRoadmap(
            "## Phase 1: Foundation\nREQ-IDs: REQ-01\nSuccess criteria:\n- Feature A works",
            cancellationToken: CancellationToken.None);
        await _tools.ResearchStart("01", "test-topic", "What approach should we take?",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _tools.ResearchComplete("01", "test-topic",
            "Findings: approach X is best.", cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("Planner task created",
            "research_complete response must mention the planner task was created");
    }
}
