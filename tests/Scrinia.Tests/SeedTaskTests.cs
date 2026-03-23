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
        await _tools.ProjectInit(
            "Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — add a goal
        await _tools.GoalUpdate("add", "Implement authentication",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — a task with "researcher" in the name must exist with correct keywords
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var researcherEntry = entries.FirstOrDefault(e => e.Name.Contains("researcher"));
        researcherEntry.Should().NotBeNull("goal('add') should create a researcher seed task");
        researcherEntry!.Keywords.Should().Contain("status:pending",
            "researcher task must have status:pending keyword");
        researcherEntry.Keywords.Should().Contain("wave:0",
            "researcher task must be wave 0 to run first");
        researcherEntry.Keywords.Should().Contain("phase:00",
            "researcher task must have phase:00 keyword");
        researcherEntry.Keywords.Should().Contain("gate:researcher",
            "researcher task must have gate:researcher keyword");
    }

    [Fact]
    public async Task GoalAdd_CreatesAllThreeSeedTasks()
    {
        // Arrange — init project so goal_update prerequisite passes
        await _tools.ProjectInit(
            "Goals:\n- Build the API\n- Create the UI\n- Ship MVP",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — add a goal
        await _tools.GoalUpdate("add", "Implement search feature",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — all three seed tasks must exist with correct keywords
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        // Researcher (wave 0 — research first for full context)
        var researcherEntry = entries.FirstOrDefault(e => e.Name.Contains("researcher"));
        researcherEntry.Should().NotBeNull("goal('add') should create a researcher seed task");
        researcherEntry!.Keywords.Should().Contain("wave:0");
        researcherEntry.Keywords.Should().Contain("phase:00");
        researcherEntry.Keywords.Should().Contain("gate:researcher");

        // Auditor (wave 1, depends on researcher)
        var auditorEntry = entries.FirstOrDefault(e => e.Name.Contains("auditor"));
        auditorEntry.Should().NotBeNull("goal('add') should create an auditor seed task");
        auditorEntry!.Keywords.Should().Contain("status:pending");
        auditorEntry.Keywords.Should().Contain("wave:1",
            "auditor task must be wave 1 to run after researcher");
        auditorEntry.Keywords.Should().Contain("phase:00");
        auditorEntry.Keywords.Should().Contain("gate:auditor");
        auditorEntry.Keywords.Should().Contain(k => k.StartsWith("depends_on:") && k.Contains("researcher"),
            "auditor task must depend on researcher");

        // Planner (wave 2, depends on auditor)
        var plannerEntry = entries.FirstOrDefault(e => e.Name.Contains("planner"));
        plannerEntry.Should().NotBeNull("goal('add') should create a planner seed task");
        plannerEntry!.Keywords.Should().Contain("status:pending");
        plannerEntry.Keywords.Should().Contain("wave:2",
            "planner task must be wave 2 to run after auditor");
        plannerEntry.Keywords.Should().Contain("phase:00");
        plannerEntry.Keywords.Should().Contain("gate:planner");
        plannerEntry.Keywords.Should().Contain(k => k.StartsWith("depends_on:") && k.Contains("auditor"),
            "planner task must depend on auditor");
    }

    [Fact]
    public async Task GoalAdd_ResponseMentionsResearcherTask()
    {
        // Arrange
        await _tools.ProjectInit(
            "Goals:\n- Build the API",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _tools.GoalUpdate("add", "Add logging",
            null, null, cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("Researcher task created",
            "goal('add') response must mention the researcher task was created");
        result.Should().Contain("Auditor and planner seed tasks queued",
            "goal('add') response must mention auditor and planner tasks were queued");
        result.Should().Contain("task('next')",
            "goal('add') response must mention task('next') to continue");
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
        await _tools.ProjectInit("Goals: explore existing project",
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
        onboarderEntry.Keywords.Should().Contain("gate:onboarder",
            "onboarder task must have gate:onboarder keyword");
    }

    [Fact]
    public async Task ProjectInit_WithoutExistingCode_DoesNotCreateOnboarderTask()
    {
        // Arrange — workspace has only .scrinia/ (dot-prefixed), so hasExistingCode is false
        var store = MemoryStoreContext.Current!;

        // Act
        await _tools.ProjectInit("Goals: start fresh project",
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
        string result = await _tools.ProjectInit("Goals: onboard to existing project",
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().Contain("Onboarder task created",
            "project_init response with existing code must mention the onboarder task");
    }
}
