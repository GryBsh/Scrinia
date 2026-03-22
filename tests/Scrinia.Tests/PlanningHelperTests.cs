using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for planning tool internal helpers via their observable effects.
/// Since private helpers aren't directly testable (no InternalsVisibleTo),
/// we test them indirectly through tool outputs that depend on them.
/// </summary>
public sealed class PlanningHelperTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public PlanningHelperTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── CalculateProgress (tested via plan_status) ──

    [Fact]
    public async Task PlanStatus_NoTasks_ShowsZeroProgress()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test\n- REQ-02: Test2", CancellationToken.None);
        await _tools.PlanRoadmap(
            "## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done\n\n" +
            "## Phase 2\nRequirements: REQ-02\nSuccess Criteria:\n1. Done", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Progress: 0%", because: "no tasks decomposed = 0% progress");
    }

    [Fact]
    public async Task PlanStatus_AllTasksComplete_Shows100Percent()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: do it\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Progress: 100%", because: "all tasks complete = 100%");
    }

    [Fact]
    public async Task PlanStatus_PartialCompletion_ShowsCorrectPercent()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: first\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 1\nDepends on: none\nAction: second\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Progress: 50%", because: "1 of 2 tasks complete = 50%");
    }

    // ── ExtractPhaseIds + CountPhases (tested via plan_status roadmap note) ──

    [Fact]
    public async Task PlanStatus_MultiplePhases_CountsCorrectly()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: A\n- REQ-02: B\n- REQ-03: C", CancellationToken.None);
        await _tools.PlanRoadmap(
            "## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done\n\n" +
            "## Phase 2\nRequirements: REQ-02\nSuccess Criteria:\n1. Done\n\n" +
            "## Phase 3\nRequirements: REQ-03\nSuccess Criteria:\n1. Done", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("3 phase(s)", because: "roadmap has 3 phases");
    }

    // ── ExtractStateField (tested via plan_status field extraction) ──

    [Fact]
    public async Task PlanStatus_ReturnsAllStateFields()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Project:");
        result.Should().Contain("Progress:");
        result.Should().Contain("Blockers:");
        result.Should().Contain("Next:");
    }

    // ── task_next wave ordering ──

    [Fact]
    public async Task TaskNext_ReturnsOnlyCurrentWave()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: wave1\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 2\nDepends on: 01-1-01\nAction: wave2\nAcceptance criteria:\n- done", CancellationToken.None);

        var result = await _tools.TaskNext("01", CancellationToken.None);
        result.Should().Contain("Wave 1", because: "should return wave 1 first");
        result.Should().NotContain("wave2", because: "wave 2 tasks should not appear yet");
    }

    [Fact]
    public async Task TaskNext_AfterWave1Complete_ReturnsWave2()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. Done", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: wave1\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 2\nDepends on: 01-1-01\nAction: wave2\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "done", CancellationToken.None);

        var result = await _tools.TaskNext("01", CancellationToken.None);
        result.Should().Contain("wave2", because: "wave 1 complete, wave 2 should now appear");
    }

    // ── plan_verify criteria checking ──

    [Fact]
    public async Task PlanVerify_AllTasksComplete_ReturnsAllPass()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanRoadmap("## Phase 1\nRequirements: REQ-01\nSuccess Criteria:\n1. All tasks complete", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: do it\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "done", CancellationToken.None);

        // Without evidence: returns checklist
        var checklist = await _tools.PlanVerify("01", cancellationToken: CancellationToken.None);
        checklist.Should().Contain("Verification Checklist");
        checklist.Should().Contain("All tasks complete");

        // With evidence: records results (include test output to pass QA gate)
        var result = await _tools.PlanVerify("01", "PASS: All tasks complete — 1 passed, 0 failed", CancellationToken.None);
        result.Should().Contain("ALL_PASS");
    }
}
