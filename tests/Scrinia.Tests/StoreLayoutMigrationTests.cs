using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class StoreLayoutMigrationTests : IDisposable
{
    private readonly string _root;

    public StoreLayoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        string full = Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private bool Exists(string relative) =>
        File.Exists(Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Migrate_MovesEveryLegacyRootIntoMemories()
    {
        Write("store/notes.nmp2", "a");
        Write("store/notes.meta.json", "{}");
        Write("topics/findings/review.nmp2", "b");
        Write("topics/memory/api/auth.nmp2", "c");
        Write("topics/entity/skill/qa.nmp2", "d");
        Write("topics/agent/profile.nmp2", "e");
        Write("agent/profile.md", "f");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Ran.Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        Exists("memories/notes.meta.json").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/api/auth.nmp2").Should().BeTrue();
        Exists("memories/skill/qa.nmp2").Should().BeTrue();
        Exists("memories/agent/profile.nmp2").Should().BeTrue();
        Exists("memories/agent/profile.md").Should().BeTrue();

        // Moved, not copied.
        Exists("store/notes.nmp2").Should().BeFalse();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
        Directory.Exists(Path.Combine(_root, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void Migrate_PreservesVersionsSubdirectories()
    {
        Write("topics/findings/versions/review_20260801-010101.nmp2", "old");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);
        Exists("memories/findings/versions/review_20260801-010101.nmp2").Should().BeTrue();
    }

    [Fact]
    public void Migrate_Collision_NonNamespacedWins_LoserQuarantined()
    {
        Write("topics/api/auth.nmp2", "flat-wins");
        Write("topics/memory/api/auth.nmp2", "namespaced-loses");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Conflicts.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "memories", "api", "auth.nmp2"))
            .Should().Be("flat-wins");
        Directory.EnumerateDirectories(_root, ".scrinia-conflicts-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Migrate_DeletesTransientLockAndTmpFiles_NotRelocated()
    {
        Write("topics/findings/review.nmp2", "b");
        Write("topics/findings/.lock", "");
        Write("topics/findings/scratch.tmp", "partial-write");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/findings/.lock").Should().BeFalse();
        Exists("memories/findings/scratch.tmp").Should().BeFalse();
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "findings", ".lock")).Should().BeFalse();
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "findings", "scratch.tmp")).Should().BeFalse();
        result.Moved.Should().Be(1);
        Directory.Exists(Path.Combine(_root, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void Migrate_Collision_EntityBeforeMemory_Deterministic()
    {
        Write("topics/entity/skill/qa.nmp2", "entity-wins");
        Write("topics/memory/skill/qa.nmp2", "memory-loses");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Conflicts.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "memories", "skill", "qa.nmp2"))
            .Should().Be("entity-wins");
        Directory.EnumerateDirectories(_root, ".scrinia-conflicts-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunDoesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        var second = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: true);

        second.Ran.Should().BeFalse();
        second.Moved.Should().Be(0);
        second.BackupPath.Should().BeNull();
        Directory.EnumerateDirectories(_root, ".scrinia-backup-*").Should().BeEmpty();
    }

    [Fact]
    public void Migrate_DryRun_MovesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        var result = StoreLayoutMigration.Migrate(_root, dryRun: true, backup: false);

        result.Moved.Should().Be(1);
        Exists("topics/findings/review.nmp2").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void Migrate_Resumes_AfterPartialMigration()
    {
        Write("memories/findings/already.nmp2", "done");   // simulates interrupted run
        Write("topics/findings/pending.nmp2", "todo");

        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        Exists("memories/findings/already.nmp2").Should().BeTrue();
        Exists("memories/findings/pending.nmp2").Should().BeTrue();
    }

    [Fact]
    public void NeedsMigration_FalseWhenOnlyMemoriesExists()
    {
        Write("memories/findings/review.nmp2", "b");
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }

    [Fact]
    public void BothTriggers_ProduceIdenticalLayout()
    {
        Write("topics/findings/review.nmp2", "b");
        Write("store/notes.nmp2", "a");
        StoreLayoutMigration.MigrateIfNeeded(_root);

        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }
}
