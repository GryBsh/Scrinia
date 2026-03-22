using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Input validation tests for planning tools — null, empty, whitespace edge cases.
/// Verifies tools return error messages rather than throwing unhandled exceptions.
/// </summary>
public sealed class PlanningInputValidationTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public PlanningInputValidationTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── project_init ──

    [Fact]
    public async Task ProjectInit_EmptyContext_StillInitializes()
    {
        var result = await _tools.ProjectInit("", CancellationToken.None);
        result.Should().Contain("Initialized", because: "project_init accepts any context including empty");
    }

    [Fact]
    public async Task ProjectInit_WhitespaceContext_StillInitializes()
    {
        var result = await _tools.ProjectInit("   \n\t  ", CancellationToken.None);
        result.Should().Contain("Initialized", because: "project_init accepts any context including whitespace");
    }

    // ── plan_requirements ──

    [Fact]
    public async Task PlanRequirements_EmptyRequirements_ReturnsError()
    {
        var result = await _tools.PlanRequirements("", CancellationToken.None);
        result.Should().Contain("Error", because: "empty requirements should be rejected");
    }

    [Fact]
    public async Task PlanRequirements_WithoutProjectInit_ReturnsError()
    {
        var result = await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        result.Should().Contain("Error", because: "requirements without project_init should fail");
    }

    // ── plan_roadmap ──

    [Fact]
    public async Task PlanRoadmap_EmptyRoadmap_ReturnsError()
    {
        var result = await _tools.PlanRoadmap("", CancellationToken.None);
        result.Should().Contain("Error", because: "empty roadmap should be rejected");
    }

    // ── plan_tasks ──

    [Fact]
    public async Task PlanTasks_EmptyPhaseId_ReturnsError()
    {
        var result = await _tools.PlanTasks("", "## Task 01\nWave: 1\nDepends on: none\nAction: test", CancellationToken.None);
        result.Should().Contain("Error", because: "empty phaseId should be rejected");
    }

    [Fact]
    public async Task PlanTasks_EmptyTasks_ReturnsError()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);

        var result = await _tools.PlanTasks("01", "", CancellationToken.None);
        result.Should().Contain("Error", because: "empty tasks text should be rejected");
    }

    // ── task_complete ──

    [Fact]
    public async Task TaskComplete_NonExistentTask_ReturnsError()
    {
        var result = await _tools.TaskComplete("task:99-1-01", "done", CancellationToken.None);
        result.Should().Contain("Error", because: "completing a non-existent task should fail");
    }

    [Fact]
    public async Task TaskComplete_EmptyOutcome_StillCompletes()
    {
        // Setup: create a project with a task
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);
        await _tools.PlanTasks("01", "## Task 01\nWave: 1\nDepends on: none\nAction: do something\nAcceptance criteria:\n- done", CancellationToken.None);

        var result = await _tools.TaskComplete("task:01-1-01", "", CancellationToken.None);
        result.Should().Contain("complete", because: "empty outcome should still mark complete");
    }

    // ── research_start ──

    [Fact]
    public async Task ResearchStart_WithoutProject_ReturnsError()
    {
        var result = await _tools.ResearchStart("01", "topic", "question?", CancellationToken.None);
        result.Should().Contain("Error", because: "research without project should fail");
    }

    // ── concern_add ──

    [Fact]
    public async Task ConcernAdd_EmptyDescription_ReturnsError()
    {
        var result = await _tools.ConcernAdd("", "high", "all", null, CancellationToken.None);
        result.Should().Contain("Error", because: "empty description should be rejected");
    }

    // ── goal_update ──

    [Fact]
    public async Task GoalUpdate_AddWithoutDescription_ReturnsError()
    {
        var result = await _tools.GoalUpdate("add", null, null, null, cancellationToken: CancellationToken.None);
        result.Should().Contain("Error", because: "add without description should fail");
    }

    [Fact]
    public async Task GoalUpdate_CompleteNonExistentGoal_ReturnsError()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        var result = await _tools.GoalUpdate("complete", null, "G-99", "done", cancellationToken: CancellationToken.None);
        result.Should().Contain("Error", because: "completing non-existent goal should fail");
    }

    [Fact]
    public async Task GoalUpdate_InvalidAction_ReturnsError()
    {
        var result = await _tools.GoalUpdate("invalid", null, null, null, cancellationToken: CancellationToken.None);
        result.Should().Contain("Error", because: "invalid action should be rejected");
    }

    // ── plan_status ──

    [Fact]
    public async Task PlanStatus_WithoutProject_ReturnsError()
    {
        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Error", because: "status without any project should fail");
    }
}
