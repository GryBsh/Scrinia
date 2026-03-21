using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for organic adoption signals (ADOPT-01, ADOPT-03) and learning feedback:
/// - guide() text covers all v2.0 tools and topic namespaces (ADOPT-01)
/// - plan_resume surfaces unused capability hints (ADOPT-03)
/// - plan_roadmap and plan_tasks query learn:patterns before responding
/// </summary>
public sealed class OrganicAdoptionTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public OrganicAdoptionTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals:\n- Build the API\n- Create the UI", CancellationToken.None);
    }

    private async Task InitProjectWithRequirements()
    {
        await InitProject();
        await _tools.PlanRequirements("## v1 Requirements\n- REQ-01: Core feature", CancellationToken.None);
    }

    private static string SimpleRoadmap => "### Phase 1\nREQ-01: Core feature\nSuccess criteria:\n- Core feature shipped";

    private static string SimpleTasks =>
        "## Task 01\nWave: 1\nDepends on: none\nAction: do something\nAcceptance criteria:\n- criterion 1";

    private async Task StorePatternsMemory()
    {
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("learn:patterns");
        string content = "Pattern: use research_start before planning. Pattern: log concerns early.";
        string artifact = Nmp2ChunkedEncoder.Encode(content);
        await store.WriteArtifactAsync(subject, scope, artifact, CancellationToken.None);
        long bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        var entry = new Scrinia.Core.Models.ArtifactEntry(
            Name: subject,
            Uri: store.ArtifactUri(subject, scope),
            OriginalBytes: bytes,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: content[..Math.Min(100, content.Length)],
            Keywords: null,
            UpdatedAt: null);
        store.Upsert(entry, scope);
    }

    // ── ADOPT-03: plan_resume capability hints ────────────────────────────────

    [Fact]
    public async Task PlanResume_HintsUnusedConcernTracking()
    {
        // Arrange — project init but no concern_add called
        await InitProject();

        // Act
        string response = await _tools.PlanResume(CancellationToken.None);

        // Assert — hint about unused concern tracking must appear
        (response.Contains("hint", StringComparison.OrdinalIgnoreCase) ||
         response.Contains("concern", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "plan_resume should surface a hint about unused concern tracking when concern_add has never been called");
    }

    [Fact]
    public async Task PlanResume_HintsUnusedKnowledge()
    {
        // Arrange — project init but no store of domain knowledge
        await InitProject();

        // Act
        string response = await _tools.PlanResume(CancellationToken.None);

        // Assert — hint about persisting knowledge must appear
        (response.Contains("knowledge", StringComparison.OrdinalIgnoreCase) ||
         response.Contains("store", StringComparison.OrdinalIgnoreCase) ||
         response.Contains("topic", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "plan_resume should surface a hint about persisting domain knowledge");
    }

    [Fact]
    public async Task PlanResume_NoHintWhenConcernsExist()
    {
        // Arrange — init project and add a concern
        await InitProject();
        await _tools.ConcernAdd("Test risk", "low", "01", id: "test-risk", CancellationToken.None);

        // Act
        string response = await _tools.PlanResume(CancellationToken.None);

        // Assert — unused-concern hint text must NOT appear
        response.Should().NotContain(
            "concern tracking is available",
            "plan_resume should NOT show an unused concern hint when concerns have been logged");
    }

    // ── Learning feed-in: plan_roadmap queries learn:patterns ─────────────────

    [Fact]
    public async Task PlanRoadmap_IncludesLearnPatterns()
    {
        // Arrange — store a learn:patterns memory, then call plan_roadmap
        await InitProjectWithRequirements();
        await StorePatternsMemory();

        // Act
        string response = await _tools.PlanRoadmap(SimpleRoadmap, CancellationToken.None);

        // Assert — response must contain Pattern or Learn to indicate patterns were surfaced
        (response.Contains("Pattern", StringComparison.OrdinalIgnoreCase) ||
         response.Contains("Learn", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "plan_roadmap should surface learn:patterns content in its response when patterns exist");
    }

    [Fact]
    public async Task PlanTasks_IncludesLearnPatterns()
    {
        // Arrange — store learn:patterns plus a valid plan:roadmap
        await InitProjectWithRequirements();
        await _tools.PlanRoadmap(SimpleRoadmap, CancellationToken.None);
        await StorePatternsMemory();

        // Act
        string response = await _tools.PlanTasks("01", SimpleTasks, CancellationToken.None);

        // Assert — response must contain Pattern or Learn substring
        (response.Contains("Pattern", StringComparison.OrdinalIgnoreCase) ||
         response.Contains("Learn", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "plan_tasks should surface learn:patterns content in its response when patterns exist");
    }

    [Fact]
    public async Task PlanRoadmap_NoErrorWhenNoPatternsExist()
    {
        // Arrange — no learn:patterns stored
        await InitProjectWithRequirements();

        // Act
        string response = await _tools.PlanRoadmap(SimpleRoadmap, CancellationToken.None);

        // Assert — must NOT return error or crash
        response.Should().NotStartWith("Error:",
            "plan_roadmap should not error when learn:patterns does not exist");
        response.Should().NotBeNullOrWhiteSpace("plan_roadmap should always return a response");
    }

    [Fact]
    public async Task PlanTasks_NoErrorWhenNoPatternsExist()
    {
        // Arrange — no learn:patterns stored
        await InitProjectWithRequirements();
        await _tools.PlanRoadmap(SimpleRoadmap, CancellationToken.None);

        // Act
        string response = await _tools.PlanTasks("01", SimpleTasks, CancellationToken.None);

        // Assert — must NOT return error or crash
        response.Should().NotStartWith("Error:",
            "plan_tasks should not error when learn:patterns does not exist");
        response.Should().NotBeNullOrWhiteSpace("plan_tasks should always return a response");
    }

    // ── ADOPT-01: guide() text covers v2.0 tools and namespaces ──────────────

    [Fact]
    public async Task Guide_MentionsResearchTools()
    {
        // Arrange — guide() has no store dependency
        string guide = await _memTools.Guide(CancellationToken.None);

        // Assert
        guide.Should().Contain("research_start",
            "guide() should mention the research_start tool");
        guide.Should().Contain("research_complete",
            "guide() should mention the research_complete tool");
    }

    [Fact]
    public async Task Guide_MentionsConcernTools()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        guide.Should().Contain("concern_add",
            "guide() should mention the concern_add tool");
        guide.Should().Contain("concern_resolve",
            "guide() should mention the concern_resolve tool");
        guide.Should().Contain("concern",
            "guide() should mention the concern query tool");
    }

    [Fact]
    public async Task Guide_MentionsKnowledgeTool()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        guide.Should().Contain("store",
            "guide() should mention store() for persisting knowledge");
        guide.Should().Contain("skill_create",
            "guide() should mention skill_create for reusable prompts");
    }

    [Fact]
    public async Task Guide_MentionsSubagentTools()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        guide.Should().Contain("skill_create",
            "guide() should mention the skill_create tool");
        guide.Should().Contain("skill_load",
            "guide() should mention the skill_load tool");
    }

    [Fact]
    public async Task Guide_MentionsGoalUpdate()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        guide.Should().Contain("goal_update",
            "guide() should mention the goal_update tool");
    }

    [Fact]
    public async Task Guide_MentionsNewTopicNamespaces()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        guide.Should().Contain("research:*",
            "guide() should mention the research:* topic namespace");
        guide.Should().Contain("concern:*",
            "guide() should mention the concern:* topic namespace");
        guide.Should().Contain("skill:*",
            "guide() should mention the skill:* topic namespace");
    }
}
