using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
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

        // Assert — the task name should include the goal prefix (e.g., "task:g1-abc-01-1-01")
        // The active goal from InitProject + goal_update(add) will be G-1-xxx (init has no structured goals),
        // so the prefix should be "g1-xxx-"
        result.Should().Contain("task:g",
            "plan_tasks should create task memories with goal-prefixed names (task:gN-...)");
        result.Should().MatchRegex(@"task:g\d+-[a-f0-9]+-01-",
            "task name should follow pattern task:g{goalNum}-{hex}-{phaseId}-{wave}-{taskId}");
    }

    // ── Test 3: Branch-safe goal IDs with hex suffix ─────────────────────────

    [Fact]
    public async Task GoalUpdate_Add_ProducesIdWithHexSuffix()
    {
        // Arrange — initialize a project so goal_update has the required context
        await _projTools.ProjectInit("Goals: test branch-safe goal IDs",
            cancellationToken: CancellationToken.None);

        // Act — add a goal
        string result = await _projTools.GoalUpdate("add", "test goal",
            cancellationToken: CancellationToken.None);

        // Assert — the response should contain a goal ID matching G-{num}-{hex3}
        Regex.IsMatch(result, @"G-\d+-[a-f0-9]{3}").Should().BeTrue(
            $"goal ID in response should match pattern G-N-xxx (3-char hex suffix), but got: {result}");
    }

    // ── Test 5: Two adds produce different IDs ───────────────────────────────

    [Fact]
    public async Task GoalUpdate_Add_TwoCallsProduceDifferentIds()
    {
        // Arrange — initialize a project
        await _projTools.ProjectInit("Goals: test unique goal IDs",
            cancellationToken: CancellationToken.None);

        // Act — add two goals
        string result1 = await _projTools.GoalUpdate("add", "first goal",
            cancellationToken: CancellationToken.None);
        string result2 = await _projTools.GoalUpdate("add", "second goal",
            cancellationToken: CancellationToken.None);

        // Extract goal IDs from both responses
        var match1 = Regex.Match(result1, @"G-\d+-[a-f0-9]{3}");
        var match2 = Regex.Match(result2, @"G-\d+-[a-f0-9]{3}");

        match1.Success.Should().BeTrue("first goal_update(add) should produce a branch-safe goal ID");
        match2.Success.Should().BeTrue("second goal_update(add) should produce a branch-safe goal ID");

        // Assert — the two IDs should differ (different sequence numbers and hex suffixes)
        match1.Value.Should().NotBe(match2.Value,
            "two consecutive goal_update(add) calls must produce different goal IDs");
    }

    // ── Test 6: Complete accepts short-form ID ───────────────────────────────

    [Fact]
    public async Task GoalUpdate_Complete_AcceptsShortFormId()
    {
        // Arrange — initialize project and add a goal
        await _projTools.ProjectInit("Goals: test short-form goal completion",
            cancellationToken: CancellationToken.None);

        string addResult = await _projTools.GoalUpdate("add", "goal for short-form completion",
            cancellationToken: CancellationToken.None);

        // Extract the full goal ID (e.g. G-1-a3f)
        var fullIdMatch = Regex.Match(addResult, @"G-(\d+)-[a-f0-9]{3}");
        fullIdMatch.Success.Should().BeTrue("goal_update(add) should return a full goal ID");
        string fullId = fullIdMatch.Value;
        string shortId = $"G-{fullIdMatch.Groups[1].Value}"; // e.g. "G-1"

        // Act — complete using short-form ID (G-1 instead of G-1-a3f)
        string completeResult = await _projTools.GoalUpdate("complete",
            goalId: shortId, outcome: "done",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed (not return an error)
        completeResult.Should().NotStartWith("Error:",
            $"completing with short-form ID '{shortId}' should match full ID '{fullId}'");
        completeResult.Should().Contain("complete",
            "response should confirm the goal was completed");
    }

    // ── Test 7: Complete auto-appends to session memory (AUTO-01) ──────────

    [Fact]
    public async Task GoalUpdate_Complete_AutoAppendsToSessionMemory()
    {
        // Arrange — initialize project and add a goal
        await _projTools.ProjectInit("Goals: test session memory on completion",
            cancellationToken: CancellationToken.None);

        string addResult = await _projTools.GoalUpdate("add", "Test goal for session logging",
            cancellationToken: CancellationToken.None);

        // Extract goal ID from addResult
        var idMatch = Regex.Match(addResult, @"G-\d+-[a-f0-9]{3}");
        idMatch.Success.Should().BeTrue("goal_update(add) should return a goal ID");
        string goalId = idMatch.Value;

        // Act — complete the goal
        string completeResult = await _projTools.GoalUpdate("complete",
            goalId: goalId, outcome: "Done testing",
            cancellationToken: CancellationToken.None);

        completeResult.Should().NotStartWith("Error:",
            "goal completion should succeed");

        // Assert — a sessions:{today} memory should exist with the outcome text
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var store = MemoryStoreContext.Current!;
        var (sessScope, _) = store.ParseQualifiedName($"sessions:{today}");
        var sessEntries = store.LoadIndex(sessScope);

        sessEntries.Should().Contain(e => e.Name.Contains(today),
            $"a sessions:{today} memory should be created when a goal is completed");

        // Verify the session memory content includes the outcome text
        string artifact = await store.ReadArtifactAsync(today, sessScope);
        artifact.Should().NotBeNullOrEmpty("session memory artifact should have content");

        // Decode the artifact to check it contains the outcome
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        string content = System.Text.Encoding.UTF8.GetString(decoded);
        content.Should().Contain("Done testing",
            "session memory should contain the goal outcome text");
    }

    // ── Test 8: Complete mentions march report in post-goal guidance ─────────

    [Fact]
    public async Task GoalUpdate_Complete_MentionsMarchReport()
    {
        // Arrange — initialize project and add a goal
        await _projTools.ProjectInit("Goals: test march report mention",
            cancellationToken: CancellationToken.None);

        string addResult = await _projTools.GoalUpdate("add", "Test goal for march report mention",
            cancellationToken: CancellationToken.None);

        var idMatch = Regex.Match(addResult, @"G-\d+-[a-f0-9]{3}");
        idMatch.Success.Should().BeTrue("goal_update(add) should return a goal ID");
        string goalId = idMatch.Value;

        // Act — complete the goal
        string completeResult = await _projTools.GoalUpdate("complete",
            goalId: goalId, outcome: "Done",
            cancellationToken: CancellationToken.None);

        // Assert — response should complete and mention march report in post-goal guidance
        completeResult.Should().NotStartWith("Error:",
            "goal completion should succeed");
        completeResult.Should().Contain("march report",
            "post-goal guidance should mention producing a march report");
    }
}
