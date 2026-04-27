using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for subagent creation MCP tools:
/// skill_create (AGENT-01, AGENT-02, AGENT-04) and skill_load (AGENT-03).
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

    /// <summary>Reads a skill markdown file from disk (.scrinia/skills/{name}.md).</summary>
    private static string ReadSkillFile(IMemoryStore store, string skillName)
    {
        string storeDir = store.GetStoreDirForScope("local");
        // Walk up to .scrinia/
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        string baseDir = dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
        return File.ReadAllText(Path.Combine(baseDir, "skills", $"{skillName}.md"));
    }

    /// <summary>Reads a skill sidecar metadata from disk (.scrinia/skills/{name}.meta.json).</summary>
    private static SkillFileMeta? ReadSkillMeta(IMemoryStore store, string skillName)
    {
        string storeDir = store.GetStoreDirForScope("local");
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        string baseDir = dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
        string metaPath = Path.Combine(baseDir, "skills", $"{skillName}.meta.json");
        if (!File.Exists(metaPath)) return null;
        string json = File.ReadAllText(metaPath);
        return System.Text.Json.JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SkillFileMeta);
    }

    /// <summary>Sets up a project so skill_create prerequisite check passes.</summary>
    private async Task InitProject()
    {
        await ScriniaProjectTools.ProjectInit("Goals: test subagent creation", CancellationToken.None);
    }

    // ── AGENT-01 tests (skill_create storage and response) ─────────────────────

    [Fact]
    public async Task SkillCreate_StoresSkillMemory()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — a .md file must exist on disk at .scrinia/skills/test-reviewer.md
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "test-reviewer");
        content.Should().NotBeNullOrEmpty(
            "skill_create should write a .md file to .scrinia/skills/");
    }

    [Fact]
    public async Task SkillCreate_StoresRoleKeyword()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — sidecar .meta.json must have Role: "reviewer"
        var store = MemoryStoreContext.Current!;
        var meta = ReadSkillMeta(store, "test-reviewer");
        meta.Should().NotBeNull("skill_create must write a sidecar .meta.json");
        meta!.Role.Should().Be("reviewer",
            "skill sidecar must record role:reviewer");
    }

    [Fact]
    public async Task SkillCreate_ContentContainsSystemPrompt()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — disk file content must contain "## Role" and the role description
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "test-reviewer");
        content.Should().Contain("## Role",
            "skill content should contain '## Role' section header");
        content.Should().ContainEquivalentOf("review",
            "skill content should contain role description mentioning 'review'");
    }

    [Fact]
    public async Task SkillCreate_ResponseConfirmsStorage()
    {
        // Arrange
        await InitProject();

        // Act
        string result = await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — return value confirms storage
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "skill_create should succeed");
        r.Content.Should().Contain("Stored as .scrinia/skills/test-reviewer.md",
            "skill_create response must confirm storage with the disk file path");
    }

    [Fact]
    public async Task SkillCreate_ArchivesExistingVersion()
    {
        // Arrange — write same skill name twice
        await InitProject();
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Get the versions dir path for disk skills
        var store = MemoryStoreContext.Current!;
        string storeDir = store.GetStoreDirForScope("local");
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        string baseDir = dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
        string versionsDir = Path.Combine(baseDir, "skills", "versions");

        // Act — write again (same name, archives previous)
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", "Updated instructions.", null, CancellationToken.None);

        // Assert — a version archive file must exist for test-reviewer
        bool versionsExist = Directory.Exists(versionsDir) &&
            Directory.GetFiles(versionsDir, "test-reviewer*").Length > 0;
        versionsExist.Should().BeTrue(
            "skill_create with same skill name should archive the previous version");
    }

    [Fact]
    public async Task SkillCreate_RequiresProjectInit()
    {
        // Act — no project_init called
        string result = await ScriniaProjectTools.SkillCreate("test-skill", "researcher", null, null, CancellationToken.None);

        // Assert
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "skill_create without project:context must return an error");
    }

    // ── AGENT-02 tests (capability-conditional fallback section) ──────────────

    [Fact]
    public async Task SkillCreate_PromptContainsFallbackSection()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — disk file content must contain "Fallback"
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-researcher");
        content.Should().ContainEquivalentOf("Fallback",
            "skill prompt must contain a Fallback section for non-MCP environments");
    }

    [Fact]
    public async Task SkillCreate_FallbackSectionHasInstructions()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — disk file content contains fallback marker phrase
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-researcher");
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
        await ScriniaProjectTools.SkillCreate("api-reviewer", "reviewer", null, null, CancellationToken.None);
        await ScriniaProjectTools.SkillCreate("auth-researcher", "researcher", null, null, CancellationToken.None);

        // Act — list mode (no skillName)
        string result = await ScriniaProjectTools.SkillLoad(null, cancellationToken: CancellationToken.None);

        // Assert — both skill names should appear
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "skill_load list mode should succeed");
        r.Content.Should().Contain("api-reviewer",
            "skill_load list mode must include 'api-reviewer' from index");
        r.Content.Should().Contain("auth-researcher",
            "skill_load list mode must include 'auth-researcher' from index");
    }

    [Fact]
    public async Task SkillLoad_LoadsSkillContent()
    {
        // Arrange — store a skill
        await InitProject();
        await ScriniaProjectTools.SkillCreate("test-reviewer", "reviewer", null, null, CancellationToken.None);

        // Act — load mode (skillName provided)
        string result = await ScriniaProjectTools.SkillLoad("test-reviewer", cancellationToken: CancellationToken.None);

        // Assert — returns full prompt content with "## Role"
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "skill_load should succeed");
        r.Content.Should().Contain("## Role",
            "skill_load with a skill name must return the full prompt content");
        r.Content.Should().ContainEquivalentOf("review",
            "skill_load must return content containing role description 'review'");
    }

    [Fact]
    public async Task SkillLoad_ListsBuiltInSkillsWhenNoProjectSkills()
    {
        // Arrange — no project skills stored yet (but project initialized)
        await InitProject();

        // Act — list mode with no project skills
        string result = await ScriniaProjectTools.SkillLoad(null, cancellationToken: CancellationToken.None);

        // Assert — built-in skills should always appear
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "skill_load list mode should succeed");
        r.Content.Should().Contain("march-reporter",
            "skill_load must list built-in skills even when no project skills exist");
        r.Content.Should().Contain("built-in",
            "built-in skills must be tagged as [built-in]");
    }

    [Fact]
    public async Task SkillLoad_ErrorOnMissingSkill()
    {
        // Arrange — project init but no skill stored
        await InitProject();

        // Act — load mode for a skill that does not exist
        string result = await ScriniaProjectTools.SkillLoad("nonexistent", cancellationToken: CancellationToken.None);

        // Assert — must return an error
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("error",
            "skill_load with a missing skill name must return an error");
    }

    // ── AGENT-04 tests (built-in scaffolds and custom mode) ───────────────────

    [Fact]
    public async Task SkillCreate_ResearcherScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("my-researcher", "researcher", null, null, CancellationToken.None);

        // Assert — disk file content must contain "research" and tool references
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-researcher");
        content.Should().ContainEquivalentOf("research",
            "researcher scaffold content must reference 'research'");
        content.Should().ContainEquivalentOf("tool",
            "researcher scaffold must reference tools or contain tool section");
    }

    [Fact]
    public async Task SkillCreate_ReviewerScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("my-reviewer", "reviewer", null, null, CancellationToken.None);

        // Assert — disk file content must contain "review"
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-reviewer");
        content.Should().ContainEquivalentOf("review",
            "reviewer scaffold content must contain 'review'");
    }

    [Fact]
    public async Task SkillCreate_DomainExpertScaffold()
    {
        // Arrange
        await InitProject();

        // Act
        await ScriniaProjectTools.SkillCreate("my-expert", "domain-expert", null, null, CancellationToken.None);

        // Assert — disk file content must contain "domain" or "expert"
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-expert");
        bool hasDomainOrExpert =
            content.Contains("domain", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("expert", StringComparison.OrdinalIgnoreCase);
        hasDomainOrExpert.Should().BeTrue(
            "domain-expert scaffold content must contain 'domain' or 'expert'");
    }

    [Fact]
    public async Task SkillCreate_CustomRole()
    {
        // Arrange
        await InitProject();

        // Act — custom scaffold with instructions and tools
        await ScriniaProjectTools.SkillCreate(
            "my-custom",
            "custom",
            "Analyze database query performance and suggest indexes.",
            "search,show",
            CancellationToken.None);

        // Assert — disk file content must come from the provided instructions
        var store = MemoryStoreContext.Current!;
        string content = ReadSkillFile(store, "my-custom");
        content.Should().Contain("database",
            "custom scaffold must embed the provided instructions in the content");
    }

    // ── ADOPT-02 tests (description context signals) ──────────────────────────

    [Fact]
    public void SkillCreate_InternalMethodExists()
    {
        // SkillCreate is an internal method routed through memory() path routing (/skill/...).
        var method = typeof(ScriniaProjectTools).GetMethod("SkillCreate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("SkillCreate must exist as an internal method");
    }

    [Fact]
    public void SkillLoad_InternalMethodExists()
    {
        // SkillLoad is an internal method routed through memory() path routing (/skill/...).
        var method = typeof(ScriniaProjectTools).GetMethod("SkillLoad",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull("SkillLoad must exist as an internal method");
    }
}
