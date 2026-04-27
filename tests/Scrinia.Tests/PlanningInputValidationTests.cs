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
        var result = await ScriniaProjectTools.ProjectInit("", CancellationToken.None);
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success", because: "project_init accepts any context including empty");
        parsed.Content.Should().Contain("Initialized", because: "response content should mention initialization");
    }

    [Fact]
    public async Task ProjectInit_WhitespaceContext_StillInitializes()
    {
        var result = await ScriniaProjectTools.ProjectInit("   \n\t  ", CancellationToken.None);
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success", because: "project_init accepts any context including whitespace");
        parsed.Content.Should().Contain("Initialized", because: "response content should mention initialization");
    }

    // ── plan_requirements ──

    [Fact]
    public async Task PlanRequirements_EmptyRequirements_ReturnsError()
    {
        var result = await ScriniaProjectTools.PlanRequirements("", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "empty requirements should be rejected");
    }

    [Fact]
    public async Task PlanRequirements_WithoutProjectInit_ReturnsError()
    {
        var result = await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "requirements without project_init should fail");
    }

    // ── plan_tasks ──

    [Fact]
    public async Task PlanTasks_EmptyPhaseId_ReturnsError()
    {
        var result = await ScriniaProjectTools.PlanTasks("", "## Task 01\nWave: 1\nDepends on: none\nAction: test", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "empty phaseId should be rejected");
    }

    [Fact]
    public async Task PlanTasks_EmptyTasks_ReturnsError()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);

        var result = await ScriniaProjectTools.PlanTasks("01", "", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "empty tasks text should be rejected");
    }

    // ── task_complete ──

    [Fact]
    public async Task TaskComplete_NonExistentTask_ReturnsError()
    {
        var result = await ScriniaProjectTools.TaskComplete("task:99-1-01", "done", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "completing a non-existent task should fail");
    }

    [Fact]
    public async Task TaskComplete_EmptyOutcome_StillCompletes()
    {
        // Setup: create a project with a task
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await ScriniaProjectTools.PlanTasks("01", "## Task 01\nWave: 1\nDepends on: none\nAction: do something\nAcceptance criteria:\n- done", CancellationToken.None);

        var result = await ScriniaProjectTools.TaskComplete("task:01-1-01", "", CancellationToken.None);
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success", because: "empty outcome should still mark complete");
        parsed.Content.Should().Contain("complete", because: "response should confirm task completion");
    }

    // ── concern_add ──

    [Fact]
    public async Task ConcernAdd_EmptyDescription_ReturnsError()
    {
        var result = await ScriniaProjectTools.ConcernAdd("", "high", "all", null, CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "empty description should be rejected");
    }

    // ── goal_update ──

    [Fact]
    public async Task GoalUpdate_AddWithoutDescription_ReturnsError()
    {
        var result = await ScriniaProjectTools.GoalUpdate("add", null, null, null, cancellationToken: CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "add without description should fail");
    }

    [Fact]
    public async Task GoalUpdate_CompleteNonExistentGoal_ReturnsError()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        var result = await ScriniaProjectTools.GoalUpdate("complete", null, "G-99", "done", cancellationToken: CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "completing non-existent goal should fail");
    }

    [Fact]
    public async Task GoalUpdate_InvalidAction_ReturnsError()
    {
        var result = await ScriniaProjectTools.GoalUpdate("invalid", null, null, null, cancellationToken: CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "invalid action should be rejected");
    }

    // ── plan_status ──

    [Fact]
    public async Task PlanStatus_WithoutProject_ReturnsError()
    {
        var result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "status without any project should fail");
    }
}
