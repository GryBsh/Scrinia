using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Disk I/O edge case tests for workflow, skill, and agent file operations.
/// Verifies graceful degradation when files are corrupted, missing, or malformed.
/// </summary>
public sealed class DiskIoEdgeCaseTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public DiskIoEdgeCaseTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ══════════════════════════════════════════════════════════════════════════
    // DISKIO-01: Workflow disk I/O (5 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkflow_CorruptedJsonFile_FallsBack()
    {
        // Arrange — write invalid JSON to .scrinia/workflows/default.json
        string baseDir = Path.Combine(_scope.WorkspaceDir, ".scrinia");
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "default.json"), "{{{not valid json!!!");

        // Also init project so goal('add') can run
        await ScriniaProjectTools.ProjectInit("Goals: test corrupted workflow", cancellationToken: CancellationToken.None);

        // Act — goal('add') internally calls ResolveWorkflowAsync
        string result = await ScriniaProjectTools.GoalUpdate("add", "Test corrupted workflow fallback",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed by falling back to built-in, with a warning
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "corrupted workflow JSON should fall back to built-in rather than error");

        // Verify seed tasks were created (proving the built-in workflow was used)
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "built-in workflow fallback should still create researcher seed task");
    }

    [Fact]
    public async Task ResolveWorkflow_MissingWorkflowsDir_FallsBack()
    {
        // Arrange — no .scrinia/workflows/ directory at all
        string workflowsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "workflows");
        if (Directory.Exists(workflowsDir))
            Directory.Delete(workflowsDir, recursive: true);

        await ScriniaProjectTools.ProjectInit("Goals: test missing workflows dir", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.GoalUpdate("add", "Test missing workflows dir",
            cancellationToken: CancellationToken.None);

        // Assert — should succeed using built-in default
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "missing workflows directory should silently fall back to built-in");

        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "built-in workflow should create researcher seed task when workflows dir is missing");
    }

    [Fact]
    public async Task ResolveWorkflow_EmptyJsonFile_FallsBack()
    {
        // Arrange — write empty string to workflow JSON file
        string baseDir = Path.Combine(_scope.WorkspaceDir, ".scrinia");
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "default.json"), "");

        await ScriniaProjectTools.ProjectInit("Goals: test empty workflow file", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.GoalUpdate("add", "Test empty workflow file",
            cancellationToken: CancellationToken.None);

        // Assert — should fall back to built-in
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "empty workflow JSON file should fall back to built-in rather than crash");

        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "empty workflow file should fall back to built-in and create seed tasks");
    }

    [Fact]
    public async Task ResolveWorkflow_FileWithBom_ParsesCorrectly()
    {
        // Arrange — write valid workflow JSON with UTF-8 BOM
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        string json = JsonSerializer.Serialize(workflow, PlanningJsonContext.Default.WorkflowDefinition);

        string baseDir = Path.Combine(_scope.WorkspaceDir, ".scrinia");
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Write with UTF-8 BOM
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "default.json"), json, encoding);

        await ScriniaProjectTools.ProjectInit("Goals: test BOM workflow", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.GoalUpdate("add", "Test workflow with BOM",
            cancellationToken: CancellationToken.None);

        // Assert — should parse the file despite BOM prefix
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "workflow JSON with UTF-8 BOM should parse successfully");

        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "BOM-prefixed workflow should be parsed and create seed tasks");
    }

    [Fact]
    public async Task CreateOrUpdateWorkflow_LongName_HandlesCorrectly()
    {
        // Arrange — workflow definition with a 200-character name
        string longName = new string('a', 200);
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        string json = JsonSerializer.Serialize(workflow, PlanningJsonContext.Default.WorkflowDefinition);

        // Patch the name field in the JSON to use the long name
        var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "name")
                    writer.WriteString("name", longName);
                else
                    prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        string patchedJson = Encoding.UTF8.GetString(ms.ToArray());

        await ScriniaProjectTools.ProjectInit("Goals: test long workflow name", cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("create", "workflow", definition: patchedJson,
            cancellationToken: CancellationToken.None);

        // Assert — should either succeed or return a validation error, not crash
        // The workflow should be created on disk if valid
        var parsed = ResponseParser.Parse(result);
        if (parsed.Status != "error")
        {
            string filePath = Path.Combine(_scope.WorkspaceDir, ".scrinia", "workflows", $"{longName}.json");
            File.Exists(filePath).Should().BeTrue(
                "workflow with long name should be written to disk");
        }
        else
        {
            // If it errors, it should be a clean validation error, not a crash
            (parsed.Error ?? "").Should().NotContain("Exception",
                "long workflow name should produce clean validation error if rejected");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DISKIO-02: Skill disk I/O (5 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SkillLoad_CorruptedMdFile_HandlesGracefully()
    {
        // Arrange — write binary junk to a skill .md file
        string skillsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDir);
        byte[] binaryJunk = [0x00, 0xFF, 0xFE, 0x80, 0x81, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0];
        await File.WriteAllBytesAsync(Path.Combine(skillsDir, "test-skill.md"), binaryJunk);

        // Act — load the corrupted skill
        string result = await ScriniaProjectTools.SkillLoad("test-skill", cancellationToken: CancellationToken.None);

        // Assert — should not throw; should return something readable
        result.Should().NotBeNull("loading a corrupted skill file should not throw");
        // It may return the binary content as-is (File.ReadAllTextAsync) or handle gracefully
        // The key requirement: no unhandled exception
    }

    [Fact]
    public async Task SkillLoad_CorruptedMetaJson_DegradeGracefully()
    {
        // Arrange — valid .md but invalid .meta.json
        string skillsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "test-skill.md"),
            "# Test Skill\nThis is a valid markdown skill.");
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "test-skill.meta.json"),
            "{{{not valid json");

        // Act — list skills (which reads sidecar metadata)
        string listResult = await ScriniaProjectTools.SkillLoad(cancellationToken: CancellationToken.None);

        // Assert — listing should not crash; the skill should appear even with bad metadata
        ResponseParser.Parse(listResult).Content.Should().Contain("test-skill",
            "skill with corrupted .meta.json should still appear in listing");

        // Act — load the specific skill
        string loadResult = await ScriniaProjectTools.SkillLoad("test-skill", cancellationToken: CancellationToken.None);

        // Assert — should return the .md content despite bad sidecar
        ResponseParser.Parse(loadResult).Content.Should().Contain("Test Skill",
            "skill load should return .md content even when .meta.json is corrupted");
    }

    [Fact]
    public async Task SkillCreate_MissingSkillsDir_AutoCreates()
    {
        // Arrange — ensure no .scrinia/skills/ directory exists
        string skillsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "skills");
        if (Directory.Exists(skillsDir))
            Directory.Delete(skillsDir, recursive: true);

        // Need a project for skill creation
        await ScriniaProjectTools.ProjectInit("Goals: test skill creation without skills dir",
            cancellationToken: CancellationToken.None);

        // Act — create a skill; this should auto-create the skills directory
        string result = await ScriniaProjectTools.SkillCreate("test-auto", "researcher",
            cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Status.Should().NotBe("error",
            "skill create should auto-create skills directory if missing");
        Directory.Exists(skillsDir).Should().BeTrue(
            "skills directory should be auto-created by skill('create')");
        File.Exists(Path.Combine(skillsDir, "test-auto.md")).Should().BeTrue(
            "skill .md file should be written after auto-creating directory");
    }

    [Fact]
    public async Task SkillLoad_EmptyMdFile_ReturnsAppropriateResponse()
    {
        // Arrange — create an empty .md file for a skill
        string skillsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "empty-skill.md"), "");

        // Act
        string result = await ScriniaProjectTools.SkillLoad("empty-skill", cancellationToken: CancellationToken.None);

        // Assert — should not crash; should return a response (possibly empty content)
        result.Should().NotBeNull("loading an empty skill file should not throw");
        // The file exists on disk, so it should be picked up (diskContent != null, but empty)
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().NotBe("error",
            "empty skill should still be loaded from file source without error");
    }

    [Fact]
    public async Task SkillLoad_OrphanMetaJson_NoMdFile()
    {
        // Arrange — .meta.json exists but .md file is missing
        string skillsDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "skills");
        Directory.CreateDirectory(skillsDir);
        // Write only the sidecar, not the .md
        string metaJson = JsonSerializer.Serialize(
            new SkillFileMeta(null, "researcher", null, "researcher", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"),
            PlanningJsonContext.Default.SkillFileMeta);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "orphan-skill.meta.json"), metaJson);

        // Act — try to load the skill (no .md file, only .meta.json)
        string result = await ScriniaProjectTools.SkillLoad("orphan-skill", cancellationToken: CancellationToken.None);

        // Assert — should fall through to NMP/2 and then built-in lookup, eventually returning not found
        result.Should().NotBeNull("orphan .meta.json should not cause a crash");
        // The .meta.json is not an .md file, so listing filters by *.md — orphan should not appear in listing
        string listResult = await ScriniaProjectTools.SkillLoad(cancellationToken: CancellationToken.None);
        ResponseParser.Parse(listResult).Content.Should().NotContain("orphan-skill",
            "orphan .meta.json without .md should not appear in skill listing");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DISKIO-03: Agent disk I/O (5 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentShow_CorruptedMdFile_HandlesGracefully()
    {
        // Arrange — write binary junk to agent profile .md
        string agentDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        byte[] binaryJunk = [0x00, 0xFF, 0xFE, 0x80, 0x81, 0x90, 0xA0, 0xB0, 0xC0, 0xD0, 0xE0, 0xF0];
        await File.WriteAllBytesAsync(Path.Combine(agentDir, "profile.md"), binaryJunk);

        // Act — show agent:profile (reads the .md file first)
        string result = await _memTools.Show("agent:profile", cancellationToken: CancellationToken.None);

        // Assert — should not throw; File.ReadAllTextAsync may return garbled text but shouldn't crash
        result.Should().NotBeNull(
            "showing agent:profile with binary content should not throw");
    }

    [Fact]
    public async Task AgentShow_CorruptedMetaJson_DegradeGracefully()
    {
        // Arrange — valid .md but invalid .meta.json
        string agentDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "profile.md"),
            "autonomy_level: high\nreview_depth: detailed");
        await File.WriteAllTextAsync(Path.Combine(agentDir, "profile.meta.json"),
            "NOT VALID JSON AT ALL {{{");

        // Act — show the agent profile
        string result = await _memTools.Show("agent:profile", cancellationToken: CancellationToken.None);

        // Assert — should return the .md content despite corrupted sidecar
        ResponseParser.Parse(result).Content.Should().Contain("autonomy_level: high",
            "agent:profile show should return .md content even when .meta.json is corrupted");

        // Act — overwrite the profile (which reads existing meta first)
        // This tests that WriteSidecarMeta handles the corrupted meta gracefully
        string storeResult = await _memTools.Store(
            ["autonomy_level: low"],
            "agent:profile",
            cancellationToken: CancellationToken.None);

        // Assert — store should succeed despite corrupted meta
        ResponseParser.Parse(storeResult).Content.Should().Contain("Remembered:",
            "storing agent:profile should succeed even when existing .meta.json is corrupted");
    }

    [Fact]
    public async Task AgentStore_MissingAgentDir_AutoCreates()
    {
        // Arrange — ensure no .scrinia/agent/ directory exists
        string agentDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent");
        if (Directory.Exists(agentDir))
            Directory.Delete(agentDir, recursive: true);

        // Act — store an agent profile; should auto-create the directory
        string result = await _memTools.Store(
            ["autonomy_level: high"],
            "agent:profile",
            cancellationToken: CancellationToken.None);

        // Assert
        ResponseParser.Parse(result).Content.Should().Contain("Remembered:",
            "storing agent:profile should auto-create agent directory if missing");
        Directory.Exists(agentDir).Should().BeTrue(
            "agent directory should be auto-created by memory('store') for agent topic");
        File.Exists(Path.Combine(agentDir, "profile.md")).Should().BeTrue(
            "profile.md should be written after auto-creating agent directory");
    }

    [Fact]
    public async Task AgentShow_EmptyMdFile_ReturnsAppropriateResponse()
    {
        // Arrange — create an empty .md file for agent config
        string agentDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "profile.md"), "");

        // Act
        string result = await _memTools.Show("agent:profile", cancellationToken: CancellationToken.None);

        // Assert — should not crash; may return empty string or a fallback message
        result.Should().NotBeNull(
            "showing an empty agent:profile file should not throw");
    }

    [Fact]
    public async Task AgentShow_OrphanMetaJson_NoMdFile()
    {
        // Arrange — .meta.json exists but .md file is missing
        string agentDir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        string metaJson = JsonSerializer.Serialize(
            new AgentFileMeta("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"),
            PlanningJsonContext.Default.AgentFileMeta);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "profile.meta.json"), metaJson);
        // Ensure .md does NOT exist
        string mdPath = Path.Combine(agentDir, "profile.md");
        if (File.Exists(mdPath)) File.Delete(mdPath);

        // Act — show agent:profile when .md is missing but .meta.json exists
        string result = await _memTools.Show("agent:profile", cancellationToken: CancellationToken.None);

        // Assert — should fall through to NMP/2 resolution and return not-found message
        result.Should().NotBeNull(
            "orphan .meta.json without .md should not cause a crash");
        ResponseParser.Parse(result).Error.Should().Contain("not found",
            "agent:profile with only .meta.json (no .md) should report not found");
    }
}
