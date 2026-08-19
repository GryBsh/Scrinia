using System.Text.Json;
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

    // Task 13 deleted PathRouter.ToFilesystemPath/ToMetadataPath — a second scope→directory
    // mapper with no production callers, which built its path from RAW segments and so disagreed
    // with ToStoreDir once the one scope constructor started validating. These four assertions
    // were rewritten (not dropped) against the resolvers the store actually uses:
    // ParseQualifiedName → ArtifactPath/MetaPath → GetStoreDirForScope → ToStoreDir. They now pin
    // the same v2 layout on the live path.

    private FileMemoryStore NewStore() => new(_tempDir);

    [Fact]
    public void V2Path_WritesToMemoriesDir()
    {
        using var store = NewStore();
        var (scope, subject) = store.ParseQualifiedName("/api/auth-flow");

        store.ArtifactPath(subject, scope)
            .Should().Be(Path.Combine(_tempDir, ".scrinia", "memories", "api", "auth-flow.nmp2"));
    }

    [Fact]
    public void V2Path_AgentWritesMd()
    {
        // Hand-authored file entities (skill, agent) live at the top level of .scrinia/, never
        // under memories/. This is PathRouter.TryToFileEntityPath's job — the live resolver
        // MemoryTools.Core/Skills call — see PathRouterTests for the dedicated coverage.
        PathRouter.TryToFileEntityPath("agent", "profile", _tempDir, out string path)
            .Should().BeTrue();
        path.Should().Be(Path.Combine(_tempDir, ".scrinia", "agent", "profile.md"));
    }

    [Fact]
    public void V2Path_DeepEntityPath_CreatesNestedDirs()
    {
        using var store = NewStore();
        var (scope, subject) = store.ParseQualifiedName("/goal/G-5/research/frontend");

        string artPath = store.ArtifactPath(subject, scope);
        artPath.Should().Be(Path.Combine(
            _tempDir, ".scrinia", "memories", "goal", "G-5", "research", "frontend.nmp2"));
        Path.GetDirectoryName(artPath)!.Should().EndWith(Path.Combine("goal", "G-5", "research"));
    }

    [Fact]
    public void V2Path_SidecarMetadata_WrittenCorrectly()
    {
        using var store = NewStore();
        var (scope, subject) = store.ParseQualifiedName("/api/auth-flow");

        string artPath = store.ArtifactPath(subject, scope);
        string metaPath = Path.ChangeExtension(artPath, ".meta.json");

        metaPath.Should().Be(
            Path.Combine(_tempDir, ".scrinia", "memories", "api", "auth-flow.meta.json"));
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
            int exit = await cmd.Migrate(root, dryRun: true, skipBackup: true);

            exit.Should().Be(0);
            File.Exists(Path.Combine(legacy, "review.nmp2")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, ".scrinia", "memories")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Creates a fresh workspace root (own temp dir, never reused across tests, so
    /// StoreLayoutMigration's "skip the backup if one already exists" guard cannot mask a
    /// backup assertion) with exactly one legacy, non-colliding file to migrate — one file
    /// moved, zero conflicts.</summary>
    private static string CreateLegacyWorkspaceRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        string legacy = Path.Combine(root, ".scrinia", "topics", "findings");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "review.nmp2"), "body");
        return root;
    }

    // These two pin the `backup: !skipBackup` inversion in ScriniaCommands.Migrate. Deleting
    // that `!` leaves every existing test green — the only other CLI-level test is a dry run,
    // and StoreLayoutMigration's dry-run branch returns before the backup block ever executes,
    // so it cannot observe the flag either way. Real (non-dry-run) invocations are required.

    [Fact]
    public async Task Migrate_ByDefault_WritesTheBackup()
    {
        string root = CreateLegacyWorkspaceRoot();
        try
        {
            var cmd = new ScriniaCommands();
            int exit = await cmd.Migrate(root); // no flags: the default shape

            exit.Should().Be(0);
            Directory.EnumerateDirectories(root, ".scrinia-backup-*").Should().NotBeEmpty(
                because: "skipBackup defaults to false, so the default `scri migrate` must " +
                         "pass backup: true through to StoreLayoutMigration and write a backup " +
                         "before moving files");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Migrate_SkipBackup_WritesNoBackup()
    {
        string root = CreateLegacyWorkspaceRoot();
        try
        {
            var cmd = new ScriniaCommands();
            int exit = await cmd.Migrate(root, skipBackup: true);

            exit.Should().Be(0);
            Directory.EnumerateDirectories(root, ".scrinia-backup-*").Should().BeEmpty(
                because: "skipBackup: true must suppress the pre-migration backup");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Runs <see cref="ScriniaCommands.Migrate"/> with <c>json: true</c> while
    /// capturing stdout, returning the exit code and the raw JSON text.</summary>
    private static async Task<(int Exit, string Json)> RunMigrateJson(ScriniaCommands cmd, params object?[] args)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        int exit;
        try
        {
            exit = args switch
            {
                [string root] => await cmd.Migrate(workspaceRoot: root, json: true),
                [string root, bool dryRun] => await cmd.Migrate(workspaceRoot: root, dryRun: dryRun, json: true),
                _ => throw new ArgumentException("unsupported arg shape", nameof(args)),
            };
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (exit, writer.ToString().Trim());
    }

    [Fact]
    public async Task Migrate_Json_MissingStore_ReportsErrorAndExitsNonZero()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            var (exit, json) = await RunMigrateJson(new ScriniaCommands(), root);

            exit.Should().Be(1);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("error").GetString().Should().Contain(".scrinia/");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Migrate_Json_DryRun_ReportsMovedAndConflictsWithoutSwapping()
    {
        // CliStoreMigrateOutput's constructor call is positional: `Moved` and `Conflicts` are
        // adjacent ints, `Ran`/`BackupSkipped`/`DryRun` are adjacent bools. A workspace with
        // exactly one legacy, non-colliding file makes moved=1 distinct from conflicts=0, and a
        // dry run makes ran=false distinct from dryRun=true — so a positional swap of either
        // pair fails this test instead of compiling silently into wrong JSON.
        string root = CreateLegacyWorkspaceRoot();
        try
        {
            var (exit, json) = await RunMigrateJson(new ScriniaCommands(), root, true);

            exit.Should().Be(0);
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;

            el.GetProperty("moved").GetInt32().Should().Be(1);
            el.GetProperty("conflicts").GetInt32().Should().Be(0);
            el.GetProperty("ran").GetBoolean().Should().BeFalse();
            el.GetProperty("dryRun").GetBoolean().Should().BeTrue();
            el.GetProperty("backupSkipped").GetBoolean().Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The `--json` branch is deliberately ONE emission placed after the migration runs, so that
    /// the nothing-to-migrate, dry-run and migrated outcomes cannot report different shapes. That
    /// property is only real if every outcome is covered: with just the dry-run and error tests, an
    /// early return added to any other branch leaves `--json` silently emitting nothing while still
    /// exiting 0. This test and the one below close the two uncovered outcomes.
    ///
    /// <para>Nothing-to-migrate is the common case in practice — most stores are already v3 — so a
    /// consumer scripting against `scri migrate --json` hits this path more than any other.</para>
    /// </summary>
    [Fact]
    public async Task Migrate_Json_NothingToMigrate_StillEmitsTheEnvelope()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        // .scrinia/ exists (so the command does not take its missing-store error path) but holds
        // nothing in the legacy layout, which is what "already v3" looks like on disk.
        Directory.CreateDirectory(Path.Combine(root, ".scrinia", "memories"));
        try
        {
            var (exit, json) = await RunMigrateJson(new ScriniaCommands(), root);

            exit.Should().Be(0);
            json.Should().NotBeEmpty(
                because: "--json must emit its envelope for every outcome, including this one");

            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            el.GetProperty("ran").GetBoolean().Should().BeFalse();
            el.GetProperty("moved").GetInt32().Should().Be(0);
            el.GetProperty("conflicts").GetInt32().Should().Be(0);
            el.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The remaining uncovered outcome: a real migration that moves a file. Distinct from
    /// the dry-run test in that <c>ran</c> is true and the files have actually moved, so a JSON
    /// branch that reported a dry run's numbers for a real migration would fail here.</summary>
    [Fact]
    public async Task Migrate_Json_RealMigration_ReportsRanAndMoved()
    {
        string root = CreateLegacyWorkspaceRoot();
        try
        {
            var (exit, json) = await RunMigrateJson(new ScriniaCommands(), root);

            exit.Should().Be(0);
            json.Should().NotBeEmpty();

            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            el.GetProperty("ran").GetBoolean().Should().BeTrue();
            el.GetProperty("moved").GetInt32().Should().Be(1);
            el.GetProperty("dryRun").GetBoolean().Should().BeFalse();
            el.GetProperty("backupSkipped").GetBoolean().Should().BeFalse();

            // The reported outcome must match the disk, not merely be internally consistent.
            Directory.Exists(Path.Combine(root, ".scrinia", "memories")).Should().BeTrue();
            Directory.EnumerateDirectories(root, ".scrinia-backup-*").Should().NotBeEmpty(
                because: "a real migration backs up by default, and the envelope says it was not skipped");
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
