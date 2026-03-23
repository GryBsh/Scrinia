using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
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
    private readonly ScriniaMcpTools _memTools;

    public PlanningHelperTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    // ── CalculateProgress (tested via plan_status) ──

    [Fact]
    public async Task PlanStatus_NoTasks_ShowsZeroProgress()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test\n- REQ-02: Test2", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Progress: 0%", because: "no tasks decomposed = 0% progress");
    }

    [Fact]
    public async Task PlanStatus_AllTasksComplete_Shows100Percent()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: do it\nAcceptance criteria:\n- done", CancellationToken.None);

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
        await File.WriteAllTextAsync(Path.Combine(reportsDir, "march-report.md"), "# March Report\nGoal complete.");

        // Complete user task + all auto-injected gate tasks
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);
        await _tools.TaskComplete("task:01-2-qa-gate", "completed", CancellationToken.None);
        await _tools.TaskComplete("task:01-3-self-reflector-gate", "completed", CancellationToken.None);
        await _tools.TaskComplete("task:01-4-evolutionary-gate", "completed", CancellationToken.None);
        await _tools.TaskComplete("task:01-4-cartographer-gate", "completed", CancellationToken.None);
        await _tools.TaskComplete("task:01-4-march-gate", "completed", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        result.Should().Contain("Progress: 100%", because: "all tasks complete = 100%");
    }

    [Fact]
    public async Task PlanStatus_PartialCompletion_ShowsCorrectPercent()
    {
        await _tools.ProjectInit("Goals: test", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: first\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 1\nDepends on: none\nAction: second\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        var result = await _tools.PlanStatus(CancellationToken.None);
        // 1 of 7 tasks complete (2 user + 5 auto-injected gates) = 14%
        result.Should().Contain("Progress: 14%", because: "1 of 7 tasks complete (2 user + 5 gates) = 14%");
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
        await _tools.PlanRequirements("## v1\n- REQ-01: All tasks complete", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: Implement REQ-01 — do it\nAcceptance criteria:\n- done", CancellationToken.None);
        await _tools.TaskComplete("task:01-1-01", "done", CancellationToken.None);

        // Without evidence: returns checklist
        var checklist = await _tools.PlanVerify("01", cancellationToken: CancellationToken.None);
        checklist.Should().Contain("Verification Checklist");
        checklist.Should().Contain("All tasks complete");

        // Write qa:latest so the QA gate passes
        await _memTools.Store(["## QA Report\nBuild: 0 errors\nTests: 1 passed, 0 failed"],
            "qa:latest", cancellationToken: CancellationToken.None);

        // With evidence: records results (include test output to pass QA gate)
        var result = await _tools.PlanVerify("01", evidence: "PASS: All tasks complete — 1 passed, 0 failed", cancellationToken: CancellationToken.None);
        result.Should().Contain("ALL_PASS");
    }

    // ── REQ-06: carry-forward writes computed progress ──

    [Fact]
    public async Task CarryForwardSite_WritesComputedProgress_NotStaleValue()
    {
        // 1. Set up project with tasks
        await _tools.ProjectInit("Goals: test carry-forward", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: first\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 1\nDepends on: none\nAction: second\nAcceptance criteria:\n- done",
            CancellationToken.None);

        // 2. Complete 1 of 7 tasks (2 user + 5 gates) → computed progress = 14%
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        // 3. Overwrite project:state with a stale Progress: 42%
        await _memTools.Store(
            content: [
                "Project: test_carry_forward\n" +
                "ID: test\n" +
                "Phase: Phase 01\n" +
                "Progress: 42%\n" +
                "Last action: stale\n" +
                "Blockers: none\n" +
                "Next: nothing"
            ],
            name: "project:state",
            cancellationToken: CancellationToken.None);

        // Verify stale value is present before carry-forward
        var store = MemoryStoreContext.Current!;
        string stateBefore = await ReadMemoryText(store, "project:state");
        stateBefore.Should().Contain("Progress: 42%", because: "stale value should be written");

        // 4. Trigger a carry-forward site (ConcernAdd calls WriteStateAsync with CalculateProgress)
        await _tools.ConcernAdd("Test concern for carry-forward", "low", "01",
            id: "carry-forward-test", CancellationToken.None);

        // 5. Read project:state and verify computed progress overwrote the stale value
        string stateAfter = await ReadMemoryText(store, "project:state");
        stateAfter.Should().Contain("Progress: 14%",
            because: "carry-forward should write computed progress (1 of 7 tasks = 14%), not stale 42%");
        stateAfter.Should().NotContain("Progress: 42%",
            because: "stale progress value must not survive a carry-forward write");
    }

    // ── REQ-07: restore shows computed progress ──

    [Fact]
    public async Task Restore_ShowsComputedProgress_NotStaleStateProgress()
    {
        // 1. Set up project with tasks
        await _tools.ProjectInit("Goals: test restore progress", CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await _tools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: first\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 1\nDepends on: none\nAction: second\nAcceptance criteria:\n- done",
            CancellationToken.None);

        // 2. Complete 1 of 7 tasks (2 user + 5 gates) → computed progress = 14%
        await _tools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        // 3. Overwrite project:state with a stale Progress: 20%
        await _memTools.Store(
            content: [
                "Project: test_restore_progress\n" +
                "ID: test\n" +
                "Phase: Phase 01\n" +
                "Progress: 20%\n" +
                "Last action: stale\n" +
                "Blockers: none\n" +
                "Next: nothing"
            ],
            name: "project:state",
            cancellationToken: CancellationToken.None);

        // 4. Call Restore via the memory tool dispatcher
        string restoreResult = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // 5. Assert response contains computed progress, not stale value
        restoreResult.Should().Contain("Progress: 14%",
            because: "restore should replace stale progress with computed value (1 of 7 tasks = 14%)");
        restoreResult.Should().NotContain("Progress: 20%",
            because: "stale progress value must not appear in restore output");
    }
}
