using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for goal ID non-repetition and goal-prefixed memory names:
///   1. goal_update(add) uses max(existing IDs) not count to avoid ID collisions
///   2. plan_tasks creates task memories with goal-prefixed names
///   3. research_start creates research memories with goal-prefixed names
/// </summary>
public sealed class GoalPrefixTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaMcpTools _memTools;
    private readonly ScriniaProjectTools _projTools;

    public GoalPrefixTests()
    {
        _scope = new TestHelpers.StoreScope();
        _memTools = new ScriniaMcpTools();
        _projTools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Test 1: Goal ID non-repetition ────────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_Add_UsesMaxIdNotCount()
    {
        // Arrange — create a project:context with non-sequential goal IDs (G-1 and G-5),
        // simulating a scenario where goals G-2, G-3, G-4 were cleaned up.
        string contextWithGaps =
            "Project context for testing goal ID assignment.\n\n" +
            "## Goals\n" +
            "Original goals: 1\n" +
            "- [G-1] [complete] First goal | Outcome: done\n" +
            "- [G-5] [complete] Fifth goal | Outcome: done";

        // Use store() to manually write the project:context memory with specific content
        await _memTools.Store(
            [contextWithGaps],
            "project:context",
            description: "Test project context with non-sequential goal IDs");

        // Also need project:state for goal_update to work
        await _memTools.Store(
            ["Project: test\nID: test\nPhase: Not started\nProgress: 0%\nLast action: init\nBlockers: none\nNext step: add goal"],
            "project:state",
            description: "Test project state");

        // Act — add a new goal; it should be G-6, not G-3 (which would collide if count were used)
        string result = await _projTools.GoalUpdate("add", "New goal after cleanup",
            cancellationToken: CancellationToken.None);

        // Assert — the response must contain G-6 (max existing ID 5 + 1), not G-3 (count 2 + 1)
        result.Should().Contain("G-6",
            "goal_update(add) should assign G-6 because the highest existing ID is G-5, " +
            "not G-3 which would result from using goal count (2) + 1");
        result.Should().NotContain("G-3",
            "goal_update(add) must not reuse G-3 when G-5 already exists");
    }

    // ── Test 2: plan_tasks creates goal-prefixed task names ───────────────────

    [Fact]
    public async Task PlanTasks_GoalPrefixedNames()
    {
        // Arrange — full project setup: init, add goal, requirements, roadmap
        await _projTools.ProjectInit("Goals: test goal-prefixed task names",
            cancellationToken: CancellationToken.None);
        await _projTools.GoalUpdate("add", "Test goal for task prefix",
            cancellationToken: CancellationToken.None);
        await _projTools.PlanRequirements(
            "- REQ-01: verify task naming includes goal prefix",
            cancellationToken: CancellationToken.None);
        await _projTools.PlanRoadmap(
            "### Phase 01\nREQ-01 task naming verification\nSuccess criteria:\n- Tasks have goal-prefixed names",
            cancellationToken: CancellationToken.None);

        string taskDef = """
            ## Task 01
            Depends on: none
            Action: verify goal prefix in task names
            Acceptance criteria:
            - Task name includes goal prefix
            """;

        // Act
        string result = await _projTools.PlanTasks("01", taskDef,
            cancellationToken: CancellationToken.None);

        // Assert — the task name should include the goal prefix (e.g., "task:g1-01-1-01")
        // The active goal from InitProject + goal_update(add) will be G-1 (init has no structured goals),
        // so the prefix should be "g1-"
        result.Should().Contain("task:g",
            "plan_tasks should create task memories with goal-prefixed names (task:gN-...)");
        result.Should().MatchRegex(@"task:g\d+-01-",
            "task name should follow pattern task:g{goalNum}-{phaseId}-{wave}-{taskId}");
    }

    // ── Test 3: research_start creates goal-prefixed research names ───────────

    [Fact]
    public async Task ResearchStart_GoalPrefixedName()
    {
        // Arrange — set up a project with an active goal
        await _projTools.ProjectInit("Goals: test goal-prefixed research names",
            cancellationToken: CancellationToken.None);
        await _projTools.GoalUpdate("add", "Test goal for research prefix",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _projTools.ResearchStart("01", "naming",
            "How are research memories named with goal prefixes?",
            cancellationToken: CancellationToken.None);

        // Assert — the research memory name should include the goal prefix
        // With active goal G-1, the name should be "research:g1-01-naming"
        result.Should().Contain("research:g",
            "research_start should create a research memory with goal-prefixed name (research:gN-...)");
        result.Should().MatchRegex(@"research:g\d+-01-naming",
            "research memory name should follow pattern research:g{goalNum}-{phaseId}-{topic}");
    }
}
