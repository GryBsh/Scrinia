using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for organic adoption signals (ADOPT-01, ADOPT-03) and learning feedback:
/// - guide() text covers all v2.0 tools and topic namespaces (ADOPT-01)
/// - context_resume surfaces unused capability hints (ADOPT-03)
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
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API\n- Create the UI", CancellationToken.None);
    }

    private async Task InitProjectWithRequirements()
    {
        await InitProject();
        await ScriniaProjectTools.PlanRequirements("## v1 Requirements\n- REQ-01: Core feature", CancellationToken.None);
    }

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

    // ── ADOPT-03: context_resume capability hints ────────────────────────────────

    [Fact]
    public async Task ContextResume_HintsUnusedConcernTracking()
    {
        // Arrange — project init but no concern_add called
        await InitProject();

        // Act
        string response = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — hint about unused concern tracking must appear
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "context_resume should succeed");
        string fullText = (r.Content ?? "") + " " + string.Join(" ", r.ActionNeeded) + " " + string.Join(" ", r.Info);
        (fullText.Contains("hint", StringComparison.OrdinalIgnoreCase) ||
         fullText.Contains("concern", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "context_resume should surface a hint about unused concern tracking when concern_add has never been called");
    }

    [Fact]
    public async Task ContextResume_HintsUnusedKnowledge()
    {
        // Arrange — project init but no store of domain knowledge
        await InitProject();

        // Act
        string response = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — hint about persisting knowledge must appear
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "context_resume should succeed");
        string fullText = (r.Content ?? "") + " " + string.Join(" ", r.ActionNeeded) + " " + string.Join(" ", r.Info);
        (fullText.Contains("knowledge", StringComparison.OrdinalIgnoreCase) ||
         fullText.Contains("store", StringComparison.OrdinalIgnoreCase) ||
         fullText.Contains("topic", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "context_resume should surface a hint about persisting domain knowledge");
    }

    [Fact]
    public async Task ContextResume_NoHintWhenConcernsExist()
    {
        // Arrange — init project and add a concern
        await InitProject();
        await ScriniaProjectTools.ConcernAdd("Test risk", "low", "01", id: "test-risk", CancellationToken.None);

        // Act
        string response = await ScriniaProjectTools.ContextResume(CancellationToken.None);

        // Assert — unused-concern hint text must NOT appear
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "context_resume should succeed");
        string fullText = (r.Content ?? "") + " " + string.Join(" ", r.ActionNeeded) + " " + string.Join(" ", r.Info);
        fullText.Should().NotContain(
            "concern tracking is available",
            "context_resume should NOT show an unused concern hint when concerns have been logged");
    }

    // ── Learning feed-in: plan_tasks queries learn:patterns ──────────────────

    [Fact]
    public async Task PlanTasks_IncludesLearnPatterns()
    {
        // Arrange — store learn:patterns, then call plan_tasks
        await InitProjectWithRequirements();
        await StorePatternsMemory();

        // Act
        string response = await ScriniaProjectTools.PlanTasks("01", SimpleTasks, CancellationToken.None);

        // Assert — response must contain Pattern or Learn substring
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success", "plan_tasks should succeed");
        string fullText = (r.Content ?? "") + " " + string.Join(" ", r.ActionNeeded) + " " + string.Join(" ", r.Info);
        (fullText.Contains("Pattern", StringComparison.OrdinalIgnoreCase) ||
         fullText.Contains("Learn", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "plan_tasks should surface learn:patterns content in its response when patterns exist");
    }

    [Fact]
    public async Task PlanTasks_NoErrorWhenNoPatternsExist()
    {
        // Arrange — no learn:patterns stored
        await InitProjectWithRequirements();

        // Act
        string response = await ScriniaProjectTools.PlanTasks("01", SimpleTasks, CancellationToken.None);

        // Assert — must NOT return error or crash
        var r = ResponseParser.Parse(response);
        r.Status.Should().Be("success",
            "plan_tasks should not error when learn:patterns does not exist");
    }

    // ── ADOPT-01: guide() text covers v2.0 tools and namespaces ──────────────

    [Fact]
    public async Task Guide_MentionsResearchTools()
    {
        // Arrange — guide() has no store dependency
        string guide = await _memTools.Guide(CancellationToken.None);

        // Assert — guide mentions memory('remember' for storing knowledge
        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("memory('remember'",
            "guide() should mention memory('remember' for persisting research findings");
    }

    [Fact]
    public async Task Guide_MentionsConcernTools()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("/concern/...",
            "guide() should mention the /concern/ path in reserved paths");
        r.Content.Should().Contain("tracked risks",
            "guide() should describe concerns as tracked risks");
        r.Content.Should().Contain("concern",
            "guide() should mention the concern topic");
    }

    [Fact]
    public async Task Guide_MentionsKnowledgeTool()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("memory('remember'",
            "guide() should mention memory('remember' for persisting knowledge");
        r.Content.Should().Contain("/skill/",
            "guide() should mention /skill/ in reserved paths");
    }

    [Fact]
    public async Task Guide_MentionsSubagentTools()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("/skill/",
            "guide() should mention /skill/ reserved path for specialist prompts");
        r.Content.Should().Contain("memory('recall'",
            "guide() should mention memory('recall' for reading memories");
    }

    [Fact]
    public async Task Guide_MentionsGoalUpdate()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("/goal/",
            "guide() should mention /goal/ reserved path");
    }

    [Fact]
    public async Task Guide_MentionsNewTopicNamespaces()
    {
        string guide = await _memTools.Guide(CancellationToken.None);

        var r = ResponseParser.Parse(guide);
        r.Status.Should().Be("success", "guide() should succeed");
        r.Content.Should().Contain("/research/",
            "guide() should mention the /research/ path");
        r.Content.Should().Contain("/concern/",
            "guide() should mention the /concern/ path");
        r.Content.Should().Contain("/skill/",
            "guide() should mention the /skill/ path");
    }
}
