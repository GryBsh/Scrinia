using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests that PlanTasks auto-injects gate tasks (qa-gate, evolutionary-gate,
/// cartographer-gate, march-gate) into the task plan so that gate compliance
/// becomes mandatory.
/// </summary>
public sealed class AutoInjectedGateTaskTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public AutoInjectedGateTaskTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up a project with requirements and a single-phase roadmap.
    /// Phase 01 is the last (and only) phase.
    /// </summary>
    private async Task SetupSinglePhaseProject()
    {
        await _tools.ProjectInit("Goals: gate injection testing", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Feature X", cancellationToken: CancellationToken.None);
        await _tools.PlanRoadmap(
            "## Phase 01\nREQ-IDs: REQ-01\nSuccess criteria:\n- Feature X works",
            cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Sets up a project with requirements and a two-phase roadmap.
    /// Phase 01 is NOT the last phase; phase 02 is.
    /// </summary>
    private async Task SetupTwoPhaseProject()
    {
        await _tools.ProjectInit("Goals: multi-phase gate testing", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("## v1\n- REQ-01: Feature X\n- REQ-02: Feature Y",
            cancellationToken: CancellationToken.None);
        await _tools.PlanRoadmap(
            "## Phase 01\nREQ-IDs: REQ-01\nSuccess criteria:\n- Feature X works\n\n" +
            "## Phase 02\nREQ-IDs: REQ-02\nSuccess criteria:\n- Feature Y works",
            cancellationToken: CancellationToken.None);
    }

    private static string SingleTaskInput() =>
        "## Task 01\nDepends on: none\nAction: Implement feature X\nAcceptance criteria:\n- Feature works";

    private static string TwoTaskInput() =>
        "## Task 01\nDepends on: none\nAction: Build auth\nAcceptance criteria:\n- done\n\n" +
        "## Task 02\nDepends on: none\nAction: Build profile\nAcceptance criteria:\n- done";

    // ── Test 1: QA gate always injected ───────────────────────────────────────

    [Fact]
    public async Task PlanTasks_AlwaysInjectsQaGateTask()
    {
        // Arrange
        await SetupSinglePhaseProject();

        // Act
        string result = await _tools.PlanTasks("01", SingleTaskInput(),
            cancellationToken: CancellationToken.None);

        // Assert — response should list the qa-gate task
        result.Should().Contain("qa-gate",
            "PlanTasks should always inject a qa-gate task");

        // Verify the qa-gate task was actually stored
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        entries.Should().Contain(e => e.Name.Contains("qa-gate"),
            "qa-gate task should be persisted in the task index");
    }

    // ── Test 2: QA gate depends on all user tasks ─────────────────────────────

    [Fact]
    public async Task PlanTasks_QaGateDependsOnAllUserTasks()
    {
        // Arrange
        await SetupSinglePhaseProject();

        // Act
        string result = await _tools.PlanTasks("01", TwoTaskInput(),
            cancellationToken: CancellationToken.None);

        // Assert — qa-gate should exist and depend on both user tasks
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var qaGateEntry = entries.FirstOrDefault(e => e.Name.Contains("qa-gate"));
        qaGateEntry.Should().NotBeNull("qa-gate task should be stored");
        qaGateEntry!.Keywords.Should().NotBeNull("qa-gate should have keywords");

        // The qa-gate should have depends_on keywords for both user tasks
        var dependsOnKeywords = qaGateEntry.Keywords!
            .Where(k => k.StartsWith("depends_on:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        dependsOnKeywords.Should().HaveCount(2,
            "qa-gate should depend on both user tasks (01 and 02)");
    }

    // ── Test 3: Last phase injects all gates ──────────────────────────────────

    [Fact]
    public async Task PlanTasks_LastPhaseInjectsAllGates()
    {
        // Arrange — single-phase roadmap: phase 01 IS the last phase
        await SetupSinglePhaseProject();

        // Act
        string result = await _tools.PlanTasks("01", SingleTaskInput(),
            cancellationToken: CancellationToken.None);

        // Assert — all four gate tasks should be present
        result.Should().Contain("qa-gate", "QA gate should always be injected");
        result.Should().Contain("evolutionary-gate",
            "evolutionary gate should be injected for the last phase");
        result.Should().Contain("cartographer-gate",
            "cartographer gate should be injected for the last phase");
        result.Should().Contain("march-gate",
            "march gate should be injected for the last phase");
    }

    // ── Test 4: Non-last phase only injects QA gate ───────────────────────────

    [Fact]
    public async Task PlanTasks_NonLastPhaseOnlyInjectsQaGate()
    {
        // Arrange — two-phase roadmap: phase 01 is NOT the last phase
        await SetupTwoPhaseProject();

        // Act
        string result = await _tools.PlanTasks("01", SingleTaskInput(),
            cancellationToken: CancellationToken.None);

        // Assert — only qa-gate should be present, not the last-phase gates
        result.Should().Contain("qa-gate",
            "QA gate should always be injected regardless of phase position");
        result.Should().NotContain("evolutionary-gate",
            "evolutionary gate should NOT be injected for a non-last phase");
        result.Should().NotContain("cartographer-gate",
            "cartographer gate should NOT be injected for a non-last phase");
        result.Should().NotContain("march-gate",
            "march gate should NOT be injected for a non-last phase");
    }

    // ── Test 5: Gate tasks have gate keyword ──────────────────────────────────

    [Fact]
    public async Task PlanTasks_GateTasksHaveGateKeyword()
    {
        // Arrange
        await SetupSinglePhaseProject();

        // Act
        await _tools.PlanTasks("01", SingleTaskInput(),
            cancellationToken: CancellationToken.None);

        // Assert — qa-gate should have "gate:qa" keyword
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var qaGateEntry = entries.FirstOrDefault(e => e.Name.Contains("qa-gate"));
        qaGateEntry.Should().NotBeNull("qa-gate task should exist in index");
        qaGateEntry!.Keywords.Should().NotBeNull("qa-gate should have keywords");
        qaGateEntry.Keywords.Should().Contain("gate:qa",
            "qa-gate task should have a 'gate:qa' keyword for filtering and identification");
    }
}
