using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for seed tasks auto-created by goal('add') (auditor) and plan('init') (onboarder).
/// </summary>
public sealed class SeedTaskTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public SeedTaskTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Seed task tests (goal 'add') — researcher → auditor → planner ─────

    [Fact]
    public async Task GoalAdd_CreatesResearcherSeedTask()
    {
        // Arrange — init project so goal_update prerequisite passes
        await ScriniaProjectTools.ProjectInit(
            "Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — add a goal
        await ScriniaProjectTools.GoalUpdate("add", "Implement authentication",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — a task with "researcher" in the name must exist with correct keywords
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var researcherEntry = entries.FirstOrDefault(e => e.Name.Contains("researcher"));
        researcherEntry.Should().NotBeNull("goal('add') should create a researcher seed task");
        researcherEntry!.Keywords.Should().Contain("status:pending",
            "researcher task must have status:pending keyword");
        researcherEntry.Keywords.Should().Contain("wave:1",
            "researcher task must be wave 1 to run after agent-specialist");
        researcherEntry.Keywords.Should().Contain("phase:00",
            "researcher task must have phase:00 keyword");
        researcherEntry.Keywords.Should().Contain("tag:researcher",
            "researcher task must have gate:researcher keyword");
    }

    [Fact]
    public async Task GoalAdd_CreatesAllThreeSeedTasks()
    {
        // Arrange — init project so goal_update prerequisite passes
        await ScriniaProjectTools.ProjectInit(
            "Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — add a goal
        await ScriniaProjectTools.GoalUpdate("add", "Implement search feature",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — all three seed tasks must exist with correct keywords
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        // Agent-specialist (wave 0 — assess skill fit first)
        var agentSpecialistEntry = entries.FirstOrDefault(e => e.Name.Contains("agent-specialist"));
        agentSpecialistEntry.Should().NotBeNull("goal('add') should create an agent-specialist seed task");
        agentSpecialistEntry!.Keywords.Should().Contain("wave:0");
        agentSpecialistEntry.Keywords.Should().Contain("phase:00");
        agentSpecialistEntry.Keywords.Should().Contain("tag:agent-specialist");

        // Researcher (wave 1, depends on agent-specialist)
        var researcherEntry = entries.FirstOrDefault(e => e.Name.Contains("researcher"));
        researcherEntry.Should().NotBeNull("goal('add') should create a researcher seed task");
        researcherEntry!.Keywords.Should().Contain("wave:1");
        researcherEntry.Keywords.Should().Contain("phase:00");
        researcherEntry.Keywords.Should().Contain("tag:researcher");
        researcherEntry.Keywords.Should().Contain(k => k.StartsWith("depends_on:") && k.Contains("agent-specialist"),
            "researcher task must depend on agent-specialist");

        // Auditor (wave 2, depends on researcher)
        var auditorEntry = entries.FirstOrDefault(e => e.Name.Contains("auditor"));
        auditorEntry.Should().NotBeNull("goal('add') should create an auditor seed task");
        auditorEntry!.Keywords.Should().Contain("status:pending");
        auditorEntry.Keywords.Should().Contain("wave:2",
            "auditor task must be wave 2 to run after researcher");
        auditorEntry.Keywords.Should().Contain("phase:00");
        auditorEntry.Keywords.Should().Contain("tag:auditor");
        auditorEntry.Keywords.Should().Contain(k => k.StartsWith("depends_on:") && k.Contains("researcher"),
            "auditor task must depend on researcher");

        // Planner (wave 3, depends on auditor)
        var plannerEntry = entries.FirstOrDefault(e => e.Name.Contains("planner"));
        plannerEntry.Should().NotBeNull("goal('add') should create a planner seed task");
        plannerEntry!.Keywords.Should().Contain("status:pending");
        plannerEntry.Keywords.Should().Contain("wave:3",
            "planner task must be wave 3 to run after auditor");
        plannerEntry.Keywords.Should().Contain("phase:00");
        plannerEntry.Keywords.Should().Contain("tag:planner");
        plannerEntry.Keywords.Should().Contain(k => k.StartsWith("depends_on:") && k.Contains("auditor"),
            "planner task must depend on auditor");
    }

    [Fact]
    public async Task GoalAdd_ResponseMentionsResearcherTask()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit(
            "Goals:\n- Build the API",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.GoalUpdate("add", "Add logging",
            null, null, cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "goal('add') response should be successful");
        parsed.Content.Should().Contain("Seed tasks created",
            "goal('add') response must mention seed tasks were created");
        parsed.Content.Should().Contain("researcher",
            "goal('add') response must mention the researcher seed task");
        parsed.Content.Should().Contain("auditor",
            "goal('add') response must mention the auditor seed task");
        parsed.Content.Should().Contain("planner",
            "goal('add') response must mention the planner seed task");
        parsed.Instruction.Should().Contain("task('next',",
            "goal('add') instruction must mention task('next') with path to continue");
        parsed.Instruction.Should().Contain("Confirm this goal with the user",
            "goal('add') instruction must ask for user confirmation before proceeding");
    }

    // ── Onboarder seed task tests (plan 'init') ──────────────────────────────

    [Fact]
    public async Task ProjectInit_WithExistingCode_CreatesOnboarderTask()
    {
        // Arrange — create a non-dot file in the workspace so hasExistingCode is true
        string dummyFile = Path.Combine(_scope.WorkspaceDir, "Program.cs");
        await File.WriteAllTextAsync(dummyFile, "// existing code");

        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.ProjectInit("Goals: explore existing project",
            cancellationToken: CancellationToken.None);

        // Assert — a task with "onboarder" in the name must exist with correct keywords
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var onboarderEntry = entries.FirstOrDefault(e => e.Name.Contains("onboarder"));
        onboarderEntry.Should().NotBeNull("project_init with existing code should create an onboarder seed task");
        onboarderEntry!.Keywords.Should().Contain("status:pending",
            "onboarder task must have status:pending keyword");
        onboarderEntry.Keywords.Should().Contain("wave:0",
            "onboarder task must be wave 0");
        onboarderEntry.Keywords.Should().Contain("phase:init",
            "onboarder task must have phase:init keyword");
        onboarderEntry.Keywords.Should().Contain("tag:onboarder",
            "onboarder task must have gate:onboarder keyword");
    }

    [Fact]
    public async Task ProjectInit_WithoutExistingCode_DoesNotCreateOnboarderTask()
    {
        // Arrange — workspace has only .scrinia/ (dot-prefixed), so hasExistingCode is false
        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.ProjectInit("Goals: start fresh project",
            cancellationToken: CancellationToken.None);

        // Assert — no onboarder task should exist
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var onboarderEntry = entries.FirstOrDefault(e => e.Name.Contains("onboarder"));
        onboarderEntry.Should().BeNull(
            "project_init without existing code should NOT create an onboarder seed task");
    }

    [Fact]
    public async Task ProjectInit_WithExistingCode_ResponseMentionsOnboarder()
    {
        // Arrange — create a non-dot file so hasExistingCode is true
        string dummyFile = Path.Combine(_scope.WorkspaceDir, "app.js");
        await File.WriteAllTextAsync(dummyFile, "// existing app");

        // Act
        string result = await ScriniaProjectTools.ProjectInit("Goals: onboard to existing project",
            cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "project_init response should be successful");
        parsed.Content.Should().Contain("Onboarder task created",
            "project_init response with existing code must mention the onboarder task");
    }
}
