using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the <c>scri migrate</c> CLI command (<see cref="ScriniaCommands.Migrate"/>, a thin
/// wrapper over <see cref="StoreLayoutMigration.Migrate"/> — see
/// <c>StoreLayoutMigrationTests</c> for engine-level coverage) and the v2 path structure
/// (<see cref="PathRouter"/>).
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
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
    //  `scri migrate` CLI COMMAND (thin wrapper — engine coverage lives in
    //  StoreLayoutMigrationTests)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Migrate_DryRun_ReportsPlanAndMovesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        string legacy = Path.Combine(root, ".scrinia", "topics", "findings");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "review.nmp2"), "body");
        try
        {
            var cmd = new ScriniaCommands();
            int exit = await cmd.Migrate(root, dryRun: true, backup: false);

            exit.Should().Be(0);
            File.Exists(Path.Combine(legacy, "review.nmp2")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, ".scrinia", "memories")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ATTRIBUTES MIGRATION COMMAND (`scri migrate_attributes` —
    //  Scrinia.Commands.ScriniaCommands.MigrateAttributes)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MigrateAttributes_ForwardFillsLegacyEntry_FileHashesBecomeFieldsFile()
    {
        // Arrange: seed one legacy entry (FileHashes only, no Fields) directly via a FileMemoryStore
        // against the temp workspace — mirrors AttributeMigrationTests' seeding pattern. FileHashes
        // is the one member-sourced carrier Forward still folds (Task 4 trimmed tags/review/anchor).
        using (var seedStore = new FileMemoryStore(_tempDir))
        {
            seedStore.Upsert(
                new ArtifactEntry(
                    Name: "legacy-note", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
                    CreatedAt: DateTimeOffset.UnixEpoch,
                    FileHashes: new() { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" }),
                "local");
        }

        // Act
        var cmd = new ScriniaCommands();
        int exitCode = await cmd.MigrateAttributes(workspaceRoot: _tempDir, skipBackup: true, json: false);

        // Assert: exit 0, and a fresh store reload shows the entry gained Fields["file"]
        exitCode.Should().Be(0);
        using var freshStore = new FileMemoryStore(_tempDir);
        var entry = freshStore.LoadIndex("local").Single(e => e.Name == "legacy-note");
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!["file"].Should().BeEquivalentTo("src/A.cs", "src/B.cs");
    }
}
