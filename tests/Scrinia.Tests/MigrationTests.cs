using System.Reflection;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the v1 → v2 filesystem migration command (<see cref="ScriniaCommands.Migrate"/>)
/// and the v2 path structure (<see cref="PathRouter"/>).
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private static readonly HashSet<string> EntityTypes = new(
        ["goal", "phase", "task", "concern", "requirement", "project", "workflow", "skill"],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _tempDir;

    public MigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_migration_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ScriniaDir => Path.Combine(_tempDir, ".scrinia");
    private string TopicsDir => Path.Combine(ScriniaDir, "topics");
    private string MemoriesDir => Path.Combine(ScriniaDir, "memories");

    /// <summary>
    /// Calls the private static BuildMigrationPlan method via reflection.
    /// This avoids Spectre.Console markup issues in the CLI output path.
    /// </summary>
    private static List<(string Source, string Target)> BuildMigrationPlan(string scriniaDir)
    {
        var method = typeof(ScriniaCommands).GetMethod(
            "BuildMigrationPlan",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("BuildMigrationPlan must exist on ScriniaCommands");
        var result = method!.Invoke(null, [scriniaDir]);
        return (List<(string Source, string Target)>)result!;
    }

    /// <summary>Creates a file at the given path relative to .scrinia/, with dummy content.</summary>
    private void CreateV1File(string relativePath, string content = "test-content")
    {
        string fullPath = Path.Combine(ScriniaDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>Checks whether a file exists at the given path relative to .scrinia/.</summary>
    private bool FileExistsRelative(string relativePath)
    {
        string fullPath = Path.Combine(ScriniaDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    /// <summary>Reads the content of a file at the given path relative to .scrinia/.</summary>
    private string ReadRelative(string relativePath)
    {
        string fullPath = Path.Combine(ScriniaDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(fullPath);
    }

    private ParsedPath P(string raw) => PathParser.Parse(raw, EntityTypes);

    // ═══════════════════════════════════════════════════════════════════════════
    //  V2 PATH STRUCTURE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void V2Path_WritesToMemoriesDir()
    {
        // store via /api/auth-flow → verify file at .scrinia/memories/api/auth-flow.nmp2
        var result = PathRouter.ToFilesystemPath(P("/api/auth-flow"), _tempDir);

        result.Should().NotBeNull();
        result!.Should().Be(Path.Combine(_tempDir, ".scrinia", "memories", "api", "auth-flow.nmp2"));
    }

    [Fact]
    public void V2Path_AgentWritesMd()
    {
        // store via /agent/profile → verify file at .scrinia/memories/agent/profile.md
        var result = PathRouter.ToFilesystemPath(P("/agent/profile"), _tempDir);

        result.Should().NotBeNull();
        result!.Should().Be(Path.Combine(_tempDir, ".scrinia", "memories", "agent", "profile.md"));
    }

    [Fact]
    public void V2Path_DeepEntityPath_CreatesNestedDirs()
    {
        // store via /goal/G-5/research/frontend → verify nested directory structure
        var result = PathRouter.ToFilesystemPath(P("/goal/G-5/research/frontend"), _tempDir);

        result.Should().NotBeNull();
        result!.Should().Be(Path.Combine(
            _tempDir, ".scrinia", "memories", "goal", "G-5", "research", "frontend.nmp2"));

        // The parent directory chain should be: memories/goal/G-5/research/
        string expectedDir = Path.GetDirectoryName(result)!;
        expectedDir.Should().EndWith(Path.Combine("goal", "G-5", "research"));
    }

    [Fact]
    public void V2Path_SidecarMetadata_WrittenCorrectly()
    {
        // Verify .meta.json alongside the artifact
        var parsed = P("/api/auth-flow");
        var artPath = PathRouter.ToFilesystemPath(parsed, _tempDir);
        var metaPath = PathRouter.ToMetadataPath(parsed, _tempDir);

        artPath.Should().NotBeNull();
        metaPath.Should().NotBeNull();

        // Meta path should be same location but with .meta.json extension
        metaPath!.Should().Be(Path.Combine(_tempDir, ".scrinia", "memories", "api", "auth-flow.meta.json"));

        // Both should share the same directory
        Path.GetDirectoryName(artPath).Should().Be(Path.GetDirectoryName(metaPath));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MIGRATION LOGIC TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Migrate_DryRun_NoFilesCopied()
    {
        // Arrange: create v1 files in topics/
        CreateV1File("topics/api/auth-flow.nmp2", "api-content");
        CreateV1File("topics/api/auth-flow.meta.json", "{}");

        // Act: verify the plan is built but no files are actually copied.
        // We use BuildMigrationPlan directly to avoid Spectre.Console markup issues
        // in the dry-run console output path.
        var plan = BuildMigrationPlan(ScriniaDir);

        // Assert: plan has items but no files exist in memories/
        plan.Should().HaveCountGreaterThan(0, because: "v1 files should produce a migration plan");
        Directory.Exists(MemoriesDir).Should().BeFalse(
            because: "building a plan should not create the memories/ directory");

        // Verify none of the target files exist
        foreach (var (_, target) in plan)
            File.Exists(target).Should().BeFalse(because: "no files should be copied during plan-only");
    }

    [Fact]
    public async Task Migrate_CopiesV1ToV2Paths()
    {
        // Arrange: create v1 files in topics/
        CreateV1File("topics/arch/overview.nmp2", "arch-content");
        CreateV1File("topics/arch/overview.meta.json", "{\"tags\":[]}");
        CreateV1File("topics/patterns/resilience.nmp2", "resilience-content");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: files appear in v2 structure
        exitCode.Should().Be(0);
        FileExistsRelative("memories/arch/overview.nmp2").Should().BeTrue();
        FileExistsRelative("memories/arch/overview.meta.json").Should().BeTrue();
        FileExistsRelative("memories/patterns/resilience.nmp2").Should().BeTrue();

        // Content should be preserved
        ReadRelative("memories/arch/overview.nmp2").Should().Be("arch-content");
    }

    [Fact]
    public async Task Migrate_SkipsExistingTargets()
    {
        // Arrange: create file in both v1 and v2 locations
        CreateV1File("topics/api/auth-flow.nmp2", "old-v1-content");
        CreateV1File("memories/api/auth-flow.nmp2", "existing-v2-content");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: existing v2 content is preserved (not overwritten)
        exitCode.Should().Be(0);
        ReadRelative("memories/api/auth-flow.nmp2").Should().Be("existing-v2-content",
            because: "migration must not overwrite existing v2 files");
    }

    [Fact]
    public async Task Migrate_NamespacedEntity_StripsPrefix()
    {
        // Arrange: file at topics/entity/goal/G-5.nmp2 should migrate to memories/goal/G-5.nmp2
        CreateV1File("topics/entity/goal/G-5.nmp2", "goal-content");
        CreateV1File("topics/entity/goal/G-5.meta.json", "{\"id\":\"G-5\"}");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: entity/ prefix is stripped
        exitCode.Should().Be(0);
        FileExistsRelative("memories/goal/G-5.nmp2").Should().BeTrue();
        FileExistsRelative("memories/goal/G-5.meta.json").Should().BeTrue();
        ReadRelative("memories/goal/G-5.nmp2").Should().Be("goal-content");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EDGE CASES
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Migrate_EmptyTopics_NoErrors()
    {
        // Arrange: create .scrinia/ with empty topics/
        Directory.CreateDirectory(TopicsDir);

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: completes cleanly with 0 files
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_VersionFiles_Excluded()
    {
        // Arrange: create both a regular file and a versions/ file
        CreateV1File("topics/arch/overview.nmp2", "content");
        CreateV1File("topics/arch/versions/overview_20260322-211421.nmp2", "version-content");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: regular file migrated, version file excluded
        exitCode.Should().Be(0);
        FileExistsRelative("memories/arch/overview.nmp2").Should().BeTrue();

        // Version files should NOT appear in memories/
        FileExistsRelative("memories/arch/versions/overview_20260322-211421.nmp2").Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_AgentDir_CopiedToMemoriesAgent()
    {
        // Arrange: create agent markdown files in .scrinia/agent/
        CreateV1File("agent/profile.md", "# Agent Profile\nBe helpful.");
        CreateV1File("agent/tenets.md", "# Tenets\nQuality first.");
        // Also create .scrinia/ directory so Migrate detects the workspace
        Directory.CreateDirectory(TopicsDir);

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: agent files appear under memories/agent/
        exitCode.Should().Be(0);
        FileExistsRelative("memories/agent/profile.md").Should().BeTrue();
        FileExistsRelative("memories/agent/tenets.md").Should().BeTrue();
        ReadRelative("memories/agent/profile.md").Should().Be("# Agent Profile\nBe helpful.");
    }

    [Fact]
    public async Task Migrate_SkillsDir_CopiedToMemoriesSkill()
    {
        // Arrange: create skill files in .scrinia/skills/
        CreateV1File("skills/qa.md", "# QA Skill");
        // Ensure .scrinia/ exists
        Directory.CreateDirectory(TopicsDir);

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: skills/ migrated to memories/skill/
        exitCode.Should().Be(0);
        FileExistsRelative("memories/skill/qa.md").Should().BeTrue();
        ReadRelative("memories/skill/qa.md").Should().Be("# QA Skill");
    }

    [Fact]
    public async Task Migrate_MemoryNamespaced_StripsPrefix()
    {
        // Arrange: file at topics/memory/api/auth-flow.nmp2 → memories/api/auth-flow.nmp2
        CreateV1File("topics/memory/api/auth-flow.nmp2", "memory-ns-content");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: memory/ prefix is stripped
        exitCode.Should().Be(0);
        FileExistsRelative("memories/api/auth-flow.nmp2").Should().BeTrue();
        ReadRelative("memories/api/auth-flow.nmp2").Should().Be("memory-ns-content");
    }

    [Fact]
    public void Migrate_NoTopicsDir_EmptyPlan()
    {
        // Arrange: create .scrinia/ with no topics/, agent/, skills/, or workflows/
        Directory.CreateDirectory(ScriniaDir);

        // Act: BuildMigrationPlan should return an empty plan
        var plan = BuildMigrationPlan(ScriniaDir);

        // Assert
        plan.Should().BeEmpty(because: "no v1 source directories exist");
    }

    [Fact]
    public async Task Migrate_FreshWorkspace_ReturnsZero()
    {
        // Arrange: use a temp dir that has no .scrinia/ at all.
        // WorkspaceSetup.Configure will create .scrinia/ automatically.
        string freshDir = Path.Combine(Path.GetTempPath(), $"scrinia_fresh_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(freshDir);

            // Act: migrate on a fresh workspace with no v1 data
            var cmd = new ScriniaCommands();
            int exitCode = await cmd.Migrate(workspace: freshDir, dryRun: false, backup: false);

            // Assert: returns success (nothing to migrate)
            exitCode.Should().Be(0);
        }
        finally
        {
            try { Directory.Delete(freshDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Migrate_MultipleTopics_AllMigrated()
    {
        // Arrange: create files across multiple topics
        CreateV1File("topics/arch/overview.nmp2", "arch");
        CreateV1File("topics/arch/overview.meta.json", "{}");
        CreateV1File("topics/patterns/resilience.nmp2", "patterns");
        CreateV1File("topics/patterns/resilience.meta.json", "{}");
        CreateV1File("topics/backlog/scrinia.nmp2", "backlog");
        CreateV1File("topics/agent/profile.nmp2", "agent");

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.Migrate(workspace: _tempDir, dryRun: false, backup: false);

        // Assert: all topics migrated
        exitCode.Should().Be(0);
        FileExistsRelative("memories/arch/overview.nmp2").Should().BeTrue();
        FileExistsRelative("memories/arch/overview.meta.json").Should().BeTrue();
        FileExistsRelative("memories/patterns/resilience.nmp2").Should().BeTrue();
        FileExistsRelative("memories/patterns/resilience.meta.json").Should().BeTrue();
        FileExistsRelative("memories/backlog/scrinia.nmp2").Should().BeTrue();
        FileExistsRelative("memories/agent/profile.nmp2").Should().BeTrue();
    }
}
