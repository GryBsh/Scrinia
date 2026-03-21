using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for research_start and research_complete MCP tools (RSRCH-01, RSRCH-02, RSRCH-03, ADOPT-02).
/// </summary>
public sealed class ResearchToolTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public ResearchToolTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── research_start tests (RSRCH-01) ───────────────────────────────────────

    [Fact]
    public async Task ResearchStart_StoresResearchMemory()
    {
        // Arrange — project:context must exist (prerequisite)
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);

        // Act
        await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Assert — research:06-auth must exist in index
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("research:06-auth");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e => e.Name == subject,
            "research_start should store research:06-auth memory");
    }

    [Fact]
    public async Task ResearchStart_HasStatusActiveKeyword()
    {
        // Arrange
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);

        // Act
        await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Assert — index entry must have status:active keyword
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("research:06-auth");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == subject);
        entry.Should().NotBeNull("research:06-auth entry must exist");
        entry!.Keywords.Should().Contain("status:active",
            "research_start must store status:active keyword");
    }

    [Fact]
    public async Task ResearchStart_HasPhaseKeyword()
    {
        // Arrange
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);

        // Act
        await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Assert — index entry must have phase:06 keyword
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("research:06-auth");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == subject);
        entry!.Keywords.Should().Contain("phase:06",
            "research_start must store phase:06 keyword");
    }

    [Fact]
    public async Task ResearchStart_ResponseContainsMemoryName()
    {
        // Arrange
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);

        // Act
        string result = await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Assert — response must contain the stored memory name
        result.Should().Contain("research:06-auth",
            "research_start response must reference the stored memory name");
    }

    [Fact]
    public async Task ResearchStart_FailsWithoutProject()
    {
        // Act — no project_init called
        string result = await _tools.ResearchStart("06", "auth", "Some questions", cancellationToken: CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "research_start without project:context should return Error:");
    }

    // ── research_complete tests (RSRCH-02) ────────────────────────────────────

    [Fact]
    public async Task ResearchComplete_OverwritesWithStatusComplete()
    {
        // Arrange — start research first
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);
        await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Act
        await _tools.ResearchComplete("06", "auth", "Findings: JWT is used everywhere.", cancellationToken: CancellationToken.None);

        // Assert — keyword must be status:complete, not status:active
        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("research:06-auth");
        var entries = store.LoadIndex(scope);
        var entry = entries.FirstOrDefault(e => e.Name == subject);
        entry.Should().NotBeNull("research:06-auth entry must still exist after research_complete");
        entry!.Keywords.Should().Contain("status:complete",
            "research_complete must update keyword to status:complete");
        entry.Keywords.Should().NotContain("status:active",
            "research_complete must remove status:active keyword");
    }

    [Fact]
    public async Task ResearchComplete_StoresFindings()
    {
        // Arrange
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);
        await _tools.ResearchStart("06", "auth", "What auth patterns are used?", cancellationToken: CancellationToken.None);

        // Act
        string findings = "JWT is widely used. OAuth2 is the external auth pattern.";
        await _tools.ResearchComplete("06", "auth", findings, cancellationToken: CancellationToken.None);

        // Assert — artifact content must contain findings
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "research:06-auth");
        content.Should().Contain(findings,
            "research_complete must store findings in the artifact content");
    }

    [Fact]
    public async Task ResearchComplete_FailsWhenNoActiveResearch()
    {
        // Arrange — no research_start called
        await _tools.ProjectInit("Goals: test research", cancellationToken: CancellationToken.None);

        // Act
        string result = await _tools.ResearchComplete("06", "auth", "Some findings", cancellationToken: CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "research_complete without prior research_start should return Error:");
    }

    // ── description contract tests (ADOPT-02, RSRCH-03) ──────────────────────

    [Fact]
    public void ResearchStart_DescriptionContainsContextSignals()
    {
        // Reflection test — verify [Description] attribute references plan_tasks and research_complete
        var method = typeof(ScriniaProjectTools).GetMethod("ResearchStart");
        method.Should().NotBeNull("ResearchStart method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("ResearchStart must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("plan_tasks",
            "ResearchStart description must reference plan_tasks to cue agents on when to call it");
        descText.Should().ContainEquivalentOf("research_complete",
            "ResearchStart description must reference research_complete so agents know what to call next");
    }

    [Fact]
    public void ResearchComplete_DescriptionContainsContextSignals()
    {
        // Reflection test — verify [Description] attribute references research_start and plan_tasks
        var method = typeof(ScriniaProjectTools).GetMethod("ResearchComplete");
        method.Should().NotBeNull("ResearchComplete method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("ResearchComplete must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("research_start",
            "ResearchComplete description must reference research_start (signals it follows it)");
        descText.Should().ContainEquivalentOf("plan_tasks",
            "ResearchComplete description must reference plan_tasks (signals it precedes it)");
    }

    [Fact]
    public void PlanTasks_DescriptionReferencesResearchMemories()
    {
        // Reflection test — verify PlanTasks [Description] references research:* (RSRCH-03)
        var method = typeof(ScriniaProjectTools).GetMethod("PlanTasks");
        method.Should().NotBeNull("PlanTasks method must exist");

        var descAttr = method!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("PlanTasks must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().Contain("research:",
            "PlanTasks description must reference 'research:' memories so agents are cued to research first (RSRCH-03)");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Scrinia.Core.Encoding.Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }
}
