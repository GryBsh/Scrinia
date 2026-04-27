using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for the goal_update MCP tool:
/// GOAL-01: Add goals dynamically without re-initializing the project
/// GOAL-02: Mark a goal complete with an outcome note
/// GOAL-04: Original goal count is recorded at init time and immutable
/// ADOPT-02: Description context signals
/// </summary>
public sealed class GoalToolTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public GoalToolTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    /// <summary>Sets up a project with 3 goals so goal_update prerequisite check passes.</summary>
    private async Task InitProject()
    {
        await ScriniaProjectTools.ProjectInit(
            "Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            CancellationToken.None);
    }

    // ── GOAL-01 tests (add action) ────────────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_Add_AppendsGoalToContext()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.GoalUpdate("add", "New goal added dynamically", null, null, cancellationToken: CancellationToken.None);

        // Assert — project:context must contain the new goal description
        string context = await ReadMemoryText(store, "project:context");
        context.Should().Contain("New goal added dynamically",
            "goal_update(add) should append the new goal to project:context");
    }

    [Fact]
    public async Task GoalUpdate_Add_ResponseConfirmsStorage()
    {
        // Arrange
        await InitProject();

        // Act
        string response = await ScriniaProjectTools.GoalUpdate("add", "Deploy to production", null, null, cancellationToken: CancellationToken.None);

        // Assert — response must mention the goal description
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "goal_update(add) should succeed");
        r.Content.Should().Contain("Deploy to production",
            "goal_update(add) response should confirm the stored goal description");
    }

    [Fact]
    public async Task GoalUpdate_Add_RequiresProjectInit()
    {
        // Act — call goal_update without calling project_init first
        string response = await ScriniaProjectTools.GoalUpdate("add", "orphan goal", null, null, cancellationToken: CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("error",
            "goal_update without project_init should return an error response");
    }

    [Fact]
    public async Task GoalUpdate_Add_MultipleTimes()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;

        // Act — add two goals sequentially
        await ScriniaProjectTools.GoalUpdate("add", "First extra goal", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Second extra goal", null, null, cancellationToken: CancellationToken.None);

        // Assert — project:context must contain both new goals
        string context = await ReadMemoryText(store, "project:context");
        context.Should().Contain("First extra goal",
            "project:context should contain the first added goal");
        context.Should().Contain("Second extra goal",
            "project:context should contain the second added goal");
    }

    // ── GOAL-02 tests (complete action) ───────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_Complete_MarksGoalDone()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;
        await ScriniaProjectTools.GoalUpdate("add", "Goal to complete", null, null, cancellationToken: CancellationToken.None);

        // Act — complete the goal (added goal will be G-1 after the 3 init goals)
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-4", "Shipped it", cancellationToken: CancellationToken.None);

        // Assert — project:context must show complete status and outcome
        string context = await ReadMemoryText(store, "project:context");
        context.Should().Contain("complete",
            "completed goal should have 'complete' status in project:context");
        context.Should().Contain("Shipped it",
            "completed goal should include outcome text in project:context");
    }

    [Fact]
    public async Task GoalUpdate_Complete_InvalidGoalId()
    {
        // Arrange
        await InitProject();

        // Act — try to complete a nonexistent goal ID
        string response = await ScriniaProjectTools.GoalUpdate("complete", null, "G-999", "some outcome", cancellationToken: CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("error",
            "completing a nonexistent goal ID should return an error response");
    }

    [Fact]
    public async Task GoalUpdate_Complete_RecordsTimestamp()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;
        await ScriniaProjectTools.GoalUpdate("add", "Goal with timestamp", null, null, cancellationToken: CancellationToken.None);

        // Act — complete the newly added goal
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-4", "Done", cancellationToken: CancellationToken.None);

        // Assert — completed section must contain an ISO format timestamp (year 20XX)
        string context = await ReadMemoryText(store, "project:context");
        context.Should().MatchRegex(@"20\d{2}-\d{2}-\d{2}",
            "completed goal should record an ISO-format timestamp");
    }

    // ── GOAL-04 tests (original count immutability) ───────────────────────────

    [Fact]
    public async Task GoalUpdate_OriginalCountPreserved()
    {
        // Arrange — project_init with 3 goals, then add 2 more
        await InitProject();
        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.GoalUpdate("add", "Extra goal 1", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Extra goal 2", null, null, cancellationToken: CancellationToken.None);

        // Assert — project:context must show original count was 3
        string context = await ReadMemoryText(store, "project:context");
        context.Should().MatchRegex(@"[Oo]riginal goals?:\s*3",
            "project:context should record the original goal count as 3");
    }

    [Fact]
    public async Task GoalUpdate_OriginalCountImmutable()
    {
        // Arrange — project_init then add one goal
        await InitProject();
        var store = MemoryStoreContext.Current!;
        await ScriniaProjectTools.GoalUpdate("add", "First dynamic goal", null, null, cancellationToken: CancellationToken.None);

        // Get the original count after first add
        string contextAfterFirst = await ReadMemoryText(store, "project:context");

        // Act — add another goal
        await ScriniaProjectTools.GoalUpdate("add", "Second dynamic goal", null, null, cancellationToken: CancellationToken.None);

        // Assert — original count marker has not changed after second add
        string contextAfterSecond = await ReadMemoryText(store, "project:context");
        var firstMatch = System.Text.RegularExpressions.Regex.Match(contextAfterFirst, @"[Oo]riginal goals?:\s*(\d+)");
        var secondMatch = System.Text.RegularExpressions.Regex.Match(contextAfterSecond, @"[Oo]riginal goals?:\s*(\d+)");

        firstMatch.Success.Should().BeTrue("original count should be present after first add");
        secondMatch.Success.Should().BeTrue("original count should be present after second add");
        firstMatch.Groups[1].Value.Should().Be(secondMatch.Groups[1].Value,
            "original count should not change after adding more goals");
    }

    // ── List action tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_List_ReturnsAllGoals()
    {
        // Arrange — init with 2 goals, then add 1 more
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI", CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Deploy to cloud", null, null, cancellationToken: CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.GoalUpdate("list", null, null, null, cancellationToken: CancellationToken.None);

        // Assert — all 3 goal descriptions should appear in the response
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "goal list should succeed");
        r.Content.Should().Contain("Build the API",
            "list response should include the first init goal");
        r.Content.Should().Contain("Create the UI",
            "list response should include the second init goal");
        r.Content.Should().Contain("Deploy to cloud",
            "list response should include the added dynamic goal");
    }

    [Fact]
    public async Task GoalUpdate_List_ShowsStatus()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build something\n- Ship it", CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Monitor it", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-3", "Monitoring in place", cancellationToken: CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.GoalUpdate("list", null, null, null, cancellationToken: CancellationToken.None);

        // Assert — completed goals show "complete", active goals show "active" or "pending"
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "goal list should succeed");
        r.Content.Should().Contain("complete",
            "list response should show 'complete' status for the completed goal");
        (r.Content!.Contains("active") || r.Content.Contains("pending"))
            .Should().BeTrue("list response should show active/pending status for incomplete goals");
    }

    [Fact]
    public async Task GoalUpdate_List_EmptyProject()
    {
        // Arrange — init project with a general description (no "Goals:" section)
        await ScriniaProjectTools.ProjectInit("A project context with no explicit goals section", CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.GoalUpdate("list", null, null, null, cancellationToken: CancellationToken.None);

        // Assert — should not return an error, just a sensible empty-state response
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success",
            "list on a project with no structured goals should return a sensible empty-state response");
    }

    // ── ADOPT-02 context signal test ──────────────────────────────────────────

    [Fact]
    public void GoalUpdate_InternalMethodExists()
    {
        // GoalUpdate is an internal method routed through EntityDispatch.
        var internalMethod = typeof(ScriniaProjectTools).GetMethod("GoalUpdate",
            BindingFlags.NonPublic | BindingFlags.Static);
        internalMethod.Should().NotBeNull("GoalUpdate must exist as an internal method");

        // EntityDispatch is the public entry point for goal operations
        var entityDispatch = typeof(ScriniaProjectTools).GetMethod("EntityDispatch",
            BindingFlags.Public | BindingFlags.Static);
        entityDispatch.Should().NotBeNull("EntityDispatch must exist as the entity routing entry point");
    }

    // ── GOAL-03 tests (plan_status goal delta) ────────────────────────────────

    [Fact]
    public async Task PlanStatus_ShowsGoalDelta()
    {
        // Arrange — init project with 2 goals, add 1 more via goal_update
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI", CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Deploy to production", null, null, cancellationToken: CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response must contain goal delta text showing "2 original + 1 added" or equivalent
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "plan_status should succeed");
        (r.Content!.Contains("original") && r.Content.Contains("added"))
            .Should().BeTrue(
                "plan_status should show goal delta (original count vs current count) when goals have been added");
        r.Content.Should().Contain("Goals:",
            "plan_status should include a 'Goals:' line when goals exist");
    }

    [Fact]
    public async Task PlanStatus_NoGoalLineWhenNoGoals()
    {
        // Arrange — init project with NO explicit goals section
        await ScriniaProjectTools.ProjectInit("A project context with no explicit goals section", CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response must NOT contain a "Goals:" line when no goals
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "plan_status should succeed");
        r.Content.Should().NotContain("Goals:",
            "plan_status should not show a Goals: line when project has no goals section");
    }

    [Fact]
    public async Task PlanStatus_OriginalOnlyNoAddedNote()
    {
        // Arrange — init project with 2 goals, do NOT add any
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI", CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.PlanStatus(CancellationToken.None);

        // Assert — response shows "Goals: 2" without "added" text
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "plan_status should succeed");
        r.Content.Should().Contain("Goals:",
            "plan_status should include a 'Goals:' line when goals exist");
        r.Content.Should().NotContain("added",
            "plan_status should NOT show 'added' text when no goals have been added");
    }

    // ── Backlog inline search tests ──────────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_Add_ShowsMatchingBacklogItems()
    {
        // Arrange — init project and create a backlog entry with matching keywords
        await InitProject();
        var store = MemoryStoreContext.Current!;
        var (backlogScope, _) = store.ParseQualifiedName("backlog:placeholder");
        store.Upsert(new ArtifactEntry(
            "improve-testing", "file://b1", 100, 1, DateTimeOffset.UtcNow,
            "Improve test coverage for API endpoints",
            Keywords: ["testing", "coverage", "endpoints"]), backlogScope);

        // Act — add a goal whose description overlaps with the backlog entry
        string response = await ScriniaProjectTools.GoalUpdate("add",
            "Add comprehensive testing for API endpoints",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — response should contain the backlog entry
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "goal_update(add) should succeed");
        r.Content.Should().Contain("Related backlog items:",
            "goal_update(add) should show related backlog items when keywords match");
        r.Content.Should().Contain("/backlog/improve-testing",
            "goal_update(add) should list the matching backlog entry name");
    }

    [Fact]
    public async Task GoalUpdate_Add_NoMatchingBacklog()
    {
        // Arrange — init project and create a backlog entry with unrelated keywords
        await InitProject();
        var store = MemoryStoreContext.Current!;
        var (backlogScope, _) = store.ParseQualifiedName("backlog:placeholder");
        store.Upsert(new ArtifactEntry(
            "database-migration", "file://b2", 100, 1, DateTimeOffset.UtcNow,
            "Migrate from SQLite to PostgreSQL",
            Keywords: ["database", "migration", "postgresql"]), backlogScope);

        // Act — add a goal with completely unrelated description
        string response = await ScriniaProjectTools.GoalUpdate("add",
            "Improve documentation for onboarding",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — response should NOT contain backlog section
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "goal_update(add) should succeed");
        r.Content.Should().NotContain("Related backlog items:",
            "goal_update(add) should not show backlog section when no entries match");
    }

    [Fact]
    public async Task GoalUpdate_Add_NoBacklogEntries()
    {
        // Arrange — init project with no backlog entries at all
        await InitProject();

        // Act — add a goal
        string response = await ScriniaProjectTools.GoalUpdate("add",
            "Build the authentication system",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — response should not contain backlog section and should not error
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success",
            "goal_update(add) should succeed even when no backlog topic exists");
        r.Content.Should().NotContain("Related backlog items:",
            "goal_update(add) should not show backlog section when no backlog entries exist");
        r.Content.Should().Contain("Goal added as",
            "goal_update(add) should still confirm the goal was added");
    }

    // ── Edit action tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GoalUpdate_Edit_ActiveGoal_UpdatesDescription()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;
        await ScriniaProjectTools.GoalUpdate("add", "Original description", null, null, cancellationToken: CancellationToken.None);

        // Act — edit the newly added goal (G-4)
        string response = await ScriniaProjectTools.GoalUpdate("edit", "Updated description", "G-4", null, cancellationToken: CancellationToken.None);

        // Assert — response confirms old and new descriptions
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "edit should succeed");
        r.Content.Should().Contain("Old:", "edit response should show the old description");
        r.Content.Should().Contain("New:", "edit response should show the new description");
        r.Content.Should().Contain("Updated description", "edit response should contain the new description text");

        // Verify via list
        string listResponse = await ScriniaProjectTools.GoalUpdate("list", null, null, null, cancellationToken: CancellationToken.None);
        var lr = ResponseParser.Parse(listResponse);
        lr.Content.Should().Contain("Updated description",
            "list response should show the updated description after edit");
        lr.Content.Should().NotContain("Original description",
            "list response should no longer contain the old description after edit");
    }

    [Fact]
    public async Task GoalUpdate_Edit_CompletedGoal_PreservesOutcome()
    {
        // Arrange
        await InitProject();
        var store = MemoryStoreContext.Current!;
        await ScriniaProjectTools.GoalUpdate("add", "Goal to complete then edit", null, null, cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("complete", null, "G-4", "Shipped successfully", cancellationToken: CancellationToken.None);

        // Act — edit the completed goal's description
        string response = await ScriniaProjectTools.GoalUpdate("edit", "Revised completed goal", "G-4", null, cancellationToken: CancellationToken.None);

        // Assert — outcome and timestamp are preserved
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "edit should succeed");
        r.Content.Should().Contain("Old:", "edit response should show the old description");
        r.Content.Should().Contain("New:", "edit response should show the new description");

        string listResponse = await ScriniaProjectTools.GoalUpdate("list", null, null, null, cancellationToken: CancellationToken.None);
        var lr = ResponseParser.Parse(listResponse);
        lr.Content.Should().Contain("Revised completed goal",
            "list should show the new description for the edited completed goal");
        lr.Content.Should().Contain("Outcome:",
            "list should still show the outcome after editing a completed goal");
        lr.Content.Should().Contain("Shipped successfully",
            "list should preserve the original outcome text after edit");
    }

    [Fact]
    public async Task GoalUpdate_Edit_MissingGoalId_ReturnsError()
    {
        // Arrange
        await InitProject();

        // Act — call edit with no goalId
        string response = await ScriniaProjectTools.GoalUpdate("edit", "Some new description", null, null, cancellationToken: CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("error",
            "edit without goalId should return an error");
        r.Error.Should().Contain("goalId",
            "error message should mention goalId is required");
    }

    [Fact]
    public async Task GoalUpdate_Edit_EmptyDescription_ReturnsError()
    {
        // Arrange
        await InitProject();

        // Act — call edit with goalId but empty description
        string response = await ScriniaProjectTools.GoalUpdate("edit", "", "G-1", null, cancellationToken: CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("error",
            "edit with empty description should return an error");
        r.Error.Should().Contain("description",
            "error message should mention description is required");
    }

    [Fact]
    public async Task GoalUpdate_Edit_NonexistentGoal_ReturnsError()
    {
        // Arrange
        await InitProject();

        // Act — call edit with a goalId that doesn't exist
        string response = await ScriniaProjectTools.GoalUpdate("edit", "New description", "G-999", null, cancellationToken: CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("error",
            "editing a nonexistent goal should return an error");
        r.Error.Should().Contain("not found",
            "error should indicate the goal was not found");
    }
}
