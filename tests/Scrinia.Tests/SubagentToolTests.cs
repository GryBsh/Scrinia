using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for subagent creation MCP tools:
/// spawn_agent (AGENT-01, AGENT-02, AGENT-04) and skill_load (AGENT-03).
/// Also covers ADOPT-02 description context signal checks.
/// </summary>
public sealed class SubagentToolTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;

    public SubagentToolTests()
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

    /// <summary>Sets up a project so spawn_agent prerequisite check passes.</summary>
    private async Task InitProject()
    {
        await _tools.ProjectInit("Goals: test subagent creation", CancellationToken.None);
    }

    // ── AGENT-01 tests (spawn_agent storage and response) ─────────────────────

    [Fact]
    public async Task SpawnAgent_StoresSkillMemory()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — a skill:* entry must exist in skill scope index
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("skill:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().HaveCountGreaterOrEqualTo(1,
            "spawn_agent should create at least one skill entry in the index");
        entries.Should().Contain(e => e.Name == "test-reviewer",
            "spawn_agent should store a skill:test-reviewer entry in the skill scope");
    }

    [Fact]
    public async Task SpawnAgent_StoresRoleKeyword()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — stored entry must have keyword role:reviewer
        var store = MemoryStoreContext.Current!;
        var (scope, _) = store.ParseQualifiedName("skill:placeholder");
        var entries = store.LoadIndex(scope);
        entries.Should().Contain(e =>
            e.Keywords != null &&
            e.Keywords.Contains("role:reviewer", StringComparer.OrdinalIgnoreCase),
            "skill entry must have role:reviewer keyword");
    }

    [Fact]
    public async Task SpawnAgent_ContentContainsSystemPrompt()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — decoded content must contain "## Role" and the role description
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:test-reviewer");
        content.Should().Contain("## Role",
            "skill content should contain '## Role' section header");
        content.Should().ContainEquivalentOf("review",
            "skill content should contain role description mentioning 'review'");
    }

    [Fact]
    public async Task SpawnAgent_ResponseConfirmsStorage()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — return value starts with "Stored as skill:test-reviewer"
        result.Should().StartWith("Stored as skill:test-reviewer",
            "spawn_agent response must confirm storage with the qualified name");
    }

    [Fact]
    public async Task SpawnAgent_ArchivesExistingVersion()
    {
        // Arrange — write same skill name twice
        await InitProject();
        await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Get the versions dir path for skill scope
        var store = MemoryStoreContext.Current!;
        var (skillScope, skillSubject) = store.ParseQualifiedName("skill:test-reviewer");
        string storeDir = store.GetStoreDirForScope(skillScope);
        string versionsDir = Path.Combine(storeDir, "versions");

        // Act — write again (same name, archiveExisting: true)
        await _tools.SpawnAgent("test-reviewer", "reviewer", "Updated instructions.", null, CancellationToken.None);

        // Assert — a version archive file must exist for the subject
        bool versionsExist = Directory.Exists(versionsDir) &&
            Directory.GetFiles(versionsDir, $"{skillSubject}*").Length > 0;
        versionsExist.Should().BeTrue(
            "spawn_agent with same skill name should archive the previous version");
    }

    [Fact]
    public async Task SpawnAgent_RequiresProjectInit()
    {
        // Act — no project_init called
        string result = await _tools.SpawnAgent("test-skill", "researcher", null, null, CancellationToken.None);

        // Assert
        result.Should().StartWith("Error:",
            "spawn_agent without project:context must return Error:");
    }

    // ── AGENT-02 tests (capability-conditional fallback section) ──────────────

    [Fact]
    public async Task SpawnAgent_PromptContainsFallbackSection()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — decoded content must contain "Fallback"
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-researcher");
        content.Should().ContainEquivalentOf("Fallback",
            "skill prompt must contain a Fallback section for non-MCP environments");
    }

    [Fact]
    public async Task SpawnAgent_FallbackSectionHasInstructions()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — content contains fallback marker phrase
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-researcher");
        bool hasFallbackMarker =
            content.Contains("if Scrinia MCP is not available", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Scrinia MCP", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Fallback Instructions", StringComparison.OrdinalIgnoreCase);
        hasFallbackMarker.Should().BeTrue(
            "skill prompt fallback section must indicate it applies when Scrinia MCP is not available");
    }

    // ── AGENT-03 tests (skill_load list and load modes) ───────────────────────

    [Fact]
    public async Task SkillLoad_ListsAvailableSkills()
    {
        // Arrange — store two skills
        await InitProject();
        await _tools.SpawnAgent("api-reviewer", "reviewer", null, null, CancellationToken.None);
        await _tools.SpawnAgent("auth-researcher", "researcher", null, null, CancellationToken.None);

        // Act — list mode (no skillName)
        string result = await _tools.SkillLoad(null, CancellationToken.None);

        // Assert — both skill names should appear
        result.Should().Contain("api-reviewer",
            "skill_load list mode must include 'api-reviewer' from index");
        result.Should().Contain("auth-researcher",
            "skill_load list mode must include 'auth-researcher' from index");
    }

    [Fact]
    public async Task SkillLoad_LoadsSkillContent()
    {
        // Arrange — store a skill
        await InitProject();
        await _tools.SpawnAgent("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Act — load mode (skillName provided)
        string result = await _tools.SkillLoad("test-reviewer", CancellationToken.None);

        // Assert — returns full prompt content with "## Role"
        result.Should().Contain("## Role",
            "skill_load with a skill name must return the full prompt content");
        result.Should().ContainEquivalentOf("review",
            "skill_load must return content containing role description 'review'");
    }

    [Fact]
    public async Task SkillLoad_EmptyListWhenNoSkills()
    {
        // Arrange — no skills stored yet (but project initialized)
        await InitProject();

        // Act — list mode with no skills
        string result = await _tools.SkillLoad(null, CancellationToken.None);

        // Assert
        result.Should().ContainEquivalentOf("No skills stored yet",
            "skill_load with no stored skills must return 'No skills stored yet.'");
    }

    [Fact]
    public async Task SkillLoad_ErrorOnMissingSkill()
    {
        // Arrange — project init but no skill stored
        await InitProject();

        // Act — load mode for a skill that does not exist
        string result = await _tools.SkillLoad("nonexistent", CancellationToken.None);

        // Assert — must return Error: or informative message
        bool isErrorOrInformative =
            result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("nonexistent", StringComparison.OrdinalIgnoreCase);
        isErrorOrInformative.Should().BeTrue(
            "skill_load with a missing skill name must return an error or informative message");
    }

    // ── AGENT-04 tests (built-in scaffolds and custom mode) ───────────────────

    [Fact]
    public async Task SpawnAgent_ResearcherScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — content must contain "research" and tool references
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-researcher");
        content.Should().ContainEquivalentOf("research",
            "researcher scaffold content must reference 'research'");
        content.Should().ContainEquivalentOf("tool",
            "researcher scaffold must reference tools or contain tool section");
    }

    [Fact]
    public async Task SpawnAgent_ReviewerScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("my-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — content must contain "review"
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-reviewer");
        content.Should().ContainEquivalentOf("review",
            "reviewer scaffold content must contain 'review'");
    }

    [Fact]
    public async Task SpawnAgent_DomainExpertScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await _tools.SpawnAgent("my-expert", "domain-expert", null, null, CancellationToken.None);

        // Assert — content must contain "domain" or "expert"
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-expert");
        bool hasDomainOrExpert =
            content.Contains("domain", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("expert", StringComparison.OrdinalIgnoreCase);
        hasDomainOrExpert.Should().BeTrue(
            "domain-expert scaffold content must contain 'domain' or 'expert'");
    }

    [Fact]
    public async Task SpawnAgent_CustomRole()
    {
        // Arrange
        await InitProject();

        // Act — custom scaffold with instructions and tools
        await _tools.SpawnAgent(
            "my-custom",
            "custom",
            "Analyze database query performance and suggest indexes.",
            "search,show",
            CancellationToken.None);

        // Assert — content must come from the provided instructions
        var store = MemoryStoreContext.Current!;
        string content = await ReadMemoryText(store, "skill:my-custom");
        content.Should().Contain("database",
            "custom scaffold must embed the provided instructions in the content");
    }

    // ── ADOPT-02 tests (description context signals) ──────────────────────────

    [Fact]
    public void SpawnAgent_DescriptionContainsContextSignals()
    {
        // Reflection test — [Description] on SpawnAgent must reference "skill"
        var method = typeof(ScriniaProjectTools).GetMethod("SpawnAgent");
        method.Should().NotBeNull("SpawnAgent method must exist");

        var descAttr = method!.GetCustomAttributes(
                typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("SpawnAgent must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("skill",
            "SpawnAgent description must contain 'skill' reference so agents know where prompts are stored");
    }

    [Fact]
    public void SkillLoad_DescriptionContainsContextSignals()
    {
        // Reflection test — [Description] on SkillLoad must reference "skill"
        var method = typeof(ScriniaProjectTools).GetMethod("SkillLoad");
        method.Should().NotBeNull("SkillLoad method must exist");

        var descAttr = method!.GetCustomAttributes(
                typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault();
        descAttr.Should().NotBeNull("SkillLoad must have a [Description] attribute");

        string descText = descAttr!.Description;
        descText.Should().ContainEquivalentOf("skill",
            "SkillLoad description must contain 'skill' reference so agents know what it loads");
    }
}
