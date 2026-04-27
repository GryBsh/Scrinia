using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Error path tests for planning tools — missing prerequisites, corrupt state, recovery.
/// </summary>
public sealed class PlanningErrorPathTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public PlanningErrorPathTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Missing prerequisites ──

    [Fact]
    public async Task PlanStatus_NoProject_ReturnsError()
    {
        var result = await ScriniaProjectTools.PlanStatus(CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "no project:state or project:context exists");
    }

    [Fact]
    public async Task PlanVerify_NoRequirements_ReturnsError()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        var result = await ScriniaProjectTools.PlanVerify("01", cancellationToken: CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "no project:requirements exists");
    }

    [Fact]
    public async Task TaskNext_NoTasksForPhase_ReturnsMessage()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        var result = await ScriniaProjectTools.TaskNext("99", CancellationToken.None);
        ResponseParser.Parse(result).Content.Should().Contain("No pending tasks", because: "no tasks exist for phase 99");
    }

    [Fact]
    public async Task PlanTasks_SucceedsWithoutRoadmap()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        var result = await ScriniaProjectTools.PlanTasks("01", "## Task 01\nWave: 1\nDepends on: none\nAction: test\nAcceptance criteria:\n- done", CancellationToken.None);
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error", because: "roadmap is no longer a prerequisite for plan_tasks");
        parsed.Content.Should().Contain("Created", because: "tasks should be created successfully");
    }

    // ── context_resume rebuilds state ──

    [Fact]
    public async Task ContextResume_WithContextButNoState_RebuildsState()
    {
        // Create project context but delete state
        await ScriniaProjectTools.ProjectInit("Goals: test project\nConstraints: none", CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        var (stateScope, stateSubject) = store.ParseQualifiedName("project:state");
        store.Remove(stateSubject, stateScope);
        store.DeleteArtifact(stateSubject, stateScope);

        var result = await ScriniaProjectTools.ContextResume(CancellationToken.None);
        var parsed = ResponseParser.Parse(result);
        parsed.Content.Should().Contain("Project:", because: "resume should rebuild from project:context");
        parsed.Status.Should().NotBe("error");
    }

    // ── plan_gaps without verification failures ──

    [Fact]
    public async Task PlanGaps_EmptyFailedCriteria_ReturnsError()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);

        var result = await ScriniaProjectTools.PlanGaps("01", "", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "empty failed criteria should be rejected");
    }

    // ── concern_resolve non-existent concern ──

    [Fact]
    public async Task ConcernResolve_NonExistent_ReturnsError()
    {
        var result = await ScriniaProjectTools.ConcernResolve("nonexistent-concern", "resolved", verifiedBy: "manual", CancellationToken.None);
        ResponseParser.Parse(result).Status.Should().Be("error", because: "resolving non-existent concern should fail");
    }

    // ── plan_verify with no tasks referencing REQ-IDs for phase ──

    [Fact]
    public async Task PlanVerify_NoCriteriaForPhase_ReturnsMessage()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);

        var result = await ScriniaProjectTools.PlanVerify("99", cancellationToken: CancellationToken.None);
        ResponseParser.Parse(result).Content.Should().Contain("No requirements found", because: "phase 99 has no tasks with REQ-IDs");
    }
}
